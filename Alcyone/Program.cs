using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Photino.NET;
using Microsoft.AspNetCore.DataProtection;
using StateMachine.Components;
using StateMachine.Automation;

namespace StateMachine;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopInput.InitializeDpiAwareness();
        // Single-file assemblies are extracted elsewhere; editable files stay beside the exe.
        var desktopDirectory = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath),
            "dotnet", StringComparison.OrdinalIgnoreCase)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(Environment.ProcessPath!)!;
        var verifyHost = args.Contains("--check-host", StringComparer.Ordinal);
        var browserHost = args.Contains("--browser-host", StringComparer.Ordinal);
        var bundledSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var desktopSettings = Path.Combine(desktopDirectory, "appsettings.json");
        if (File.Exists(bundledSettings) && !File.Exists(desktopSettings))
            File.Copy(bundledSettings, desktopSettings);
        foreach (var directory in new[] { "HotScripts", "StateMachineConfigs" })
        {
            var source = Path.Combine(AppContext.BaseDirectory, directory);
            var destination = Path.Combine(desktopDirectory, directory);
            if (!Directory.Exists(source) || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args.Where(argument => argument is not ("--check-host" or "--browser-host")).ToArray(),
            ContentRootPath = desktopDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.Logging.ClearProviders();
        if (verifyHost || browserHost) builder.Logging.AddSimpleConsole();
        // The local webview session ends with this process; no persisted browser cookies are needed.
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRazorComponents().AddInteractiveServerComponents()
            .AddHubOptions(options => options.MaximumReceiveMessageSize = 96 * 1024 * 1024);
        builder.Services.AddSingleton<StateMachine.Training.TrainingSessions>();
        builder.Services.AddScoped<StateMachine.Training.TrainingSession>();
        builder.Services.AddSingleton<FastColorFinder.Native.NativeRegionSelector>();
        builder.Services.AddSingleton<StateMachine.Training.IRecognitionRegionSelector, StateMachine.Training.RecognitionRegionSelector>();
        builder.Services.AddSingleton<FastColorFinder.Services.TextDictionaryDownloads>();
        builder.Services.AddSingleton<IColorProbeScanner, DesktopColorProbeScanner>();
        builder.Services.AddSingleton<MachineRuntimeService>();
        var app = builder.Build();
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.MapGet("/api/training/{id}/{kind}", (string id, string kind, StateMachine.Training.TrainingSessions sessions) =>
        {
            if (kind is not ("color" or "text")) return Results.NotFound();
            try
            {
                var png = sessions.Frame(id, kind == "text");
                return png is null ? Results.NoContent() : Results.File(png, "image/png");
            }
            catch (Exception ex) { return Results.Problem(detail: ex.Message, statusCode: 503); }
        });
        app.MapGet("/FastTextSearch.cs.txt", () => Results.File(
            Path.Combine(AppContext.BaseDirectory, "Core", "FastTextSearch.cs"), "text/plain", "FastTextSearch.cs"));
        app.MapGet("/api/text/dictionaries/download/{id}", (string id, FastColorFinder.Services.TextDictionaryDownloads downloads) =>
        {
            var file = downloads.Get(id);
            return file is null ? Results.NotFound() : Results.File(file.Bytes, "text/plain; charset=utf-8", file.FileName);
        });
        app.MapStaticAssets(Path.Combine(AppContext.BaseDirectory, "Alcyone.staticwebassets.endpoints.json"));
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            var url = addresses?.FirstOrDefault(address => address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("无法启动本地桌面界面。");
            if (verifyHost)
            {
                VerifyScripts(app.Services, desktopDirectory);
                VerifyHostAsync(url).GetAwaiter().GetResult();
                return;
            }
            if (browserHost)
            {
                Console.WriteLine("State machine browser host: " + url);
                app.WaitForShutdownAsync().GetAwaiter().GetResult();
                return;
            }
            new PhotinoWindow()
                .SetTitle("Alcyone")
                .SetUseOsDefaultSize(false)
                .SetMaximized(true)
                .SetResizable(true)
                .SetMinSize(1366,768)
                .SetContextMenuEnabled(false)
#if DEBUG
                .SetDevToolsEnabled(true)
#else
                .SetDevToolsEnabled(false)
#endif
                .Load(url)
                .WaitForClose();
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void VerifyScripts(IServiceProvider services, string desktopDirectory)
    {
        var context = new StateMachine.Scripting.StateScriptContext(services, _ => null, (_, _) => { }, (_, _, _) => { });
        using var host = new StateMachine.Scripting.StateScriptHost(context, Path.Combine(desktopDirectory, "HotScripts"));
        var result = host.Reload();
        if (!result.Success) throw new InvalidOperationException("Published scripts failed: " + string.Join(" | ", result.Errors));
        Console.WriteLine("Desktop hot scripts verified.");
    }

    private static async Task VerifyHostAsync(string url)
    {
        using var client = new HttpClient { BaseAddress = new Uri(url) };
        var html = await client.GetStringAsync("/");
        if (!html.Contains("Alcyone", StringComparison.Ordinal)) throw new InvalidOperationException("桌面页面未加载。");
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(html, "(?:src|href)=\"([^\"]+\\.(?:css|js))\""))
        {
            using var response = await client.GetAsync(match.Groups[1].Value);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"UI asset unavailable: {match.Groups[1].Value} ({response.StatusCode})");
        }
        Console.WriteLine("Desktop host and UI assets verified.");
        var training = await client.GetStringAsync("/recognition-training");
        if (!training.Contains("training/annotation.js") || training.Contains("href=\"lib/bootstrap"))
            throw new InvalidOperationException("训练界面未正确隔离样式。");
        foreach (var asset in new[] { "training/app.css", "training/text-finder.css", "training/integration.css", "training/annotation.js", "training/text-annotation.js", "training/bridge.js", "FastTextSearch.cs.txt", "FastColorFinder.cs.txt" })
            await client.GetStringAsync(asset);
        Console.WriteLine("Embedded training page and assets verified.");
    }
}
