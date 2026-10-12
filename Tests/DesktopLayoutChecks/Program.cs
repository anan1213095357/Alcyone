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
await using (var session = new MachineSession(services, new EnvironmentStub(root), new DesktopColorProbeScanner(), new MachineModel()))
{
    var path = Path.Combine(root, "condition-config.json");
    var compiled = session.ScriptHost.ReloadFromText("""
        public sealed class DesktopConditions : StateScript
        {
            [StateCondition("建表完成", "数据库")]
            public bool Ready() => System.IO.File.Exists("not-created.db");
        }
        """);
    Check(compiled.Success, "Desktop condition script compiles");
    await session.EditDesktopGraphAsync(path, new DesktopGraphCommand
    {
        Action = "createState", Name = "数据库初始化", RequiresCondition = true,
        Conditions = [new() { Kind = "script", ScriptMethod = "DesktopConditions.Ready" }]
    });
    var state = JsonSerializer.Deserialize<MachineModel>(File.ReadAllText(path), options)!.States.Single();
    Check(state.Outputs[0].MatchMode == "all" && state.Outputs[0].Conditions.Single().ScriptMethod == "DesktopConditions.Ready", "Created state persists the compiled condition instead of unconditional output");
    var before = File.ReadAllText(path);
    foreach (var conditions in new List<ConditionModel>?[] { null, [new() { Kind = "script", ScriptMethod = "Missing.Ready" }] })
    {
        try
        {
            await session.EditDesktopGraphAsync(path, new DesktopGraphCommand { Action = "createState", RequiresCondition = true, Conditions = conditions });
            throw new Exception("Invalid condition was accepted");
        }
        catch (InvalidOperationException) { }
        Check(session.Snapshot.Machine.States.Count == 1 && File.ReadAllText(path) == before, "Missing condition or method cannot create an empty state");
    }
    await session.EditDesktopGraphBatchAsync(path,
    [
        new() { Action = "configureState", StateId = "数据库初始化", RequiresCondition = true, Outputs = [
            new() { Name = "输出", Conditions = [new() { Kind = "script", ScriptMethod = "DesktopConditions.Ready" }] },
            new() { Name = "未完成", Conditions = [new() { Kind = "script", ScriptMethod = "DesktopConditions.Ready", ExpectedSuccess = false }] }
        ] },
        new() { Action = "createState", Name = "完成" },
        new() { Action = "createState", Name = "等待" },
        new() { Action = "connect", StateId = "数据库初始化", TargetStateId = "完成", OutputId = "输出", DelayMilliseconds = 0 },
        new() { Action = "connect", StateId = "数据库初始化", TargetStateId = "等待", OutputId = "未完成", DelayMilliseconds = 0 }
    ]);
    var graph = session.Snapshot.Machine;
    Check(graph.States.Count == 3 && graph.Edges.Count == 2 && graph.States[0].Outputs.Count == 2 && graph.States[0].Outputs.All(o => o.MatchMode != "always"), "Batch connects named condition branches without bypassing them");
    await session.StepOnceAsync();
    Check(session.Snapshot.Runtime.CurrentStates.Values.Contains(graph.States.Single(s => s.Name == "等待").Id), "Runtime follows the false branch instead of unconditional success");
    before = File.ReadAllText(path);
    try
    {
        await session.EditDesktopGraphBatchAsync(path, [new() { Action = "createState", Name = "不可残留" }, new() { Action = "connect", StateId = "不可残留", TargetStateId = "不存在" }]);
        throw new Exception("Broken batch was accepted");
    }
    catch (InvalidOperationException) { }
    Check(File.ReadAllText(path) == before && !session.Snapshot.Machine.States.Any(s => s.Name == "不可残留"), "Failed batch leaves no orphaned cards or config changes");
}
await using (var session = new MachineSession(services, new EnvironmentStub(root), new DesktopColorProbeScanner(), new MachineModel()))
{
    var result = session.ScriptHost.ReloadFromText("""
        using FreeSql;
        [Table(Name = "auto_nuget_checks")]
        public class NuGetEntity
        {
            [Column(IsPrimary = true)] public int Id { get; set; }
        }
        public sealed class NuGetCondition : StateScript
        {
            [StateCondition("FreeSql 条件", "数据库")]
            public bool Ready() => typeof(NuGetEntity).GetCustomAttributes(false).Any(a => a.GetType().FullName == "FreeSql.DataAnnotations.TableAttribute");
        }
        """, "FreeSqlCondition.csx");
    Check(result.Success, "FreeSql is restored automatically and Table/Column resolve without a manual using: " + string.Join(" | ", result.Errors));
    var target = session.ScriptHost.ActionTargets.Single();
    Check((bool)target.GetType().GetMethod("Ready")!.Invoke(target, null)!, "Auto-imported attributes use actual FreeSql types at runtime");
}
await using (var session = new MachineSession(services, new EnvironmentStub(root), new DesktopColorProbeScanner(), new MachineModel()))
{
    var result = session.ScriptHost.ReloadFromText("""
        #r "nuget: FreeSql.Provider.Sqlite, 3.2.833"
        using FreeSql;
        using FreeSql.DataAnnotations;
        [Table(Name = "verify_records")]
        public class VerifyRecord { [Column(IsPrimary = true)] public int Id { get; set; } public string Name { get; set; } = ""; }
        public sealed class DatabaseCondition : StateScript
        {
            [StateCondition("建表成功", "数据库")]
            public Task<bool> Ready() => Task.Run(() =>
            {
                var path = Vars.Get<string>("dbPath");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var sql = new FreeSqlBuilder().UseConnectionString(DataType.Sqlite, "Data Source=" + path).Build();
                sql.CodeFirst.SyncStructure<VerifyRecord>();
                return Convert.ToInt32(sql.Ado.ExecuteScalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='verify_records'")) == 1;
            }, Api.CancellationToken);
        }
        """, "DatabaseCondition.csx");
    Check(result.Success, "SQLite provider restores and async database condition compiles: " + string.Join(" | ", result.Errors));
    await session.EditDesktopGraphBatchAsync(Path.Combine(root, "database-flow.json"), [
        new() { Action = "createState", Name = "建表", RequiresCondition = true, Conditions = [new() { Kind = "script", ScriptMethod = "DatabaseCondition.Ready" }] },
        new() { Action = "createState", Name = "完成" },
        new() { Action = "connect", StateId = "建表", TargetStateId = "完成", DelayMilliseconds = 0 }
    ]);
    var database = Path.Combine(root, "db", "verify.db");
    await session.SetVariableAsync("dbPath", System.Text.Json.Nodes.JsonValue.Create(database));
    await session.StepOnceAsync();
    Check(File.Exists(database) && session.Snapshot.Runtime.CurrentStates.Values.Contains(session.Snapshot.Machine.States.Single(s => s.Name == "完成").Id), "Real FreeSql table creation completes before the connected state advances");
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
