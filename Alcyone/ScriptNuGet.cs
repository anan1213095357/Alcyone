using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StateMachine.Scripting;

internal sealed record ScriptPackage(string Id, string Version);
internal sealed record ScriptPackageAssets(string[] References, string[] Assemblies, Dictionary<string,string> Native)
{
    public static readonly ScriptPackageAssets Empty = new([],[],new(StringComparer.OrdinalIgnoreCase));
}
internal sealed class ScriptNuGet(string scriptDirectory, Action<string> progress)
{
    private static readonly NuGetFramework Target = NuGetFramework.ParseFolder("net10.0-windows");
    private static readonly FrameworkReducer Reducer = new();
    private static readonly Regex Directive = new("""^\s*(?:#r\s+"nuget:\s*([^",\r\n]+)(?:\s*,\s*([^"\r\n]+))?"|//\s*nuget:\s*([^,\r\n]+)(?:\s*,\s*([^\r\n]+))?)\s*$""",RegexOptions.Multiline|RegexOptions.IgnoreCase);
    private static readonly Dictionary<string,string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenQA.Selenium"]="Selenium.WebDriver", ["HtmlAgilityPack"]="HtmlAgilityPack",
        ["OfficeOpenXml"]="EPPlus", ["Microsoft.Playwright"]="Microsoft.Playwright"
    };
    private readonly SourceRepository _source = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
    private readonly string _cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Alcyone","NuGet");
    private readonly Dictionary<string,ScriptPackage> _auto = ReadAuto(scriptDirectory);
    private readonly Dictionary<string,NuGetVersion[]> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string?> _guesses = new(StringComparer.Ordinal);
    private static Dictionary<string,ScriptPackage> ReadAuto(string directory)
    {
        var path=Path.Combine(directory,"nuget.auto.json");
        try{return File.Exists(path)?JsonSerializer.Deserialize<Dictionary<string,ScriptPackage>>(File.ReadAllText(path))??new():new();}
        catch(JsonException){return new();}
    }
    public string Clean(string source, List<ScriptPackage> packages) => Directive.Replace(source,match =>
    {
        var id=(match.Groups[1].Success?match.Groups[1].Value:match.Groups[3].Value).Trim();
        var version=(match.Groups[2].Success?match.Groups[2].Value:match.Groups[4].Value).Trim();
        if(!Regex.IsMatch(id,"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$"))throw new InvalidOperationException("NuGet 包名无效："+id);
        if(version.Length>0 && !NuGetVersion.TryParse(version,out _))throw new InvalidOperationException("NuGet 版本无效："+version);
        packages.Add(new(id,version));
        return new string(match.Value.Select(c=>c is '\r' or '\n' ? c : ' ').ToArray()); // Keep diagnostics on the original source lines.
    });
    public void AddRemembered(IEnumerable<SyntaxTree> trees,List<ScriptPackage> roots)
    {
        var names=trees.SelectMany(t=>t.GetRoot().DescendantNodes()).Select(n=>n switch {
            UsingDirectiveSyntax u=>u.Name?.ToString(), QualifiedNameSyntax q=>q.ToString(), MemberAccessExpressionSyntax m=>m.ToString(), _=>null
        }).OfType<string>().Select(n=>n.Replace("global::","")).ToHashSet();
        foreach(var (name,package) in _auto)
            if(names.Contains(name) && !roots.Any(p=>p.Id.Equals(package.Id,StringComparison.OrdinalIgnoreCase)))roots.Add(package);
    }
    public async Task<bool> InferAsync(CSharpCompilation compilation, List<ScriptPackage> roots)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token=timeout.Token; var found=false;
        foreach(var tree in compilation.SyntaxTrees.Where(t=>!t.FilePath.EndsWith("__HotScriptGlobals.g.cs")))
        {
            var semantic=compilation.GetSemanticModel(tree);
            var nodes=tree.GetRoot();
            var names=nodes.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u=>u.Name is not null && semantic.GetSymbolInfo(u.Name).Symbol is null)
                .Select(u=>u.Name!.ToString()).ToList();
            foreach(var diagnostic in compilation.GetDiagnostics().Where(d=>d.Location.SourceTree==tree && d.Id is "CS0246" or "CS0234" or "CS0103" or "CS1069"))
            {
                var node=nodes.FindNode(diagnostic.Location.SourceSpan,getInnermostNodeForTie:true);
                var qualified=node.AncestorsAndSelf().FirstOrDefault(n=>n is QualifiedNameSyntax or MemberAccessExpressionSyntax);
                if(qualified is not null && qualified.ToString().Contains('.'))
                {
                    var first=qualified.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault();
                    if(first is not null && char.IsUpper(first.Identifier.ValueText.FirstOrDefault()) && semantic.GetSymbolInfo(first).Symbol is null)
                        names.Add(qualified.ToString());
                }
            }
            foreach(var candidate in names.Distinct())
            {
                var name=candidate.Replace("global::","");
                if(name.StartsWith("StateMachine",StringComparison.Ordinal))continue;
                var id=await GuessAsync(name,token);
                if(id is null || roots.Any(p=>p.Id.Equals(id,StringComparison.OrdinalIgnoreCase)))continue;
                progress("检测到缺少库 "+name+"，自动匹配 NuGet："+id);
                var version=await SelectVersion(id,[],true,token);
                var package=new ScriptPackage(id,version.ToNormalizedString());roots.Add(package);_auto[name]=package;found=true;
            }
        }
        return found;
    }
    private async Task<string?> GuessAsync(string name,CancellationToken token)
    {
        if(_guesses.TryGetValue(name,out var guessed))return guessed;
        var alias=Aliases.FirstOrDefault(p=>name==p.Key || name.StartsWith(p.Key+".",StringComparison.Ordinal));
        if(alias.Key is not null)return _guesses[name]=alias.Value;
        var candidate=name;
        while(!string.IsNullOrEmpty(candidate) && candidate is not ("System" or "Microsoft" or "StateMachine"))
        {
            if((await Versions(candidate,token)).Length>0)return _guesses[name]=candidate;
            var dot=candidate.LastIndexOf('.'); if(dot<0)break;candidate=candidate[..dot];
        }
        if(name=="System" || name=="Microsoft")return _guesses[name]=null;
        var search=await _source.GetResourceAsync<PackageSearchResource>(token) ?? throw new InvalidOperationException("NuGet 搜索服务不可用。");
        var results=(await search.SearchAsync(name,new SearchFilter(false),0,10,NullLogger.Instance,token)).Select(p=>p.Identity.Id).Distinct().ToArray();
        if(results.Length>0)progress("无法唯一匹配 "+name+"。候选："+string.Join("、",results.Take(4))+"；可用 #r \"nuget: 包名, 版本\" 明确依赖。");
        return _guesses[name]=null;
    }
    private async Task<NuGetVersion[]> Versions(string id,CancellationToken token)
    {
        if(_versions.TryGetValue(id,out var versions))return versions;
        var resource=await _source.GetResourceAsync<FindPackageByIdResource>(token) ?? throw new InvalidOperationException("NuGet 下载服务不可用。");
        using var cache=new SourceCacheContext();
        return _versions[id]=(await resource.GetAllVersionsAsync(id,cache,NullLogger.Instance,token)).ToArray();
    }
    private async Task<NuGetVersion> SelectVersion(string id,List<VersionRange> ranges,bool latest,CancellationToken token)
    {
        var available=(await Versions(id,token)).Where(v=>ranges.All(r=>r.Satisfies(v))).ToArray();
        var stable=available.Where(v=>!v.IsPrerelease).ToArray();
        if(stable.Length>0)available=stable;
        var result=latest?available.OrderDescending().FirstOrDefault():available.Order().FirstOrDefault();
        return result??throw new InvalidOperationException("NuGet 依赖不存在或版本冲突："+id+" "+string.Join(" / ",ranges));
    }
    private string PackagePath(string id,NuGetVersion version) => Path.Combine(_cache,id.ToLowerInvariant(),version.ToNormalizedString().ToLowerInvariant(),"package.nupkg");
    private async Task<string> Download(string id,NuGetVersion version,CancellationToken token)
    {
        var path=PackagePath(id,version);if(File.Exists(path))return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        progress("正在下载 NuGet："+id+" "+version.ToNormalizedString());
        var resource=await _source.GetResourceAsync<FindPackageByIdResource>(token) ?? throw new InvalidOperationException("NuGet 下载服务不可用。");
        var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=File.Create(temporary))using(var cache=new SourceCacheContext())
                if(!await resource.CopyNupkgToStreamAsync(id,version,stream,cache,NullLogger.Instance,token))throw new InvalidOperationException("NuGet 包不存在："+id+" "+version);
            File.Move(temporary,path,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        return path;
    }
    private static FrameworkSpecificGroup? Nearest(IEnumerable<FrameworkSpecificGroup> groups)
    {
        var items=groups.ToArray();var framework=Reducer.GetNearest(Target,items.Select(g=>g.TargetFramework));
        return framework is null?null:items.First(g=>g.TargetFramework.Equals(framework));
    }
    public async Task<ScriptPackageAssets> RestoreAsync(List<ScriptPackage> requested)
    {
        if(requested.Count==0)return ScriptPackageAssets.Empty;
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));var token=timeout.Token;
        var roots=new Dictionary<string,NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        foreach(var package in requested)
        {
            var version=string.IsNullOrEmpty(package.Version)?await SelectVersion(package.Id,[],true,token):NuGetVersion.Parse(package.Version);
            if(roots.TryGetValue(package.Id,out var prior) && prior!=version)throw new InvalidOperationException("脚本引用了不同版本的 NuGet 包："+package.Id);
            roots[package.Id]=version;
        }
        var signature=string.Join(";",roots.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key.ToLowerInvariant()+"@"+p.Value.ToNormalizedString()));
        var lockPath=Path.Combine(scriptDirectory,"nuget.lock.json");
        try
        {
            if(File.Exists(lockPath))
            {
                var locked=JsonSerializer.Deserialize<PackageLock>(File.ReadAllText(lockPath));
                if(locked?.Signature==signature && locked.Assets.References.Concat(locked.Assets.Assemblies).Concat(locked.Assets.Native.Values).All(File.Exists))return locked.Assets;
            }
        }
        catch(JsonException){}
        progress("正在解析 NuGet 依赖…");
        var selected=new Dictionary<string,NuGetVersion>(roots,StringComparer.OrdinalIgnoreCase);
        var completed=false;
        for(var pass=0;pass<20;pass++)
        {
            var constraints=roots.ToDictionary(p=>p.Key,p=>new List<VersionRange>{new(p.Value,true,p.Value,true)},StringComparer.OrdinalIgnoreCase);
            var queue=new Queue<string>(roots.Keys);var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var changed=false;
            while(queue.TryDequeue(out var id))
            {
                if(visited.Count>150)throw new InvalidOperationException("NuGet 依赖过多。");
                var ranges=constraints[id];
                if(!selected.TryGetValue(id,out var version)||!ranges.All(r=>r.Satisfies(version)))
                {version=await SelectVersion(id,ranges,false,token);selected[id]=version;changed=true;}
                if(!visited.Add(id+"@"+version))continue;
                using var reader=new PackageArchiveReader(await Download(id,version,token));
                var groups=reader.GetPackageDependencies().ToArray();
                var nearest=Reducer.GetNearest(Target,groups.Select(g=>g.TargetFramework));
                if(nearest is null && groups.Length>0)throw new InvalidOperationException("NuGet 包不支持当前 .NET 10 Windows："+id);
                foreach(var dependency in groups.FirstOrDefault(g=>g.TargetFramework.Equals(nearest))?.Packages??[])
                {
                    if(!constraints.TryGetValue(dependency.Id,out var required))constraints[dependency.Id]=required=[];
                    required.Add(dependency.VersionRange);queue.Enqueue(dependency.Id);
                }
            }
            foreach(var id in selected.Keys.Where(id=>!constraints.ContainsKey(id)).ToArray())selected.Remove(id);
            if(selected.Any(p=>!constraints[p.Key].All(r=>r.Satisfies(p.Value))))continue;
            if(!changed){completed=true;break;}
        }
        if(!completed)throw new InvalidOperationException("NuGet 依赖版本无法收敛，请指定兼容版本。");
        var references=new List<string>();var assemblies=new List<string>();var native=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var (id,version) in selected)
        {
            var archive=await Download(id,version,token);using var reader=new PackageArchiveReader(archive);
            string Extract(string item)
            {
                var name=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item)))[..12]+"-"+Path.GetFileName(item);
                var path=Path.Combine(Path.GetDirectoryName(archive)!,"assets",name);
                if(!File.Exists(path)){Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var input=reader.GetStream(item);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{using(var output=File.Create(temp))input.CopyTo(output);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}}
                return path;
            }
            var files=reader.GetFiles().ToArray();
            var rids=new[]{"win-x64","win","any"};
            var runtime=rids.Select(rid=>Nearest(files.Where(f=>f.StartsWith("runtimes/"+rid+"/lib/",StringComparison.OrdinalIgnoreCase)).GroupBy(f=>f.Split('/')[3]).Select(g=>new FrameworkSpecificGroup(NuGetFramework.ParseFolder(g.Key),g)))).FirstOrDefault(g=>g is not null);
            var lib=Nearest(reader.GetLibItems());
            var reference=Nearest(files.Where(f=>f.StartsWith("ref/",StringComparison.OrdinalIgnoreCase)).GroupBy(f=>f.Split('/')[1]).Select(g=>new FrameworkSpecificGroup(NuGetFramework.ParseFolder(g.Key),g)));
            foreach(var file in (reference??lib??runtime)?.Items.Where(f=>f.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))??[])references.Add(Extract(file));
            foreach(var file in (runtime??lib)?.Items.Where(f=>f.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))??[])assemblies.Add(Extract(file));
            foreach(var rid in rids.Reverse())
                foreach(var file in files.Where(f=>f.StartsWith("runtimes/"+rid+"/native/",StringComparison.OrdinalIgnoreCase)))native[Path.GetFileName(file)]=Extract(file);
        }
        var assets=new ScriptPackageAssets(references.Distinct().ToArray(),assemblies.Distinct().ToArray(),native);
        File.WriteAllText(lockPath,JsonSerializer.Serialize(new PackageLock(signature,assets)));
        progress("NuGet 依赖已就绪，正在重新编译…");return assets;
    }
    public void Remember() => File.WriteAllText(Path.Combine(scriptDirectory,"nuget.auto.json"),JsonSerializer.Serialize(_auto));
    private sealed record PackageLock(string Signature,ScriptPackageAssets Assets);
}
