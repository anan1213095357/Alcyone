namespace StateMachine.Execution;

public class DesktopGraphCommand
{
    public string Action { get; set; } = "";
    public string StateId { get; set; } = "";
    public string TargetStateId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string EdgeId { get; set; } = "";
    public string RegionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Appearance { get; set; } = "";
    public List<string> StateIds { get; set; } = [];
    public string Position { get; set; } = "";
    public int Count { get; set; } = 1;
    public double? X { get; set; }
    public double? Y { get; set; }
    public int? DelayMilliseconds { get; set; }
}

public sealed partial class MachineSession
{
    public static readonly string[] DesktopGraphActions = ["createPlanet", "createState", "deletePlanet", "deleteState", "renamePlanet", "movePlanet", "moveState", "setPlanetAppearance", "collapse", "expand", "connect", "disconnect", "setEdgeDelay", "normalizeRegions"];
    public static readonly string[] PlanetAppearances = ["ocean", "rust", "ice", "violet", "sand", "jade", "rock", "lava", "blue", "rose", "forest", "silver", "amber", "pearl", "toxic", "storm", "crimson", "midnight"];
    public async Task EditDesktopGraphAsync(string path, DesktopGraphCommand command)
    {
        if (command.Action == "normalizeRegions" && !MachineRegions.Normalize(Clone(Snapshot.Machine))) return;
        var structural = command.Action is "deletePlanet" or "deleteState" or "connect" or "disconnect" or "setEdgeDelay";
        var resume = Snapshot.Runtime.Running;
        // Even adding a node must wait for a running cycle before publishing a new graph.
        await StopAsync(); if (_runTask is not null) await _runTask;
        for (var attempt=0; Snapshot.Executing && attempt<1200; attempt++)await Task.Delay(25);
        try
        {
            await DispatchAsync(() =>
            {
                if (_executing) throw new InvalidOperationException("请等待当前执行结束。");
                var model = Clone(Machine);
                string Id(string kind) => kind + "_" + Guid.NewGuid().ToString("N");
                string Name(string fallback) => string.IsNullOrWhiteSpace(command.Name) ? fallback : command.Name.Trim().Length <= 48 ? command.Name.Trim() : throw new InvalidOperationException("名称最多 48 字。");
                T Resolve<T>(IEnumerable<T> items, Func<T,bool> match, string label) { var found=items.Where(match).Take(2).ToArray(); return found.Length==1 ? found[0] : throw new InvalidOperationException(label+"不存在或名称不唯一，请指定准确目标。"); }
                StateModel State(string id) => Resolve(model.States,s=>s.Id==id || s.Name==id,"状态");
                StateGroupModel Group() => Resolve(model.StateGroups,g=>g.Id==command.GroupId || g.Name==command.GroupId,"星球");
                string Appearance() => string.IsNullOrWhiteSpace(command.Appearance) ? "ocean" : PlanetAppearances.Contains(command.Appearance) ? command.Appearance : throw new InvalidOperationException("星球外观不存在。");
                double Coordinate(double? value,double fallback,double maximum) => value is null ? fallback : double.IsFinite(value.Value) ? Math.Clamp(value.Value,0,maximum) : throw new InvalidOperationException("坐标无效。");
                switch(command.Action)
                {
                    case "createPlanet": case "createState":
                    {
                        var region=string.IsNullOrEmpty(command.RegionId) ? model.Regions.FirstOrDefault() : Resolve(model.Regions,r=>r.Id==command.RegionId || r.Name==command.RegionId,"分区");
                        if(region is null) { region=new RegionModel { Id=Id("region"),Name="桌面自动化" }; model.Regions.Add(region); }
                        var isPlanet=command.Action=="createPlanet";
                        if(command.Count is <1 or >12)throw new InvalidOperationException("一次可新增 1–12 个星球或卡片。");
                        for(var item=0;item<command.Count;item++)
                        {
                        var x=140d; var y=140d;
                        for(var slot=0;slot<120;slot++) {
                            x=140+(slot%10)*350; y=140+(slot/10)*190;
                            if(!model.StateGroups.Any(g=>Math.Abs(g.X-x)<230 && Math.Abs(g.Y-y)<180) && !model.States.Any(s=>Math.Abs(s.X-x)<230 && Math.Abs(s.Y-y)<180))break;
                        }
                        if(command.Position=="bottomRight") {
                            var columns=Math.Min(command.Count,3);var rows=(command.Count+columns-1)/columns;
                            x=(isPlanet?3700:3300)-(columns-1-item%columns)*(isPlanet?220:550);
                            y=2200-(rows-1-item/columns)*240;
                        }
                        x=Coordinate(command.X is {} cx ? cx+(item%3)*(isPlanet?220:550) : null,x,isPlanet?3800:3400);
                        y=Coordinate(command.Y is {} cy ? cy+(item/3)*240 : null,y,2200);
                        var state=new StateModel { Id=Id("state"),Name=Name(isPlanet?"新星球 "+(model.StateGroups.Count+1):"新状态 "+(model.States.Count+1))+(command.Count>1 && !string.IsNullOrWhiteSpace(command.Name)?" "+(item+1):""),RegionId=region.Id,X=x,Y=y,IsStart=!model.States.Any(s=>s.RegionId==region.Id),
                            Inputs=[new InputPortModel { Id=Id("input"),Name="进入" }], Outputs=[new OutputPortModel { Id=Id("output"),Name="输出",MatchMode="always" }] };
                        model.States.Add(state);
                        if(isPlanet)model.StateGroups.Add(new StateGroupModel { Id=Id("group"),Name=state.Name,Appearance=Appearance(),X=x,Y=y,StateIds=[state.Id],IsCollapsed=true });
                        }
                        break;
                    }
                    case "renamePlanet": Group().Name=Name("新星球"); break;
                    case "setPlanetAppearance": Group().Appearance=Appearance(); break;
                    case "expand": Group().IsCollapsed=false; break;
                    case "collapse":
                    {
                        if(!string.IsNullOrEmpty(command.GroupId)) { Group().IsCollapsed=true; break; }
                        var members=command.StateIds.Select(State).Distinct().ToList();
                        if(members.Count==0)throw new InvalidOperationException("请选择需要折叠的卡片。");
                        foreach(var g in model.StateGroups)g.StateIds.RemoveAll(id=>members.Any(s=>s.Id==id));
                        model.StateGroups.RemoveAll(g=>g.StateIds.Count==0);
                        model.StateGroups.Add(new StateGroupModel { Id=Id("group"),Name=Name("新星球"),Appearance=Appearance(),X=Coordinate(command.X,members.Min(s=>s.X),3800),Y=Coordinate(command.Y,members.Min(s=>s.Y),2300),StateIds=members.Select(s=>s.Id).ToList() });
                        break;
                    }
                    case "movePlanet":
                    {
                        var g=Group(); var x=Coordinate(command.X,g.X,3800);var y=Coordinate(command.Y,g.Y,2300);
                        foreach(var id in g.StateIds) { var state=State(id);state.X=Math.Clamp(state.X+x-g.X,0,3800);state.Y=Math.Clamp(state.Y+y-g.Y,0,2300); }
                        g.X=x;g.Y=y;break;
                    }
                    case "moveState": { var state=State(command.StateId);state.X=Coordinate(command.X,state.X,3400);state.Y=Coordinate(command.Y,state.Y,2200);break; }
                    case "deletePlanet": case "deleteState":
                    {
                        var ids=command.Action=="deletePlanet" ? Group().StateIds.ToHashSet() : new HashSet<string>{State(command.StateId).Id};
                        model.States.RemoveAll(s=>ids.Contains(s.Id));model.Edges.RemoveAll(e=>ids.Contains(e.FromStateId)||ids.Contains(e.ToStateId));
                        foreach(var g in model.StateGroups)g.StateIds.RemoveAll(ids.Contains); model.StateGroups.RemoveAll(g=>g.StateIds.Count==0);
                        foreach(var region in model.Regions)if(!model.States.Any(s=>s.RegionId==region.Id && s.IsStart) && model.States.FirstOrDefault(s=>s.RegionId==region.Id) is {} first)first.IsStart=true;
                        break;
                    }
                    case "connect":
                    {
                        StateModel Endpoint(string id) { var g=model.StateGroups.FirstOrDefault(g=>g.Id==id || g.Name==id); return g is null ? State(id) : State(g.StateIds[0]); }
                        var from=Endpoint(command.StateId);var to=Endpoint(command.TargetStateId);
                        
                        if(model.Edges.Any(e=>e.FromStateId==from.Id && e.ToStateId==to.Id))throw new InvalidOperationException("航线已存在。");
                        var output=new OutputPortModel {Id=Id("output"),Name="前往 "+to.Name,MatchMode="always"};from.Outputs.Add(output);
                        var input=to.Inputs.FirstOrDefault();if(input is null){input=new InputPortModel{Id=Id("input"),Name="进入"};to.Inputs.Add(input);}
                        model.Edges.Add(new EdgeModel { Id=Id("edge"),FromStateId=from.Id,FromPortId=output.Id,ToStateId=to.Id,ToPortId=input.Id,DelayMilliseconds=Math.Clamp(command.DelayMilliseconds??4000,0,86400000) });break;
                    }
                    case "disconnect": case "setEdgeDelay":
                    {
                        var edge=Resolve(model.Edges,e=>e.Id==command.EdgeId,"航线");
                        if(command.Action=="disconnect")model.Edges.Remove(edge);
                        else edge.DelayMilliseconds=Math.Clamp(command.DelayMilliseconds??4000,0,86400000);break;
                    }
                    case "normalizeRegions": break;
                    default: throw new InvalidOperationException("不支持的桌面编辑操作。");
                }
                var regionsChanged=MachineRegions.Normalize(model);
                var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                try { File.WriteAllText(temp,System.Text.Json.JsonSerializer.Serialize(model,_jsonOptions));File.Move(temp,path,true); }
                finally { if(File.Exists(temp))File.Delete(temp); }
                Machine=model;_publishedMachine=Clone(model);Interlocked.Increment(ref _canvasRevision);
                if(structural || regionsChanged)ResetRuntime();_lastNotification=0;Notify();
            });
        }
        finally { if(resume)await StartAsync(); }
    }
}
