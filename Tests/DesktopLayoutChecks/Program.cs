using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using StateMachine;
using StateMachine.Automation;
using StateMachine.Execution;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "Alcyone.LayoutChecks." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var preferencePath = Path.Combine(root, "desktop.json");
var preferences = new DesktopPreferences(preferencePath);
preferences.RememberDesktop(true, "saved-config"); preferences.SetRestore(false);
var restored = new DesktopPreferences(preferencePath);
Check(restored.DesktopEnabled && !restored.RestoreDesktop && restored.ConfigKey == "saved-config", "Preferences survive restart");
restored.RememberDesktop(false);
Check(!new DesktopPreferences(preferencePath).DesktopEnabled, "Stopping desktop is persisted");
var machine = new MachineModel
{
    States = [new() { Id = "a", X = 100, Y = 100 }, new() { Id = "b", X = 250, Y = 100 }],
    StateGroups = [new() { Id = "g", StateIds = ["a", "b"], X = 80, Y = 80 }]
};
using var services = new ServiceCollection().BuildServiceProvider();
await using (var session = new MachineSession(services, new EnvironmentStub(root), new DesktopColorProbeScanner(), machine))
{
    await session.SetVariableAsync("sentinel", System.Text.Json.Nodes.JsonValue.Create(42));
    var path = Path.Combine(root, "config.json");
    var groups = session.Snapshot.Machine.StateGroups.Select(g => new StateGroupModel { Id = g.Id, StateIds = g.StateIds.ToList(), X = 180, Y = 130 }).ToList();
    await session.UpdateCanvasLayoutAsync(path, 0, [], groups);
    Check(session.Snapshot.Machine.States[0].X == 200 && session.Snapshot.Machine.States[1].X == 350, "Planet movement preserves member offsets");
    Check(session.Snapshot.Runtime.Variables["sentinel"]!.GetValue<int>() == 42, "Layout editing does not reset runtime");
    groups[0].IsCollapsed = false;
    await session.UpdateCanvasLayoutAsync(path, 1, [], groups);
    var saved = JsonSerializer.Deserialize<MachineModel>(File.ReadAllText(path), options)!;
    Check(!saved.StateGroups[0].IsCollapsed && saved.States[0].X == 200, "Expanded state and positions are saved");
    try { await session.UpdateCanvasLayoutAsync(path, 0, [new("a", 999, 999)]); throw new Exception("Stale edit was accepted"); }
    catch (InvalidOperationException) { }
    Check(session.Snapshot.Machine.States[0].X == 200, "Concurrent stale edits cannot overwrite layout");
    await session.UpdateCanvasLayoutAsync(path, 2, [new("a", double.NaN, 400), new("b", -5, 200)]);
    Check(session.Snapshot.Machine.States[0].X == 200 && session.Snapshot.Machine.States[1].X == 0, "Invalid positions are ignored and bounds enforced");
}
Console.WriteLine("All desktop preference/layout checks passed.");
static void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS: " + message); }
sealed class EnvironmentStub(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Alcyone";
    public string EnvironmentName { get; set; } = "Testing";
    public string ContentRootPath { get; set; } = root;
    public string WebRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}
