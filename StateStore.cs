using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PaperTodo;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Strict)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Tolerate unknown properties so configs written by older / experimental builds (e.g.
        // retired deepCapsuleDock* fields) still load instead of crashing on startup. The Strict
        // preset otherwise sets Disallow, which makes any removed/renamed field fatal.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip
    };

    public string FilePath { get; } = Path.Combine(AppContext.BaseDirectory, "data.json");
    public string BackupPath { get; } = Path.Combine(AppContext.BaseDirectory, "data.backup.json");
    public string SyncBackupPath { get; } = Path.Combine(AppContext.BaseDirectory, "data.sync_backup.json");
    public string CrashRecoveryPath { get; } = Path.Combine(AppContext.BaseDirectory, "data.crash_recovery.json");
    public string BackupsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "backups");
    private bool _preserveRecoveredLoadFilesOnNextSave;
    private string? _preservedFailedPrimaryPath;
    private string? _preservedRecoveryBackupPath;
    private string? _preservedCrashRecoveryPath;
    private DateTime _lastHistoricalBackupTime = DateTime.MinValue;
    private const int MaxHistoricalBackupsToKeep = 60;

    public AppState Load()
    {
        ClearRecoveredLoadPreservationState();

        bool mainExists = File.Exists(FilePath);
        bool crashRecoveryExists = File.Exists(CrashRecoveryPath);
        bool backupExists = File.Exists(BackupPath);
        bool hasAnyHistoricalBackups = Directory.Exists(BackupsDirectory) &&
            Directory.EnumerateFiles(BackupsDirectory, "data-*.json").Any();

        if (!mainExists && !crashRecoveryExists && !backupExists && !hasAnyHistoricalBackups)
        {
            return new AppState();
        }

        Exception? mainEx = null;
        if (mainExists)
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (state != null)
                {
                    Normalize(state);
                    return state;
                }

                mainEx = new InvalidDataException($"{Path.GetFileName(FilePath)} deserialized to null.");
            }
            catch (Exception ex)
            {
                mainEx = ex;
            }
        }

        // Tier 2: Try crash recovery file (saved immediately before process exit during an unhandled exception)
        Exception? crashRecoveryEx = null;
        if (crashRecoveryExists)
        {
            try
            {
                var json = File.ReadAllText(CrashRecoveryPath);
                var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (state != null)
                {
                    _preserveRecoveredLoadFilesOnNextSave = true;
                    Normalize(state);
                    return state;
                }

                crashRecoveryEx = new InvalidDataException($"{Path.GetFileName(CrashRecoveryPath)} deserialized to null.");
            }
            catch (Exception ex)
            {
                crashRecoveryEx = ex;
            }
        }

        // Tier 3: Try standard backup file
        Exception? backupEx = null;
        if (backupExists)
        {
            try
            {
                var json = File.ReadAllText(BackupPath);
                var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                if (state != null)
                {
                    _preserveRecoveredLoadFilesOnNextSave = true;
                    Normalize(state);
                    return state;
                }

                backupEx = new InvalidDataException($"{Path.GetFileName(BackupPath)} deserialized to null.");
            }
            catch (Exception ex)
            {
                backupEx = ex;
            }
        }

        // Tier 4: Try latest historical backup from backups/ directory
        if (hasAnyHistoricalBackups)
        {
            try
            {
                var latestBackup = new DirectoryInfo(BackupsDirectory)
                    .GetFiles("data-*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (latestBackup != null)
                {
                    var json = File.ReadAllText(latestBackup.FullName);
                    var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
                    if (state != null)
                    {
                        _preserveRecoveredLoadFilesOnNextSave = true;
                        Normalize(state);
                        return state;
                    }
                }
            }
            catch
            {
                // Fall through to error
            }
        }

        var innerEx = mainEx ?? crashRecoveryEx ?? backupEx;
        throw new InvalidOperationException(
            Strings.Get("StateLoadFailed"),
            innerEx);
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _latestWrittenVersion = 0;

    public string SerializeState(AppState state)
    {
        Normalize(state);
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    public AppState? DeserializeState(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var state = JsonSerializer.Deserialize<AppState>(json, JsonOptions);
        if (state != null)
        {
            Normalize(state);
        }
        return state;
    }

    public void CreateSyncBackup(AppState? snapshot = null)
    {
        var json = snapshot == null ? null : JsonSerializer.Serialize(snapshot, JsonOptions);
        _writeLock.Wait();
        try
        {
            if (json != null)
            {
                var temporary = SyncBackupPath + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, SyncBackupPath, overwrite: true);
            }
            else if (File.Exists(FilePath))
            {
                CopyFileWithRetry(FilePath, SyncBackupPath, overwrite: true);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void SaveJsonSync(string json, long version)
    {
        _writeLock.Wait();
        try
        {
            if (version < _latestWrittenVersion)
            {
                return;
            }
            WriteJsonInternal(json);
            _latestWrittenVersion = version;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SaveJsonAsync(string json, long version)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (version < _latestWrittenVersion)
            {
                return;
            }
            await Task.Run(() => WriteJsonInternal(json));
            _latestWrittenVersion = version;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void WriteJsonInternal(string json)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var skipBackupRotation = PreserveRecoveredLoadFilesIfNeeded();
        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, json);

        try
        {
            if (!skipBackupRotation && File.Exists(FilePath))
            {
                CopyFileWithRetry(FilePath, BackupPath, overwrite: true);
            }
        }
        catch
        {
            // Backup failure should not block normal saving.
        }

        MoveFileWithRetry(tempPath, FilePath, overwrite: true);

        // Periodic historical backup snapshot in backups/ directory
        CreateHistoricalBackupIfNeeded(json);

        if (skipBackupRotation)
        {
            ClearRecoveredLoadPreservationState();
        }
    }

    private void CreateHistoricalBackupIfNeeded(string json)
    {
        try
        {
            var now = DateTime.Now;
            if (_lastHistoricalBackupTime != DateTime.MinValue && now - _lastHistoricalBackupTime < TimeSpan.FromMinutes(30))
            {
                return;
            }

            if (!Directory.Exists(BackupsDirectory))
            {
                Directory.CreateDirectory(BackupsDirectory);
            }

            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
            var backupFile = Path.Combine(BackupsDirectory, $"data-{stamp}.json");
            File.WriteAllText(backupFile, json);
            _lastHistoricalBackupTime = now;

            TrimHistoricalBackups();
        }
        catch
        {
            // Ignore historical backup failures
        }
    }

    private void TrimHistoricalBackups()
    {
        try
        {
            if (!Directory.Exists(BackupsDirectory)) return;
            var files = new DirectoryInfo(BackupsDirectory)
                .GetFiles("data-*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxHistoricalBackupsToKeep)
                .ToList();

            foreach (var file in files)
            {
                try { file.Delete(); } catch { }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void CopyFileWithRetry(string sourceFileName, string destFileName, bool overwrite, int maxRetries = 3, int delayMs = 50)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Copy(sourceFileName, destFileName, overwrite);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private static void MoveFileWithRetry(string sourceFileName, string destFileName, bool overwrite, int maxRetries = 3, int delayMs = 50)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                File.Move(sourceFileName, destFileName, overwrite);
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    private bool PreserveRecoveredLoadFilesIfNeeded()
    {
        if (!_preserveRecoveredLoadFilesOnNextSave)
        {
            return false;
        }

        if (File.Exists(FilePath) && string.IsNullOrWhiteSpace(_preservedFailedPrimaryPath))
        {
            _preservedFailedPrimaryPath = CopyRecoverySource(FilePath, "failed_load");
        }

        if (File.Exists(CrashRecoveryPath) && string.IsNullOrWhiteSpace(_preservedCrashRecoveryPath))
        {
            _preservedCrashRecoveryPath = CopyRecoverySource(CrashRecoveryPath, "used_for_recovery");
            try { File.Delete(CrashRecoveryPath); } catch { }
        }

        if (File.Exists(BackupPath) && string.IsNullOrWhiteSpace(_preservedRecoveryBackupPath))
        {
            _preservedRecoveryBackupPath = CopyRecoverySource(BackupPath, "used_for_recovery");
        }

        return true;
    }

    private static string CopyRecoverySource(string sourcePath, string suffix)
    {
        var targetPath = UniqueRecoveryCopyPath(sourcePath, suffix);
        File.Copy(sourcePath, targetPath, overwrite: false);
        return targetPath;
    }

    private static string UniqueRecoveryCopyPath(string sourcePath, string suffix)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = AppContext.BaseDirectory;
        }

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var candidate = Path.Combine(directory, $"{stem}.{suffix}.{stamp}{extension}");
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{stem}.{suffix}.{stamp}.{i}{extension}");
        }

        return candidate;
    }

    private void ClearRecoveredLoadPreservationState()
    {
        _preserveRecoveredLoadFilesOnNextSave = false;
        _preservedFailedPrimaryPath = null;
        _preservedRecoveryBackupPath = null;
        _preservedCrashRecoveryPath = null;
    }

    private static void Normalize(AppState state)
    {
        state.Papers ??= new List<PaperData>();
        state.Papers = state.Papers
            .Where(paper => paper != null)
            .Cast<PaperData>()
            .ToList();
        if (state.Theme is not ("system" or "light" or "dark"))
        {
            state.Theme = "system";
        }

        state.ColorScheme = ColorSchemes.Normalize(state.ColorScheme);
        state.CustomThemeColorHex = Theme.NormalizeCustomThemeColorHex(state.CustomThemeColorHex);
        state.WindowDisplayMode = WindowDisplayModes.Normalize(state.WindowDisplayMode);

        if (!MarkdownRenderModes.IsValid(state.MarkdownRenderMode))
        {
            state.MarkdownRenderMode = MarkdownRenderModes.Enhanced;
        }

        state.ExternalMarkdownExtension = ExternalMarkdownFileExtensions.Normalize(state.ExternalMarkdownExtension);
        state.FullscreenTopmostMode = FullscreenTopmostModes.Normalize(state.FullscreenTopmostMode);
        state.TodoReminderIntervalUnit = TodoReminderIntervalUnits.Normalize(state.TodoReminderIntervalUnit);
        state.TodoReminderScope = TodoReminderScopes.Normalize(state.TodoReminderScope);
        state.TodoDueYearDisplayMode = TodoDueYearDisplayModes.Normalize(state.TodoDueYearDisplayMode);
        state.TodoLineSpacing = NormalizeLineSpacing(state.TodoLineSpacing);
        state.NoteLineSpacing = NormalizeLineSpacing(state.NoteLineSpacing);
        state.TodoReminderIntervalValue = Math.Clamp(state.TodoReminderIntervalValue <= 0 ? 10 : state.TodoReminderIntervalValue, 1, 240);
        state.TodoReminderBubbleDurationSeconds = Math.Clamp(state.TodoReminderBubbleDurationSeconds <= 0 ? 5 : state.TodoReminderBubbleDurationSeconds, 1, 600);
        state.DeepCapsuleSide = DeepCapsuleSides.Normalize(state.DeepCapsuleSide);
        state.DeepCapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(state.DeepCapsuleMonitorDeviceName);
        state.TodoVisualSize = TodoVisualSizes.Normalize(state.TodoVisualSize);
        state.UiFontPreset = UiFontPresets.Default;
        state.SystemFontFamilyName = AppTypography.NormalizeSystemFontFamilyName(state.SystemFontFamilyName);
        state.PinnedTodoHotKey = NormalizeHotKeyForSettings(state.PinnedTodoHotKey);
        state.PinnedNoteHotKey = NormalizeHotKeyForSettings(state.PinnedNoteHotKey);
        state.TopBarHeight = 0;

        if (state.HideDeepCapsulesWhenFullscreen && !state.HideDeepCapsulesWhenCovered)
        {
            state.HideDeepCapsulesWhenCovered = true;
        }
        state.HideDeepCapsulesWhenFullscreen = false;

        if (state.ShowTopBarNewPaperButtons is bool showTopBarNewPaperButtons)
        {
            state.ShowTopBarNewTodoButton = showTopBarNewPaperButtons;
            state.ShowTopBarNewNoteButton = showTopBarNewPaperButtons;
            state.ShowTopBarNewPaperButtons = null;
        }

        if (double.IsNaN(state.Zoom) || double.IsInfinity(state.Zoom) || state.Zoom <= 0)
        {
            state.Zoom = 1.0;
        }
        state.Zoom = Math.Clamp(state.Zoom, 0.5, 1.5);

        if (!state.UseCapsuleMode)
        {
            state.UseDeepCapsuleMode = false;
        }

        state.MaxTitleLength = PaperTitles.NormalizeMaxTitleLength(state.MaxTitleLength);

        state.SyncProvider = SyncProviders.Normalize(state.SyncProvider);
        state.SyncWebDavUrl = (state.SyncWebDavUrl ?? "").Trim();
        state.SyncWebDavUsername = (state.SyncWebDavUsername ?? "").Trim();
        state.SyncWebDavPassword ??= "";
        state.SyncWebDavRemotePath = string.IsNullOrWhiteSpace(state.SyncWebDavRemotePath) ? "/papertodo/data.json" : state.SyncWebDavRemotePath.Trim();
        state.SyncS3Endpoint = (state.SyncS3Endpoint ?? "").Trim();
        state.SyncS3Bucket = (state.SyncS3Bucket ?? "").Trim();
        state.SyncS3AccessKey = (state.SyncS3AccessKey ?? "").Trim();
        state.SyncS3SecretKey ??= "";
        state.SyncS3Region = string.IsNullOrWhiteSpace(state.SyncS3Region) ? "us-east-1" : state.SyncS3Region.Trim();
        state.SyncCustomServerUrl = (state.SyncCustomServerUrl ?? "").Trim();
        state.SyncCustomToken = (state.SyncCustomToken ?? "").Trim();
        state.LastSyncStatus ??= "";

        state.SyncTombstones ??= new List<SyncTombstone>();
        var tombstoneCutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds();
        state.SyncTombstones = state.SyncTombstones
            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Id) && t.DeletedAt >= tombstoneCutoff)
            .GroupBy(t => t.Id)
            .Select(g => g.OrderByDescending(t => t.DeletedAt).First())
            .ToList();

        if (!state.UseCapsuleMode || !state.UseDeepCapsuleMode)
        {
            state.UseCapsuleCollapseAll = false;
        }

        if (!state.UseCapsuleCollapseAll)
        {
            state.CapsuleCollapseAllActive = false;
        }
        state.CapsuleCollapseAllActiveQueues ??= new Dictionary<string, bool>();
        state.CapsuleCollapseAllActiveQueues = NormalizeCollapseAllActiveQueues(state.CapsuleCollapseAllActiveQueues);
        if (!state.UseCapsuleCollapseAll)
        {
            state.CapsuleCollapseAllActiveQueues.Clear();
        }
        else
        {
            foreach (var key in state.CapsuleCollapseAllActiveQueues.Keys.ToList())
            {
                if (!state.CapsuleCollapseAllActiveQueues[key])
                {
                    state.CapsuleCollapseAllActiveQueues.Remove(key);
                }
            }
            if (state.CapsuleCollapseAllActiveQueues.Count > 0)
            {
                state.CapsuleCollapseAllActive = true;
            }
        }

        var keepDeepCapsuleStartTopMargins = state.UseCapsuleMode && state.UseDeepCapsuleMode && state.UseCapsuleCollapseAll;
        state.DeepCapsuleStartTopMargin = keepDeepCapsuleStartTopMargins
            ? NormalizeDeepCapsuleStartTopMargin(state.DeepCapsuleStartTopMargin, state.DeepCapsuleMonitorDeviceName)
            : DeepCapsuleLayout.StartTopMargin;

        // Per-queue margins: drop NaN/inf; final clamping against each queue's live work area is
        // done at layout time (monitor set can change between sessions, so we don't over-normalize
        // here). A null dict (older config) becomes empty => every queue falls back to the global.
        state.DeepCapsuleQueueStartTopMargins ??= new Dictionary<string, double>();
        state.DeepCapsuleQueueStartTopMargins = NormalizeQueueStartTopMargins(state.DeepCapsuleQueueStartTopMargins);
        if (!keepDeepCapsuleStartTopMargins)
        {
            state.DeepCapsuleQueueStartTopMargins.Clear();
        }
        else
        {
            foreach (var key in state.DeepCapsuleQueueStartTopMargins.Keys.ToList())
            {
                var v = state.DeepCapsuleQueueStartTopMargins[key];
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    state.DeepCapsuleQueueStartTopMargins.Remove(key);
                }
            }
        }

        var usedPaperIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var paper in state.Papers)
        {
            if (string.IsNullOrWhiteSpace(paper.Id) || !usedPaperIds.Add(paper.Id))
            {
                paper.Id = NewUniqueId(usedPaperIds);
            }

            if (paper.Type != PaperTypes.Note && paper.Type != PaperTypes.Todo)
            {
                paper.Type = PaperTypes.Todo;
            }

            if (paper.IsPinnedToDesktop)
            {
                paper.IsVisible = true;
                paper.IsCollapsed = false;
                state.UseCapsuleMode = true;
                state.UseDeepCapsuleMode = true;
                state.ShowDeepCapsuleWhileExpanded = true;
            }

            // Per-paper queue identity. Migration: a capsule with no explicit side inherits the
            // legacy single global anchor, so existing docked capsules keep their current edge /
            // monitor. New papers default to the global side until dragged to a queue.
            paper.CapsuleSide = string.IsNullOrWhiteSpace(paper.CapsuleSide)
                ? state.DeepCapsuleSide
                : DeepCapsuleSides.Normalize(paper.CapsuleSide);
            var capsuleMonitorDeviceName = string.IsNullOrWhiteSpace(paper.CapsuleMonitorDeviceName)
                ? (state.DeepCapsuleMonitorDeviceName ?? "")
                : paper.CapsuleMonitorDeviceName.Trim();
            paper.CapsuleMonitorDeviceName = WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(capsuleMonitorDeviceName);

            paper.Title = PaperTitles.CleanCustomTitle(paper.Title, state.MaxTitleLength);
            paper.X = NormalizeCoordinate(paper.X, 120);
            paper.Y = NormalizeCoordinate(paper.Y, 120);
            if (double.IsNaN(paper.TextZoom) || double.IsInfinity(paper.TextZoom) || paper.TextZoom <= 0)
            {
                paper.TextZoom = 1.0;
            }
            paper.TextZoom = Math.Clamp(Math.Round(paper.TextZoom, 1), 0.5, 1.5);

            paper.Width = NormalizePaperDimension(
                paper.Width,
                paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultWidth : PaperLayoutDefaults.TodoDefaultWidth,
                PaperLayoutDefaults.MinWidth);
            paper.Height = NormalizePaperDimension(
                paper.Height,
                paper.Type == PaperTypes.Note ? PaperLayoutDefaults.NoteDefaultHeight : PaperLayoutDefaults.TodoDefaultHeight,
                PaperLayoutDefaults.MinHeight);

            if (paper.UpdatedAt <= 0)
            {
                paper.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            paper.Items ??= new List<PaperItem>();
            paper.Items = paper.Items
                .Where(item => item != null)
                .Cast<PaperItem>()
                .ToList();
            paper.Content ??= "";
            NormalizeNoteCanvasElements(paper);
            if (!state.UseCapsuleMode)
            {
                paper.IsCollapsed = false;
            }

            var usedItemIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < paper.Items.Count; i++)
            {
                var item = paper.Items[i];
                if (string.IsNullOrWhiteSpace(item.Id) || !usedItemIds.Add(item.Id))
                {
                    item.Id = NewUniqueId(usedItemIds);
                }

                item.Order = i;
                item.Text ??= "";
                item.TodoColumnCount = Math.Clamp(item.TodoColumnCount <= 0 ? 1 : item.TodoColumnCount, 1, 4);
                item.TodoExtraColumns ??= new List<string>();
                while (item.TodoExtraColumns.Count < item.TodoColumnCount - 1)
                {
                    item.TodoExtraColumns.Add("");
                }
                if (item.TodoExtraColumns.Count > item.TodoColumnCount - 1)
                {
                    item.TodoExtraColumns.RemoveRange(item.TodoColumnCount - 1, item.TodoExtraColumns.Count - (item.TodoColumnCount - 1));
                }
                for (var columnIndex = 0; columnIndex < item.TodoExtraColumns.Count; columnIndex++)
                {
                    item.TodoExtraColumns[columnIndex] ??= "";
                }
                item.TodoColumnWidths ??= new List<double>();
                while (item.TodoColumnWidths.Count < item.TodoColumnCount)
                {
                    item.TodoColumnWidths.Add(1);
                }
                if (item.TodoColumnWidths.Count > item.TodoColumnCount)
                {
                    item.TodoColumnWidths.RemoveRange(item.TodoColumnCount, item.TodoColumnWidths.Count - item.TodoColumnCount);
                }
                for (var columnIndex = 0; columnIndex < item.TodoColumnWidths.Count; columnIndex++)
                {
                    var width = item.TodoColumnWidths[columnIndex];
                    item.TodoColumnWidths[columnIndex] = double.IsNaN(width) || double.IsInfinity(width) || width <= 0
                        ? 1
                        : Math.Clamp(Math.Round(width, 3), 0.2, 8);
                }
                item.DueAtLocal = NormalizeDueAtLocal(item.DueAtLocal);
                NormalizeReminderInterval(item);

                if (item.UpdatedAt <= 0)
                {
                    item.UpdatedAt = paper.UpdatedAt > 0 ? paper.UpdatedAt : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                }
            }
        }

        var noteIds = state.Papers
            .Where(p => p.Type == PaperTypes.Note)
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in state.Papers.SelectMany(p => p.Items))
        {
            if (string.IsNullOrWhiteSpace(item.LinkedNoteId) ||
                !noteIds.Contains(item.LinkedNoteId))
            {
                item.LinkedNoteId = null;
            }
        }

        if (state.EnableTodoNoteLinks && state.HideLinkedNotesFromCapsules)
        {
            var linkedNoteIds = state.Papers
                .Where(p => p.Type == PaperTypes.Todo)
                .SelectMany(p => p.Items)
                .Select(item => item.LinkedNoteId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var note in state.Papers.Where(p => p.Type == PaperTypes.Note && linkedNoteIds.Contains(p.Id)))
            {
                note.IsCollapsed = false;
            }
        }
    }

    private static void NormalizeNoteCanvasElements(PaperData paper)
    {
        paper.NoteCanvasElements ??= new List<NoteCanvasElement>();
        paper.NoteCanvasElements = paper.NoteCanvasElements
            .Where(element => element != null)
            .Cast<NoteCanvasElement>()
            .ToList();

        var usedElementIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < paper.NoteCanvasElements.Count; i++)
        {
            var element = paper.NoteCanvasElements[i];
            if (string.IsNullOrWhiteSpace(element.Id) || !usedElementIds.Add(element.Id))
            {
                element.Id = NewUniqueId(usedElementIds);
            }

            element.Type = NoteCanvasElementTypes.Normalize(element.Type);
            element.Text ??= "";
            element.X = Math.Clamp(NormalizeCoordinate(element.X, 32), -2000, 8000);
            element.Y = Math.Clamp(NormalizeCoordinate(element.Y, 32), -2000, 8000);
            element.Width = Math.Clamp(NormalizePaperDimension(element.Width, 220, 72), 72, 1600);
            element.Height = Math.Clamp(NormalizePaperDimension(element.Height, 110, 48), 48, 1600);
            if (element.ZIndex <= 0)
            {
                element.ZIndex = (i + 1) * 10;
            }
        }
    }

    private static string NewUniqueId(HashSet<string> usedIds)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (!usedIds.Add(id));

        return id;
    }

    public static string NormalizeHotKeyForSettings(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length > 64 ? text[..64] : text;
    }

    private static double NormalizeCoordinate(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : value;
    }

    private static double NormalizePaperDimension(double value, double fallback, double min)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value < min
            ? fallback
            : value;
    }

    private static Dictionary<string, bool> NormalizeCollapseAllActiveQueues(Dictionary<string, bool> source)
    {
        var normalized = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeQueueKey(key);
            if (!normalized.ContainsKey(normalizedKey) || string.Equals(key, normalizedKey, StringComparison.Ordinal))
            {
                normalized[normalizedKey] = value;
            }
        }

        return normalized;
    }

    private static Dictionary<string, double> NormalizeQueueStartTopMargins(Dictionary<string, double> source)
    {
        var normalized = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            var normalizedKey = NormalizeQueueKey(key);
            if (!normalized.ContainsKey(normalizedKey) || string.Equals(key, normalizedKey, StringComparison.Ordinal))
            {
                normalized[normalizedKey] = value;
            }
        }

        return normalized;
    }

    private static string NormalizeQueueKey(string? key)
    {
        var value = (key ?? "").Trim();
        var separator = value.LastIndexOf('|');
        if (separator < 0)
        {
            return QueueKey("", value);
        }

        var monitorDeviceName = value[..separator];
        var side = value[(separator + 1)..];
        return QueueKey(monitorDeviceName, side);
    }

    private static string QueueKey(string? monitorDeviceName, string? side)
        => $"{WindowWorkAreaHelper.NormalizeQueueMonitorDeviceName(monitorDeviceName)}|{(side == DeepCapsuleSides.Left ? DeepCapsuleSides.Left : DeepCapsuleSides.Right)}";

    private static double NormalizeDeepCapsuleStartTopMargin(double value, string monitorDeviceName)
    {
        var area = DeepCapsuleLayout.WorkAreaForQueue(monitorDeviceName);
        return DeepCapsuleLayout.NormalizeStartTopMargin(value, area, 1);
    }

    private static string? NormalizeDueAtLocal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!DateTime.TryParse(
                value.Trim(),
                CultureInfo.CurrentCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var dueAt))
        {
            return null;
        }

        return dueAt.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static double NormalizeLineSpacing(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return 1.0;
        }

        return Math.Clamp(Math.Round(value, 2), 0.8, 5.0);
    }

    private static void NormalizeReminderInterval(PaperItem item)
    {
        if (item.ReminderIntervalValue is not int value || value <= 0)
        {
            item.ReminderIntervalValue = null;
            item.ReminderIntervalUnit = null;
            return;
        }

        item.ReminderIntervalValue = Math.Clamp(value, 1, 240);
        item.ReminderIntervalUnit = TodoReminderIntervalUnits.Normalize(item.ReminderIntervalUnit);
    }
}
