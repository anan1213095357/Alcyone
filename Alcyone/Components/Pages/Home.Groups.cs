using StateMachine.Execution;

namespace StateMachine.Components.Pages;

public partial class Home
{
    private async Task SaveGroupsFromJs(string configKey, List<StateGroupModel> groups)
    {
        if (configKey != _configKey || _switchingConfig) return;
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
            group.X = Math.Clamp(group.X, 0, 3800);
            group.Y = Math.Clamp(group.Y, 0, 2300);
            var previous = Machine.StateGroups.FirstOrDefault(g => g.Id == group.Id);
            if (previous is not null)
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
        await SaveConfigAsync();
        await InvokeAsync(StateHasChanged);
    }
}
