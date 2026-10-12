namespace StateMachine.Execution;

public static class MachineRegions
{
    public static bool Normalize(MachineModel model)
    {
        var emptyRegions=model.Regions.Where(r=>!model.States.Any(s=>s.RegionId==r.Id)).ToList();
        var parent=model.States.ToDictionary(s=>s.Id,s=>s.Id);
        string Root(string id) { while(parent[id]!=id){parent[id]=parent[parent[id]];id=parent[id];}return id; }
        void Join(string a,string b){if(parent.ContainsKey(a)&&parent.ContainsKey(b))parent[Root(b)]=Root(a);}
        foreach(var edge in model.Edges)Join(edge.FromStateId,edge.ToStateId);
        foreach(var group in model.StateGroups)foreach(var id in group.StateIds.Skip(1))Join(group.StateIds[0],id);
        var used=new HashSet<string>();var regions=new List<RegionModel>();var changed=false;
        foreach(var component in model.States.GroupBy(s=>Root(s.Id)))
        {
            var states=component.ToList();var start=states.FirstOrDefault(s=>s.IsStart)??states[0];
            var existing=model.Regions.FirstOrDefault(r=>r.Id==start.RegionId && !used.Contains(r.Id));
            var region=existing??new RegionModel {Id="region_"+Guid.NewGuid().ToString("N"),Name=model.StateGroups.FirstOrDefault(g=>g.StateIds.Contains(start.Id))?.Name??start.Name};
            used.Add(region.Id);regions.Add(region);
            foreach(var state in states){if(state.RegionId!=region.Id || state.IsStart!=(state.Id==start.Id))changed=true;state.RegionId=region.Id;state.IsStart=state.Id==start.Id;}
        }
        regions.AddRange(emptyRegions.Where(r=>!used.Contains(r.Id)));
        if(!model.Regions.Select(r=>r.Id).SequenceEqual(regions.Select(r=>r.Id)))changed=true;
        model.Regions=regions;
        return changed;
    }
}
