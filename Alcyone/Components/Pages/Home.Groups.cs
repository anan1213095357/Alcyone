using StateMachine.Execution;
using System.Text.Json;

namespace StateMachine.Components.Pages;

public partial class Home
{
    public sealed record CanvasStatePosition(string Id, double X, double Y);
    public sealed record CanvasEditResult(List<string> StateIds, List<string> GroupIds)
    {
        public List<StateGroupModel> Groups { get; set; } = new();
    }

    private void ApplyCanvasPositions(List<CanvasStatePosition> positions)
    {
        foreach (var position in positions)
        {
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y)) continue;
            var state = Machine.States.FirstOrDefault(s => s.Id == position.Id);
            if (state is null) continue;
            state.X = Math.Max(0, position.X); state.Y = Math.Max(0, position.Y);
        }
    }

    private async Task<CanvasEditResult> EditSelectionFromJs(string configKey, string command, List<string> ids, List<CanvasStatePosition> positions)
    {
        if (configKey != _configKey || _switchingConfig) return new(new(), new());
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        var result = new CanvasEditResult(new(), new());
        if (command == "move") ApplyCanvasPositions(positions);
        else if (command == "delete")
        {
            Machine.States.RemoveAll(s => selected.Contains(s.Id));
            Machine.Edges.RemoveAll(e => selected.Contains(e.FromStateId) || selected.Contains(e.ToStateId));
            foreach (var group in Machine.StateGroups) group.StateIds.RemoveAll(selected.Contains);
            Machine.StateGroups.RemoveAll(g => g.StateIds.Count == 0);
            foreach (var region in Runtime.CurrentStates.Where(p => selected.Contains(p.Value)).Select(p => p.Key).ToArray())
            { Runtime.CurrentStates.Remove(region); Runtime.EnteredStates.Remove(region); }
        }
        else if (command == "copy")
        {
            var stateIds = new Dictionary<string, string>();
            var portIds = new Dictionary<string, string>();
            foreach (var source in Machine.States.Where(s => selected.Contains(s.Id)).ToArray())
            {
                var copy = JsonSerializer.Deserialize<StateModel>(JsonSerializer.Serialize(source, _jsonOptions), _jsonOptions)!;
                stateIds[source.Id] = copy.Id = Uid("state");
                copy.Name += " (copy)"; copy.X += 48; copy.Y += 48; copy.IsStart = false;
                foreach (var input in copy.Inputs) { var old = input.Id; portIds[old] = input.Id = Uid("input"); }
                foreach (var output in copy.Outputs)
                {
                    var old = output.Id; portIds[old] = output.Id = Uid("output");
                    foreach (var condition in output.Conditions) condition.Id = Uid("condition");
                }
                Machine.States.Add(copy); result.StateIds.Add(copy.Id);
            }
            foreach (var edge in Machine.Edges.Where(e => stateIds.ContainsKey(e.FromStateId) && stateIds.ContainsKey(e.ToStateId)).ToArray())
                Machine.Edges.Add(new EdgeModel { Id = Uid("edge"), FromStateId = stateIds[edge.FromStateId], ToStateId = stateIds[edge.ToStateId],
                    FromPortId = portIds[edge.FromPortId], ToPortId = portIds[edge.ToPortId], DelayMilliseconds = edge.DelayMilliseconds });
            foreach (var group in Machine.StateGroups.Where(g => g.StateIds.Count > 0 && g.StateIds.All(stateIds.ContainsKey)).ToArray())
            {
                var copy = new StateGroupModel { Id = Uid("group"), Name = group.Name + " (copy)", Appearance = group.Appearance, IsCollapsed = group.IsCollapsed, X = group.X + 48, Y = group.Y + 48,
                    StateIds = group.StateIds.Select(id => stateIds[id]).ToList() };
                Machine.StateGroups.Add(copy); if (copy.IsCollapsed) result.GroupIds.Add(copy.Id);
            }
        }
        else return result;
        SelectedStateId = null; SelectedEdgeId = null; PendingConnection = null;
        result.Groups = Machine.StateGroups;
        await SaveConfigCoreAsync(true);
        await InvokeAsync(StateHasChanged);
        return result;
    }

    private async Task SaveGroupsFromJs(string configKey, List<StateGroupModel> groups, List<CanvasStatePosition> positions)
    {
        if (configKey != _configKey || _switchingConfig) return;
        ApplyCanvasPositions(positions);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var states = Machine.States.ToDictionary(s => s.Id);
        var accepted = new List<StateGroupModel>();
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || !ids.Add(group.Id)
                || !double.IsFinite(group.X) || !double.IsFinite(group.Y)) continue;
            group.StateIds = group.StateIds.Where(id => states.ContainsKey(id) && used.Add(id)).ToList();
            if (group.StateIds.Count == 0) continue;
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "Alcyone" : group.Name.Trim();
            if (group.Name.Length > 48) group.Name = group.Name[..48];
            if (!new[] { "ocean", "rust", "ice", "violet", "sand", "jade", "rock", "lava", "blue", "rose", "forest", "silver" }.Contains(group.Appearance)) group.Appearance = "ocean";
            group.X = Math.Clamp(group.X, 0, 3800);
            group.Y = Math.Clamp(group.Y, 0, 2300);
            var previous = Machine.StateGroups.FirstOrDefault(g => g.Id == group.Id);
            if (previous is { IsCollapsed: true } && group.IsCollapsed)
            {
                var dx = group.X - previous.X;
                var dy = group.Y - previous.Y;
                // Keep every member's relative position when moving its collapsed group.
                dx = Math.Max(dx, -group.StateIds.Min(id => states[id].X));
                dy = Math.Max(dy, -group.StateIds.Min(id => states[id].Y));
                group.X = previous.X + dx;
                group.Y = previous.Y + dy;
                foreach (var id in group.StateIds) { states[id].X += dx; states[id].Y += dy; }
            }
            accepted.Add(group);
        }
        Machine.StateGroups = accepted;
        SelectedStateId = null;
        SelectedEdgeId = null;
        PendingConnection = null;
        await SaveConfigCoreAsync(true);
        await InvokeAsync(StateHasChanged);
    }
}
