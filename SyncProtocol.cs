using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaperTodo;

/// <summary>Shared content wire format; AppState settings never go to remote storage.</summary>
public static class SyncProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly string[] DevicePaperFields =
        ["x", "y", "width", "height", "isVisible", "alwaysOnTop", "isCollapsed", "isPinnedToDesktop", "textZoom", "capsuleSide", "capsuleMonitorDeviceName"];
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    public static JsonObject Item(PaperItem item)
    {
        var json = JsonSerializer.SerializeToNode(item, JsonOptions)!.AsObject();
        json["linkedNoteId"] = item.LinkedNoteId;
        json["dueAtLocal"] = item.DueAtLocal;
        json["reminderIntervalValue"] = item.ReminderIntervalValue;
        json["reminderIntervalUnit"] = item.ReminderIntervalUnit;
        json["isDeleted"] = item.IsDeleted;
        RemoveMissing(json, item.PresentSyncFields);
        return json;
    }
    public static JsonObject Paper(PaperData paper, bool includeItems = true)
    {
        var json = JsonSerializer.SerializeToNode(paper, JsonOptions)!.AsObject();
        json["isDeleted"] = paper.IsDeleted;
        RemoveMissing(json, paper.PresentSyncFields);
        foreach (var key in DevicePaperFields) json.Remove(key);
        json.Remove("items");
        if (includeItems)
            json["items"] = new JsonArray(paper.Items.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => (JsonNode)Item(i)).ToArray());
        return json;
    }
    private static void RemoveMissing(JsonObject json, HashSet<string>? fields)
    {
        if (fields == null) return;
        foreach (var key in json.Select(p => p.Key).ToList())
            if (!fields.Contains(key)) json.Remove(key);
    }
    public static JsonObject Document(AppState state) => new()
    {
        ["schemaVersion"] = 1,
        ["papers"] = new JsonArray(state.Papers.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => (JsonNode)Paper(p)).ToArray()),
        ["syncTombstones"] = JsonSerializer.SerializeToNode(state.SyncTombstones.OrderBy(TombstoneKey, StringComparer.Ordinal), JsonOptions)
    };
    public static string Serialize(AppState state) => Document(state).ToJsonString(JsonOptions);
    public static string TombstoneKey(SyncTombstone t) => $"{t.Type}:{t.ParentPaperId ?? ""}:{t.Id}";
    public static bool NeedsRewrite(string json) => JsonNode.Parse(json,
        documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!
        .AsObject().Any(p => p.Key is not ("schemaVersion" or "papers" or "syncTombstones"));

    public static AppState Deserialize(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new JsonException("Expected a sync document object.");
        if (root["papers"] is not JsonArray papers) throw new JsonException("Sync document must contain a papers array.");
        var state = new AppState();
        var paperIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in papers)
        {
            var obj = node as JsonObject ?? throw new JsonException("Invalid paper.");
            var id = obj["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || !paperIds.Add(id)) throw new JsonException("Missing or duplicate paper ID.");
            var paper = obj.Deserialize<PaperData>(JsonOptions) ?? throw new JsonException("Invalid paper.");
            paper.PresentSyncFields = obj.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            paper.UpdatedAt = obj["updatedAt"]?.GetValue<long>() ?? 0;
            paper.Items ??= new();
            paper.NoteCanvasElements ??= new();
            paper.Content ??= "";
            paper.Title ??= "";
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            var itemNodes = obj["items"] as JsonArray ?? new JsonArray();
            for (var i = 0; i < paper.Items.Count; i++)
            {
                var item = paper.Items[i] ?? throw new JsonException("Invalid item.");
                if (string.IsNullOrWhiteSpace(item.Id) || !itemIds.Add(item.Id)) throw new JsonException("Missing or duplicate item ID.");
                var itemJson = itemNodes[i] as JsonObject ?? throw new JsonException("Invalid item.");
                if (itemJson["id"] == null) throw new JsonException("Missing item ID.");
                item.PresentSyncFields = itemJson.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                item.UpdatedAt = itemJson["updatedAt"]?.GetValue<long>() ?? paper.UpdatedAt;
                item.Text ??= "";
                item.TodoExtraColumns ??= new();
                item.TodoColumnWidths ??= new();
            }
            state.Papers.Add(paper);
        }
        if (root["syncTombstones"] is JsonArray tombstones)
            state.SyncTombstones = tombstones.Deserialize<List<SyncTombstone>>(JsonOptions) ?? new();
        return state;
    }

    // Equal timestamps must resolve identically on Windows and Dart. Encode
    // strings as UTF-16 units, independent of each JSON encoder's escaping.
    public static string Canonical(JsonNode? node)
    {
        if (node == null) return "null";
        if (node is JsonObject obj)
            return "{" + string.Join(",", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => StringKey(p.Key) + ":" + Canonical(p.Value))) + "}";
        if (node is JsonArray array) return "[" + string.Join(",", array.Select(Canonical)) + "]";
        var element = JsonSerializer.SerializeToElement(node, JsonOptions);
        if (element.ValueKind == JsonValueKind.String) return StringKey(element.GetString()!);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number)) return number.ToString("G29", CultureInfo.InvariantCulture);
        return element.GetRawText();
    }
    private static string StringKey(string text) => "s" + string.Concat(text.Select(c => ((int)c).ToString("x4", CultureInfo.InvariantCulture)));
    public static JsonObject MergeFields(JsonObject local, JsonObject remote, long localTime, long remoteTime)
    {
        var remoteWins = remoteTime > localTime || remoteTime == localTime && string.CompareOrdinal(Canonical(remote), Canonical(local)) > 0;
        var merged = (remoteWins ? local : remote).DeepClone().AsObject();
        foreach (var entry in remoteWins ? remote : local) merged[entry.Key] = entry.Value?.DeepClone();
        return merged;
    }

    /// <summary>Keep identities held by windows and editor event handlers.</summary>
    public static void Apply(AppState target, AppState merged)
    {
        var existing = target.Papers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var papers = new List<PaperData>();
        foreach (var source in merged.Papers)
        {
            if (!existing.TryGetValue(source.Id, out var paper)) { papers.Add(Clone(source)); continue; }
            var items = paper.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
            var updatedItems = new List<PaperItem>();
            foreach (var incoming in source.Items)
            {
                if (items.TryGetValue(incoming.Id, out var item)) { CopyProperties(item, incoming); updatedItems.Add(item); }
                else updatedItems.Add(Clone(incoming));
            }
            var oldElements = paper.NoteCanvasElements.ToDictionary(e => e.Id, StringComparer.Ordinal);
            var elements = new List<NoteCanvasElement>();
            foreach (var incoming in source.NoteCanvasElements)
            {
                if (oldElements.TryGetValue(incoming.Id, out var element)) { CopyProperties(element, incoming); elements.Add(element); }
                else elements.Add(Clone(incoming));
            }
            CopyProperties(paper, source, nameof(PaperData.Items), nameof(PaperData.NoteCanvasElements));
            paper.Items = updatedItems;
            paper.NoteCanvasElements = elements;
            paper.PresentSyncFields = null;
            papers.Add(paper);
        }
        target.Papers = papers;
        target.SyncTombstones = Clone(merged.SyncTombstones);
    }
    private static void CopyProperties<T>(T target, T source, string? except = null, string? otherException = null)
    {
        var copy = Clone(source);
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.CanRead && property.CanWrite && property.Name != except && property.Name != otherException) property.SetValue(target, property.GetValue(copy));
    }
}
