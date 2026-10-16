namespace StateMachine.Execution;

public sealed partial class MachineSession
{
    public sealed record LayoutPosition(string Id, double X, double Y);
    private long _canvasRevision;
    public long CanvasRevision => Volatile.Read(ref _canvasRevision);

    public Task UpdateCanvasLayoutAsync(string path, long revision, IReadOnlyList<LayoutPosition> positions,
        List<StateGroupModel>? groups = null) => _dispatcher.InvokeAsync(() =>
    {
        if (revision != _canvasRevision) throw new InvalidOperationException("桌面布局已被其他窗口更新，请重试。");
        var updated = Clone(Machine);
        var states = updated.States.ToDictionary(s => s.Id);
        foreach (var p in positions)
            if (states.TryGetValue(p.Id, out var state) && double.IsFinite(p.X) && double.IsFinite(p.Y))
            { state.X = Math.Max(0, p.X); state.Y = Math.Max(0, p.Y); }
        if (groups is not null)
        {
            var used = new HashSet<string>(); var ids = new HashSet<string>();
            updated.StateGroups = Clone(groups).Where(g => !string.IsNullOrWhiteSpace(g.Id) && ids.Add(g.Id) && double.IsFinite(g.X) && double.IsFinite(g.Y)).ToList();
            foreach (var group in updated.StateGroups)
            {
                group.StateIds = group.StateIds.Where(id => states.ContainsKey(id) && used.Add(id)).ToList();
                if (group.StateIds.Count == 0) continue;
                group.Name = string.IsNullOrWhiteSpace(group.Name) ? "Alcyone" : group.Name.Trim()[..Math.Min(48, group.Name.Trim().Length)];
                if (!new[] { "ocean", "rust", "ice", "violet", "sand", "jade", "rock", "lava", "blue", "rose", "forest", "silver", "amber", "pearl", "toxic", "storm", "crimson", "midnight" }.Contains(group.Appearance)) group.Appearance = "ocean";
                group.X = Math.Max(0, group.X); group.Y = Math.Max(0, group.Y);
                var previous = Machine.StateGroups.FirstOrDefault(g => g.Id == group.Id);
                if (previous is not { IsCollapsed: true } || !group.IsCollapsed) continue;
                var dx = group.X - previous.X;
                var dy = group.Y - previous.Y;
                group.X = previous.X + dx; group.Y = previous.Y + dy;
                foreach (var id in group.StateIds) { states[id].X = Math.Max(0,states[id].X+dx); states[id].Y = Math.Max(0,states[id].Y+dy); }
            }
            updated.StateGroups.RemoveAll(g => g.StateIds.Count == 0);
        }
        // Commit only layout changes; moving a card must not reset the running state machine.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(updated, _jsonOptions)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Machine = updated; _publishedMachine = Clone(updated); Interlocked.Increment(ref _canvasRevision);
        _lastNotification = 0; Notify();
    });
}
