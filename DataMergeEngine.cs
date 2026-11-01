using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

public sealed class DataMergeResult
{
    public AppState MergedState { get; set; } = new();
    public bool HasChangesToLocal { get; set; }
    public bool HasChangesToRemote { get; set; }
    public int PapersAdded { get; set; }
    public int PapersUpdated { get; set; }
    public int PapersDeleted { get; set; }
    public int ItemsAdded { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsDeleted { get; set; }
}

public static class DataMergeEngine
{
    public static DataMergeResult Merge(AppState localState, AppState remoteState)
    {
        ArgumentNullException.ThrowIfNull(localState);
        ArgumentNullException.ThrowIfNull(remoteState);
        var mergedState = SyncProtocol.Clone(localState);
        var tombstones = new Dictionary<string, SyncTombstone>(StringComparer.Ordinal);
        void Add(SyncTombstone tombstone)
        {
            if (string.IsNullOrWhiteSpace(tombstone.Id) || tombstone.Type is not ("paper" or "item")) return;
            var key = SyncProtocol.TombstoneKey(tombstone);
            if (!tombstones.TryGetValue(key, out var previous) || tombstone.DeletedAt > previous.DeletedAt)
                tombstones[key] = SyncProtocol.Clone(tombstone);
        }
        foreach (var state in new[] { localState, remoteState })
        {
            foreach (var t in state.SyncTombstones) Add(t);
            foreach (var p in state.Papers)
            {
                if (p.IsDeleted) Add(new() { Id = p.Id, Type = "paper", DeletedAt = p.UpdatedAt });
                foreach (var i in p.Items.Where(i => i.IsDeleted))
                    Add(new() { Id = i.Id, Type = "item", ParentPaperId = p.Id, DeletedAt = i.UpdatedAt });
            }
        }
        // Offline clients may return after more than 30 days. Do not expire deletions.
        mergedState.SyncTombstones = tombstones.Values.OrderBy(SyncProtocol.TombstoneKey, StringComparer.Ordinal).ToList();
        bool Deleted(string type, string id, string? parent, long modified) =>
            tombstones.Values.Any(t => t.Type == type && t.Id == id &&
                (type == "paper" || string.IsNullOrEmpty(t.ParentPaperId) || t.ParentPaperId == parent) && t.DeletedAt >= modified);

        var local = localState.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var remote = remoteState.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var papers = new List<PaperData>();
        foreach (var id in local.Keys.Concat(remote.Keys).Distinct(StringComparer.Ordinal))
        {
            local.TryGetValue(id, out var lp);
            remote.TryGetValue(id, out var rp);
            if (Deleted("paper", id, null, Math.Max(lp?.UpdatedAt ?? 0, rp?.UpdatedAt ?? 0))) continue;
            var paper = SyncProtocol.Clone(lp ?? rp!);
            if (lp != null && rp != null)
            {
                var fields = SyncProtocol.MergeFields(SyncProtocol.Paper(lp, false), SyncProtocol.Paper(rp, false), lp.UpdatedAt, rp.UpdatedAt);
                var full = JsonSerializer.SerializeToNode(lp, SyncProtocol.JsonOptions)!.AsObject();
                foreach (var entry in fields) full[entry.Key] = entry.Value?.DeepClone();
                paper = full.Deserialize<PaperData>(SyncProtocol.JsonOptions)!;
            }
            paper.PresentSyncFields = null;
            paper.IsDeleted = false;
            var li = (lp?.Items ?? new()).ToDictionary(i => i.Id, StringComparer.Ordinal);
            var ri = (rp?.Items ?? new()).ToDictionary(i => i.Id, StringComparer.Ordinal);
            var items = new List<PaperItem>();
            foreach (var itemId in li.Keys.Concat(ri.Keys).Distinct(StringComparer.Ordinal))
            {
                li.TryGetValue(itemId, out var a);
                ri.TryGetValue(itemId, out var b);
                if (Deleted("item", itemId, id, Math.Max(a?.UpdatedAt ?? 0, b?.UpdatedAt ?? 0))) continue;
                var item = a != null && b != null
                    ? SyncProtocol.MergeFields(SyncProtocol.Item(a), SyncProtocol.Item(b), a.UpdatedAt, b.UpdatedAt).Deserialize<PaperItem>(SyncProtocol.JsonOptions)!
                    : SyncProtocol.Clone(a ?? b!);
                item.PresentSyncFields = null;
                item.IsDeleted = false;
                items.Add(item);
            }
            paper.Items = items.OrderBy(i => i.Order).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
            papers.Add(paper);
        }
        mergedState.Papers = papers;
        var document = SyncProtocol.Document(mergedState);
        return new DataMergeResult
        {
            MergedState = mergedState,
            HasChangesToLocal = !JsonNode.DeepEquals(document, SyncProtocol.Document(localState)),
            HasChangesToRemote = !JsonNode.DeepEquals(document, SyncProtocol.Document(remoteState)),
            PapersAdded = papers.Count(p => !local.ContainsKey(p.Id)),
            PapersDeleted = local.Count - papers.Count(p => local.ContainsKey(p.Id)),
            PapersUpdated = papers.Count(p => local.TryGetValue(p.Id, out var old) && !JsonNode.DeepEquals(SyncProtocol.Paper(old), SyncProtocol.Paper(p)))
        };
    }
}
