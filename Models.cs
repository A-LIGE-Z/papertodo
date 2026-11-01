using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo;

public static class PaperTypes
{
    public const string Todo = "todo";
    public const string Note = "note";
}

public static class PaperLayoutDefaults
{
    public const double MinWidth = 220;
    public const double MinHeight = 160;
    public const double TopBarHeight = 23.5;

    public const double CapsuleWidth = 92; // 包含阴影边框边距
    public const double CapsuleHeight = 46;

    public const double TodoDefaultWidth = 280;
    public const double TodoDefaultHeight = 340;

    public const double NoteDefaultWidth = 320;
    public const double NoteDefaultHeight = 360;
}

public static class WindowDisplayModes
{
    public const string Cards = "cards";
    public const string Outline = "outline";

    public static bool IsValid(string? mode) => mode is Cards or Outline;

    public static string Normalize(string? mode) => mode is Outline ? Outline : Cards;
}

public static class MarkdownRenderModes
{
    public const string Off = "off";
    public const string Basic = "basic";
    public const string Enhanced = "enhanced";

    public static bool IsValid(string? mode)
    {
        return mode is Off or Basic or Enhanced;
    }
}

public static class NoteCanvasElementTypes
{
    public const string Code = "code";

    public static bool IsValid(string? type)
    {
        return type is Code;
    }

    public static string Normalize(string? type)
    {
        return Code;
    }
}

public static class ExternalMarkdownFileExtensions
{
    public const string Default = ".md";

    public static string Normalize(string? extension)
    {
        var value = (extension ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            value = value[1..];
        }
        if (!value.StartsWith(".", StringComparison.Ordinal))
        {
            value = "." + value;
        }

        if (value.Length is < 2 or > 32 ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Default;
        }

        return value.ToLowerInvariant();
    }
}

public static class FullscreenTopmostModes
{
    public const string Avoid = "avoid";
    public const string StayOnTop = "stayOnTop";

    public static string Normalize(string? mode)
    {
        return mode is StayOnTop ? StayOnTop : Avoid;
    }
}

public static class TodoReminderIntervalUnits
{
    public const string Minutes = "minutes";
    public const string Hours = "hours";

    public static string Normalize(string? unit)
    {
        return unit is Hours ? Hours : Minutes;
    }
}

public static class TodoReminderScopes
{
    public const string Nearest = "nearest";
    public const string All = "all";

    public static string Normalize(string? scope)
    {
        return scope is Nearest ? Nearest : All;
    }
}

public static class DeepCapsuleSides
{
    public const string Left = "left";
    public const string Right = "right";

    public static string Normalize(string? side)
    {
        return side is Left ? Left : Right;
    }
}

public static class TodoVisualSizes
{
    public const string Small = "small";
    public const string Medium = "medium";
    public const string Large = "large";
    public const string ExtraLarge = "extraLarge";

    public static string Normalize(string? size)
    {
        return size is Small or Large or ExtraLarge ? size : Medium;
    }

    public static TodoVisualMetrics Metrics(string? size)
    {
        return Normalize(size) switch
        {
            Small => new TodoVisualMetrics(12.5, 2.5, 28, 13, 12, 10, 12, 22, 13, 25),
            Large => new TodoVisualMetrics(14.5, 4.0, 34, 15, 14, 12, 14, 25, 15, 29),
            ExtraLarge => new TodoVisualMetrics(16.0, 4.5, 38, 16.5, 15.5, 13.5, 15.5, 27, 17, 33),
            _ => new TodoVisualMetrics(13.5, 3.5, 32, 14, 13, 11, 13, 24, 14, 27)
        };
    }
}

public static class TodoDueYearDisplayModes
{
    public const string None = "none";
    public const string Short = "short";
    public const string Full = "full";

    public static string Normalize(string? mode)
    {
        return mode is Short or Full ? mode : None;
    }
}

public static class UiFontPresets
{
    public const string Default = "default";
    public const string YaHei = "yahei";
    public const string DengXian = "dengxian";

    public static string Normalize(string? preset)
    {
        return preset is YaHei or DengXian ? preset : Default;
    }
}

public readonly record struct TodoVisualMetrics(
    double TextFontSize,
    double TextVerticalPadding,
    double AppendMinHeight,
    double AppendGlyphFontSize,
    double TrashGlyphFontSize,
    double LinkedNoteNameFontSize,
    double LinkedNoteIconFontSize,
    double CheckColumnWidth,
    double GhostTextFontSize,
    double RowMinHeight);

public sealed class AppState
{
    public List<PaperData> Papers { get; set; } = new();
    public string Theme { get; set; } = "system";
    public string ColorScheme { get; set; } = ColorSchemes.SiYuan;
    public string CustomThemeColorHex { get; set; } = "";
    public string WindowDisplayMode { get; set; } = WindowDisplayModes.Cards;
    public string MarkdownRenderMode { get; set; } = MarkdownRenderModes.Enhanced;
    public string TodoVisualSize { get; set; } = TodoVisualSizes.Medium;
    public string UiFontPreset { get; set; } = UiFontPresets.Default;
    public string SystemFontFamilyName { get; set; } = "";
    public string ExternalMarkdownExtension { get; set; } = ExternalMarkdownFileExtensions.Default;
    public double Zoom { get; set; } = 1.0;
    public bool UseCapsuleMode { get; set; } = true;
    public bool UseDeepCapsuleMode { get; set; } = true;
    public bool ShowTopBarNewTodoButton { get; set; } = true;
    public bool ShowTopBarNewNoteButton { get; set; } = true;
    public bool ShowTopBarExternalOpenButton { get; set; } = true;
    public bool HidePapersFromWindowSwitcher { get; set; }
    public bool EnableTodoNoteLinks { get; set; } = true;
    public bool ShowTodoDueRelativeTime { get; set; }
    public string TodoDueYearDisplayMode { get; set; } = TodoDueYearDisplayModes.None;
    public double TodoLineSpacing { get; set; } = 1.0;
    public double NoteLineSpacing { get; set; } = 1.0;
    public bool UseTodoReminderInterval { get; set; }
    public int TodoReminderIntervalValue { get; set; } = 10;
    public string TodoReminderIntervalUnit { get; set; } = TodoReminderIntervalUnits.Minutes;
    public string TodoReminderScope { get; set; } = TodoReminderScopes.All;
    public int TodoReminderBubbleDurationSeconds { get; set; } = 5;
    public bool ShowLinkedNoteName { get; set; }
    public bool AllowLongLinkedNoteTitles { get; set; }
    public bool HideLinkedNotesFromCapsules { get; set; }
    public bool RunLinkedScriptCapsulesOnClick { get; set; }
    public int MaxTitleLength { get; set; } = PaperTitles.DefaultMaxTitleLength;
    public bool UseCapsuleCollapseAll { get; set; }
    public bool CapsuleCollapseAllActive { get; set; }
    public Dictionary<string, bool> CapsuleCollapseAllActiveQueues { get; set; } = new();
    public bool ShowDeepCapsuleWhileExpanded { get; set; } = true;
    public bool CollapseExpandedDeepCapsuleOnClick { get; set; }
    public bool HideDeepCapsulesWhenCovered { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideDeepCapsulesWhenFullscreen { get; set; }
    public bool EnableAnimations { get; set; } = true;
    public bool EnableToolTips { get; set; } = true;
    public string PinnedTodoHotKey { get; set; } = "";
    public string PinnedNoteHotKey { get; set; } = "";
    public string FullscreenTopmostMode { get; set; } = FullscreenTopmostModes.Avoid;
    public bool UsePersistentPowerShellProcess { get; set; }
    public bool PreferPowerShell7 { get; set; } = true;
    public bool HideScriptRunWindow { get; set; } = true;
    public double DeepCapsuleStartTopMargin { get; set; } = DeepCapsuleLayout.StartTopMargin;

    // Per-queue vertical start margin, keyed by "monitorDevice|side". A missing key falls back to
    // the legacy global DeepCapsuleStartTopMargin, so dragging one queue's master only slides that
    // queue. Old configs (no per-queue entries) keep behaving exactly as the single global margin.
    public Dictionary<string, double> DeepCapsuleQueueStartTopMargins { get; set; } = new();

    // Which screen edge the deep-capsule stack docks to. "left" or "right" (default).
    public string DeepCapsuleSide { get; set; } = DeepCapsuleSides.Right;

    // Device name (e.g. "\\\\.\\DISPLAY1") of the monitor hosting the deep-capsule stack.
    // Empty means "the primary monitor"; resolved with a nearest-monitor fallback on load,
    // so unplugging the anchored monitor gracefully lands the stack on a surviving screen.
    public string DeepCapsuleMonitorDeviceName { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double TopBarHeight { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ShowTopBarNewPaperButtons { get; set; }

    // Sync settings
    public bool SyncEnabled { get; set; }
    public string SyncProvider { get; set; } = SyncProviders.WebDav;
    public string SyncWebDavUrl { get; set; } = "";
    public string SyncWebDavUsername { get; set; } = "";
    public string SyncWebDavPassword { get; set; } = "";
    public string SyncWebDavRemotePath { get; set; } = "/papertodo/data.json";
    public string SyncS3Endpoint { get; set; } = "";
    public string SyncS3Bucket { get; set; } = "";
    public string SyncS3AccessKey { get; set; } = "";
    public string SyncS3SecretKey { get; set; } = "";
    public string SyncS3Region { get; set; } = "us-east-1";
    public string SyncCustomServerUrl { get; set; } = "";
    public string SyncCustomToken { get; set; } = "";
    public long LastSyncTimeUtc { get; set; }
    public string LastSyncStatus { get; set; } = "";

    // Sync tombstones for conflict-free deletion propagation
    public List<SyncTombstone> SyncTombstones { get; set; } = new();

    public void RecordDeletedItem(string itemId, string? parentPaperId = null)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return;
        SyncTombstones.RemoveAll(t => t.Id == itemId);
        SyncTombstones.Add(new SyncTombstone
        {
            Id = itemId,
            Type = "item",
            ParentPaperId = parentPaperId,
            DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    public void RecordDeletedPaper(string paperId)
    {
        if (string.IsNullOrWhiteSpace(paperId)) return;
        SyncTombstones.RemoveAll(t => t.Id == paperId);
        SyncTombstones.Add(new SyncTombstone
        {
            Id = paperId,
            Type = "paper",
            DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }
}

public static class SyncProviders
{
    public const string WebDav = "webdav";
    public const string S3 = "s3";
    public const string Custom = "custom";

    public static string Normalize(string? provider)
    {
        return provider is S3 or Custom ? provider : WebDav;
    }
}

public sealed class SyncTombstone
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "item"; // "paper" or "item"

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentPaperId { get; set; }

    public long DeletedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class PaperData
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    [JsonIgnore]
    public HashSet<string>? PresentSyncFields { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = PaperTypes.Todo;
    public string Title { get; set; } = "";

    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 280;
    public double Height { get; set; } = 360;

    public bool IsVisible { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool IsCollapsed { get; set; } = false;
    public bool IsPinnedToDesktop { get; set; }
    public double TextZoom { get; set; } = 1.0;

    // Which edge-queue this paper's capsule belongs to. A queue is identified by
    // (CapsuleMonitorDeviceName, CapsuleSide): every docked capsule sharing the same pair
    // forms one vertical stack with its own master pill. Empty CapsuleSide means "not yet
    // assigned" — on load it inherits the legacy global anchor so existing capsules keep place.
    public string CapsuleSide { get; set; } = "";
    public string CapsuleMonitorDeviceName { get; set; } = "";

    public List<PaperItem> Items { get; set; } = new();
    public string Content { get; set; } = "";
    public List<NoteCanvasElement> NoteCanvasElements { get; set; } = new();

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDeleted { get; set; }
}

public sealed class NoteCanvasElement
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = NoteCanvasElementTypes.Code;
    public string Text { get; set; } = "";
    public double X { get; set; } = 32;
    public double Y { get; set; } = 32;
    public double Width { get; set; } = 180;
    public double Height { get; set; } = 96;
    public int ZIndex { get; set; }
}

public sealed class PaperItem
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; set; }
    [JsonIgnore]
    public HashSet<string>? PresentSyncFields { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public int Order { get; set; }
    public int TodoColumnCount { get; set; } = 1;
    public List<string> TodoExtraColumns { get; set; } = new();
    public List<double> TodoColumnWidths { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedNoteId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DueAtLocal { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReminderIntervalValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReminderIntervalUnit { get; set; }

    public long UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDeleted { get; set; }
}
