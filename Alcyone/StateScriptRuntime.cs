using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using StateMachine.Automation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StateMachine.Scripting;

public interface IStateScriptApi
{
    IServiceProvider Services { get; }
    StateVariables Vars { get; }
    DesktopInput Input { get; }
    ColorProbeApi Color { get; }
    ColorProbeApi Recognition { get; }
    CancellationToken CancellationToken { get; }
    T Service<T>() where T : notnull;
    JsonNode? GetNode(string name);
    void SetNode(string name, JsonNode? value);
    void Log(string message, string type = "SCRIPT", string level = "info");
    Task Delay(int milliseconds, CancellationToken cancellationToken = default);
}

public sealed class StateScriptContext : IStateScriptApi
{
    private readonly Func<string, JsonNode?> _getVariable;
    private readonly Action<string, JsonNode?> _setVariable;
    private readonly Action<string, string, string> _log;
    private readonly Func<CancellationToken> _token;

    public StateScriptContext(
        IServiceProvider services,
        Func<string, JsonNode?> getVariable,
        Action<string, JsonNode?> setVariable,
        Action<string, string, string> log,
        Func<CancellationToken>? cancellationToken = null,
        Func<string, ColorProbeSettings?>? colorSettings = null,
        Func<string, ColorProbeResult?>? lastColorResult = null)
    {
        Services = services;
        _getVariable = getVariable;
        _setVariable = setVariable;
        _log = log;
        _token = cancellationToken ?? (() => default);
        Vars = new StateVariables(getVariable, (name, value) =>
        {
            _token().ThrowIfCancellationRequested();
            setVariable(name, value);
        });
        Input = new DesktopInput(_token);
        Color = new ColorProbeApi(colorSettings ?? (_ => null), lastColorResult ?? (_ => null), _token,
            services.GetService(typeof(IColorProbeScanner)) as IColorProbeScanner);
    }

    public IServiceProvider Services { get; }
    public StateVariables Vars { get; }
    public DesktopInput Input { get; }
    public ColorProbeApi Color { get; }
    public ColorProbeApi Recognition => Color;
    public CancellationToken CancellationToken => _token();

    public T Service<T>() where T : notnull
    {
        var service = Services.GetService(typeof(T));
        return service is T typed
            ? typed
            : throw new InvalidOperationException($"服务未注册：{typeof(T).FullName}");
    }

    public JsonNode? GetNode(string name) => CloneNode(_getVariable(name));

    public void SetNode(string name, JsonNode? value) => Vars.Set(name, value);

    public void Log(string message, string type = "SCRIPT", string level = "info") =>
        _log(type, message, level);

    public async Task Delay(int milliseconds, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token(), cancellationToken);
        await Task.Delay(Math.Max(0, milliseconds), linked.Token);
    }

    internal static JsonNode? CloneNode(JsonNode? node) => node?.DeepClone();
}

public sealed class StateVariables
{
    private readonly Func<string, JsonNode?> _get;
    private readonly Action<string, JsonNode?> _set;

    internal StateVariables(
        Func<string, JsonNode?> get,
        Action<string, JsonNode?> set)
    {
        _get = get;
        _set = set;
    }

    public T? Get<T>(string name, T? defaultValue = default)
    {
        var node = _get(name);
        if (node is null)
            return defaultValue;

        try
        {
            return node.Deserialize<T>();
        }
        catch
        {
            return defaultValue;
        }
    }

    public JsonNode? Get(string name)
    {
        return StateScriptContext.CloneNode(_get(name));
    }

    public void Set<T>(string name, T value)
    {
        _set(name, JsonSerializer.SerializeToNode(value));
    }

    public void Set(string name, JsonNode? value)
    {
        _set(name, StateScriptContext.CloneNode(value));
    }
}

public abstract class StateScript
{
    private StateScriptContext? _context;

    internal void Attach(StateScriptContext context) => _context = context;

    protected StateScriptContext Api =>
        _context ?? throw new InvalidOperationException("脚本上下文尚未初始化。");

    protected StateVariables Vars => Api.Vars;

    protected T Service<T>() where T : notnull => Api.Service<T>();

    protected void Log(string message, string type = "SCRIPT", string level = "info") =>
        Api.Log(message, type, level);
}

public sealed record ScriptReloadResult(
    bool Success,
    int ScriptCount,
    int ActionClassCount,
    IReadOnlyList<string> Errors)
{
    public static ScriptReloadResult Ok(int scripts, int classes) =>
        new(true, scripts, classes, Array.Empty<string>());

    public static ScriptReloadResult Fail(int scripts, IEnumerable<string> errors) =>
        new(false, scripts, 0, errors.ToArray());
}

public sealed class StateScriptHost : IDisposable
{
    public bool SupportsScriptDebugging { get; private set; }
    private const string ScriptHeader = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Text.Json;
        global using System.Text.Json.Nodes;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using StateMachine.Scripting;
        global using StateCondition = StateMachine.Scripting.Metadata.StateConditionAttribute;
        global using StateAction = StateMachine.Scripting.Metadata.StateActionAttribute;
        global using StateParameter = StateMachine.Scripting.Metadata.StateParameterAttribute;
        """;

    private readonly StateScriptContext _context;
    private readonly object _reloadLock = new();
    private readonly ScriptNuGet _nuget;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _reloadTimer;
    private IReadOnlyList<object> _actionTargets = Array.Empty<object>();
    private ScriptLoadContext? _loadContext;
    private bool _disposed;

    public StateScriptHost(StateScriptContext context, string? scriptDirectory = null)
    {
        _context = context;
        ScriptDirectory = scriptDirectory
            ?? Environment.GetEnvironmentVariable("STATE_MACHINE_SCRIPT_DIR")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "HotScripts");

        Directory.CreateDirectory(ScriptDirectory);
        _nuget = new ScriptNuGet(ScriptDirectory,message=>DependencyProgress?.Invoke(message));
        Directory.CreateDirectory(Path.Combine(ScriptDirectory, "lib"));

        _reloadTimer = new Timer(_ => ReloadFromWatcher(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(ScriptDirectory)
        {
            Filter = "*.csx",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnScriptFileChanged;
        _watcher.Created += OnScriptFileChanged;
        _watcher.Deleted += OnScriptFileChanged;
        _watcher.Renamed += OnScriptFileRenamed;
    }

    public string ScriptDirectory { get; }
    public IReadOnlyList<object> ActionTargets => Volatile.Read(ref _actionTargets);
    public event Action<ScriptReloadResult>? Reloaded;
    public event Action<string>? DependencyProgress;

    public ScriptReloadResult Reload()
    {
        var files = Directory
            .EnumerateFiles(ScriptDirectory, "*.csx", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sources = files.Select(path => new ScriptSource(path, File.ReadAllText(path, Encoding.UTF8))).ToArray();
        return CompileAndSwap(sources);
    }

    public ScriptReloadResult ReloadFromText(string code, string virtualFileName = "memory.csx") =>
        CompileAndSwap(new[] { new ScriptSource(virtualFileName, code ?? string.Empty) });

    private ScriptReloadResult CompileAndSwap(IReadOnlyList<ScriptSource> sources)
    {
        lock (_reloadLock)
        {
            if (_disposed)
                return ScriptReloadResult.Fail(sources.Count, new[] { "脚本宿主已经释放。" });

            if (sources.Count == 0)
            {
                Swap(Array.Empty<object>(), null);
                SupportsScriptDebugging = false;
                var emptyResult = ScriptReloadResult.Ok(0, 0);
                Reloaded?.Invoke(emptyResult);
                return emptyResult;
            }

            CSharpCompilation compilation;
            ScriptPackageAssets packages;
            try { (compilation,packages) = PrepareCompilation(sources); }
            catch(Exception ex)
            {
                var failed=ScriptReloadResult.Fail(sources.Count,["NuGet 自动恢复失败："+ex.GetBaseException().Message]);
                Reloaded?.Invoke(failed);return failed;
            }

            var debuggingAvailable = false;
            // Instrument valid scripts only; unsupported syntax keeps the original executable.
            if (!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                try
                {
                    var traced = ScriptTraceInstrumentation.Apply(compilation);
                    if (!traced.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) { debuggingAvailable = !ReferenceEquals(compilation, traced); compilation = traced; }
                    else DependencyProgress?.Invoke("部分脚本语法暂不支持执行追踪，已保留原始编译。");
                }
                catch (Exception ex) { DependencyProgress?.Invoke("执行追踪未启用：" + ex.Message); }
            }
            using var pe = new MemoryStream();
            using var pdb = new MemoryStream();
            var emit = compilation.Emit(pe, pdb,
                options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
                    debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb));

            if (!emit.Success)
            {
                var errors = emit.Diagnostics
                    .Where(x => x.Severity == DiagnosticSeverity.Error)
                    .Select(FormatDiagnostic)
                    .Distinct()
                    .ToArray();

                var failed = ScriptReloadResult.Fail(sources.Count, errors);
                Reloaded?.Invoke(failed);
                return failed;
            }

            pe.Position = 0;
            pdb.Position = 0;

            ScriptLoadContext? newLoadContext = null;
            try
            {
                newLoadContext = new ScriptLoadContext(Path.Combine(ScriptDirectory, "lib"),packages);
                var assembly = newLoadContext.LoadFromStream(pe, pdb);

                var targets = assembly
                    .GetTypes()
                    .Where(type =>
                        typeof(StateScript).IsAssignableFrom(type) &&
                        !type.IsAbstract &&
                        !type.IsInterface)
                    .Select(type => Activator.CreateInstance(type)
                        ?? throw new InvalidOperationException($"无法创建脚本类：{type.FullName}"))
                    .Cast<StateScript>()
                    .ToArray();

                foreach (var target in targets)
                    target.Attach(_context);

                _nuget.Remember();
                Swap(targets.Cast<object>().ToArray(), newLoadContext);
                SupportsScriptDebugging = debuggingAvailable;
                newLoadContext = null;

                var ok = ScriptReloadResult.Ok(sources.Count, targets.Length);
                Reloaded?.Invoke(ok);
                return ok;
            }
            catch (Exception ex)
            {
                newLoadContext?.Unload();
                var failed = ScriptReloadResult.Fail(sources.Count, new[] { ex.GetBaseException().Message });
                Reloaded?.Invoke(failed);
                return failed;
            }
        }
    }

    private (CSharpCompilation Compilation,ScriptPackageAssets Assets) PrepareCompilation(IReadOnlyList<ScriptSource> sources)
    {
        var options=new CSharpParseOptions(LanguageVersion.Latest,kind:SourceCodeKind.Regular);
        var roots=new List<ScriptPackage>();
        var trees=new List<SyntaxTree>{CSharpSyntaxTree.ParseText(ScriptHeader,options,"__HotScriptGlobals.g.cs",Encoding.UTF8)};
        foreach(var source in sources)trees.Add(CSharpSyntaxTree.ParseText(_nuget.Clean(source.Code,roots),options,source.Path,Encoding.UTF8));
        _nuget.AddRemembered(trees,roots);
        var assets=Task.Run(()=>_nuget.RestoreAsync(roots)).GetAwaiter().GetResult();
        CSharpCompilation Compile() => CSharpCompilation.Create($"StateMachine.HotScripts.{Guid.NewGuid():N}",trees,BuildReferences(assets.References),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,optimizationLevel:OptimizationLevel.Release,allowUnsafe:false,nullableContextOptions:NullableContextOptions.Enable));
        var compilation=Compile();
        CSharpCompilation ImportAttributes(CSharpCompilation current)
        {
            var imports = ScriptAttributeImports.Resolve(current, assets.References);
            if (imports.Length == 0) return current;
            trees.Add(CSharpSyntaxTree.ParseText(string.Join("\n", imports), options, $"__HotScriptImports{trees.Count}.g.cs", Encoding.UTF8));
            DependencyProgress?.Invoke("自动补全 NuGet 属性引用：" + string.Join("、", imports));
            return Compile();
        }
        compilation = ImportAttributes(compilation);
        for(var attempt=0;attempt<3 && compilation.GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error);attempt++)
        {
            if(!Task.Run(()=>_nuget.InferAsync(compilation,roots)).GetAwaiter().GetResult())break;
            assets=Task.Run(()=>_nuget.RestoreAsync(roots)).GetAwaiter().GetResult();compilation=ImportAttributes(Compile());
        }
        return (compilation,assets);
    }
    private IEnumerable<MetadataReference> BuildReferences(IEnumerable<string> packageReferences)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                paths.Add(path);
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location) || AssemblyLoadContext.GetLoadContext(assembly)!=AssemblyLoadContext.Default) continue;
            paths.Add(assembly.Location);
        }

        var libDirectory = Path.Combine(ScriptDirectory, "lib");
        if (Directory.Exists(libDirectory))
        {
            foreach (var dll in Directory.EnumerateFiles(libDirectory, "*.dll", SearchOption.TopDirectoryOnly))
                paths.Add(dll);
        }

        // A package can supply a newer version of an assembly already used by the host.
        var resolved=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in paths.Concat(packageReferences).Where(File.Exists))
        {
            try { resolved[AssemblyName.GetAssemblyName(path).Name!]=path; }
            catch(BadImageFormatException) { }
        }
        return resolved.Values.Select(path=>MetadataReference.CreateFromFile(path));
    }

    private static string FormatDiagnostic(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var line = span.StartLinePosition.Line + 1;
        var column = span.StartLinePosition.Character + 1;
        var file = string.IsNullOrWhiteSpace(span.Path) ? "script" : Path.GetFileName(span.Path);
        return $"{file}({line},{column}) {diagnostic.Id}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}";
    }

    private void Swap(IReadOnlyList<object> targets, ScriptLoadContext? loadContext)
    {
        var oldContext = _loadContext;
        Volatile.Write(ref _actionTargets, targets);
        _loadContext = loadContext;
        oldContext?.Unload();
    }

    private void OnScriptFileChanged(object sender, FileSystemEventArgs e) =>
        _reloadTimer.Change(250, Timeout.Infinite);

    private void OnScriptFileRenamed(object sender, RenamedEventArgs e) =>
        _reloadTimer.Change(250, Timeout.Infinite);

    private void ReloadFromWatcher()
    {
        if (_disposed) return;
        try
        {
            Reload();
        }
        catch (Exception ex)
        {
            Reloaded?.Invoke(ScriptReloadResult.Fail(0, new[] { ex.GetBaseException().Message }));
        }
    }

    public void Dispose()
    {
        ScriptTrace.CloseDebug(ScriptDirectory);
        lock (_reloadLock)
        {
            if (_disposed) return;
            _disposed = true;

            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnScriptFileChanged;
            _watcher.Created -= OnScriptFileChanged;
            _watcher.Deleted -= OnScriptFileChanged;
            _watcher.Renamed -= OnScriptFileRenamed;
            _watcher.Dispose();
            _reloadTimer.Dispose();

            Volatile.Write(ref _actionTargets, Array.Empty<object>());
            _loadContext?.Unload();
            _loadContext = null;
        }
    }

    private sealed record ScriptSource(string Path, string Code);

    private sealed class ScriptLoadContext : AssemblyLoadContext
    {
        private readonly string _libDirectory;

        private readonly Dictionary<string,string> _packages;
        private readonly Dictionary<string,string> _native;
        public ScriptLoadContext(string libDirectory,ScriptPackageAssets packages) : base(isCollectible: true)
        {
            _libDirectory=libDirectory;
            _packages=packages.Assemblies.GroupBy(path=> {
                var name=AssemblyName.GetAssemblyName(path);return name.Name+"|"+name.CultureName;
            },StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.First(),StringComparer.OrdinalIgnoreCase);
            _native=packages.Native;
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            if(_native.TryGetValue(name,out var path)||_native.TryGetValue(name+".dll",out path))return LoadUnmanagedDllFromPath(path);
            return 0;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(x =>
                AssemblyName.ReferenceMatchesDefinition(x.GetName(), assemblyName));
            if(_packages.TryGetValue(assemblyName.Name+"|"+assemblyName.CultureName,out var package))
            {
                if(shared is not null && shared.GetName().Version==AssemblyName.GetAssemblyName(package).Version)return shared;
                return LoadFromAssemblyPath(package);
            }
            if (shared is not null) return shared;

            var path = Path.Combine(_libDirectory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
