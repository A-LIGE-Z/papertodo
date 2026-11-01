using System.Text.Json.Nodes;

namespace PaperTodo;

/// <summary>Track all content mutations at save, including undo, titles and columns.</summary>
public sealed class SyncChangeTracker
{
    private Dictionary<string, (string Content, long Time, string? Parent)> _baseline = new(StringComparer.Ordinal);
    private static string Fingerprint(JsonObject json)
    {
        json.Remove("updatedAt");
        return SyncProtocol.Canonical(json);
    }
    public void Capture(AppState state) => _baseline = Snapshot(state);
    private static Dictionary<string, (string Content, long Time, string? Parent)> Snapshot(AppState state)
    {
        var snapshot = new Dictionary<string, (string, long, string?)>(StringComparer.Ordinal);
        foreach (var paper in state.Papers)
        {
            snapshot["paper:" + paper.Id] = (Fingerprint(SyncProtocol.Paper(paper, false)), paper.UpdatedAt, null);
            foreach (var item in paper.Items)
                snapshot["item:" + item.Id] = (Fingerprint(SyncProtocol.Item(item)), item.UpdatedAt, paper.Id);
        }
        return snapshot;
    }
    public void Update(AppState state)
    {
        var snapshot = Snapshot(state);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var paper in state.Papers)
        {
            if (_baseline.TryGetValue("paper:" + paper.Id, out var old) && old.Content != snapshot["paper:" + paper.Id].Content)
                paper.UpdatedAt = Math.Max(now, Math.Max(paper.UpdatedAt, old.Time) + 1);
            foreach (var item in paper.Items)
                if (_baseline.TryGetValue("item:" + item.Id, out old) && old.Content != snapshot["item:" + item.Id].Content)
                    item.UpdatedAt = Math.Max(now, Math.Max(item.UpdatedAt, old.Time) + 1);
        }
        foreach (var entry in _baseline.Where(e => !snapshot.ContainsKey(e.Key)))
        {
            var split = entry.Key.IndexOf(':');
            var type = entry.Key[..split];
            var id = entry.Key[(split + 1)..];
            var tombstone = new SyncTombstone { Type = type, Id = id, ParentPaperId = entry.Value.Parent, DeletedAt = Math.Max(now, entry.Value.Time + 1) };
            var existing = state.SyncTombstones.FirstOrDefault(t => SyncProtocol.TombstoneKey(t) == SyncProtocol.TombstoneKey(tombstone));
            if (existing == null) state.SyncTombstones.Add(tombstone);
            else existing.DeletedAt = Math.Max(existing.DeletedAt, tombstone.DeletedAt);
        }
        Capture(state);
    }
}
