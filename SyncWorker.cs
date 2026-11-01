using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PaperTodo;

public sealed class SyncWorker : IDisposable
{
    private readonly AppController _controller;
    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _periodicTimer;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _isDisposed;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeSync;

    public event Action<bool, string>? SyncStateChanged;

    public SyncWorker(AppController controller)
    {
        _controller = controller;

        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await PerformSyncAsync(isSilent: true);
        };

        _periodicTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _periodicTimer.Tick += async (_, _) =>
        {
            if (_controller.State.SyncEnabled)
            {
                await PerformSyncAsync(isSilent: true);
            }
        };

        if (_controller.State.SyncEnabled)
        {
            _periodicTimer.Start();
            Task.Delay(1500).ContinueWith(_ =>
            {
                if (!_isDisposed && _controller.State.SyncEnabled)
                {
                    Application.Current?.Dispatcher?.InvokeAsync(async () =>
                    {
                        await PerformSyncAsync(isSilent: true);
                    });
                }
            });
        }
    }

    public void NotifyLocalDataChanged()
    {
        if (_isDisposed || !_controller.State.SyncEnabled) return;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public void OnSyncConfigChanged()
    {
        _activeSync?.Cancel();
        if (_controller.State.SyncEnabled)
        {
            _periodicTimer.Stop();
            _periodicTimer.Start();
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
        else
        {
            _debounceTimer.Stop();
            _periodicTimer.Stop();
        }
    }

    public async Task<SyncTestResult> PerformSyncAsync(bool isSilent)
    {
        if (_isDisposed || !_controller.State.SyncEnabled)
        {
            return new SyncTestResult(false, "Sync is disabled");
        }

        if (!await _syncLock.WaitAsync(0))
        {
            return new SyncTestResult(false, "Sync already in progress");
        }

        try
        {
            SyncStateChanged?.Invoke(true, Strings.Get("SyncInProgress"));
            using var provider = SyncProviderFactory.Create(_controller.State);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _activeSync = operation;
            var token = operation.Token;

            for (var attempt = 0; ; attempt++)
            {
                try { return await SyncOnceAsync(provider, token); }
                catch (SyncConflictException) when (attempt < 2) { }
            }
        }
        catch (OperationCanceledException)
        {
            var message = Strings.Get("SyncCancelled");
            SyncStateChanged?.Invoke(false, message);
            return new SyncTestResult(false, message);
        }
        catch (Exception ex)
        {
            var msg = string.Format(Strings.Get("SyncFailed"), ex.Message);
            _controller.State.LastSyncStatus = msg;
            SyncStateChanged?.Invoke(false, msg);
            return new SyncTestResult(false, msg);
        }
        finally
        {
            _activeSync = null;
            _syncLock.Release();
        }
    }

    private async Task<SyncTestResult> SyncOnceAsync(ISyncStorageProvider provider, CancellationToken token)
    {
        // 1. Download remote data
        var remoteJson = await provider.DownloadDataJsonAsync(token);
        token.ThrowIfCancellationRequested();
        if (!_controller.State.SyncEnabled) return new SyncTestResult(false, "Sync is disabled");
        _controller.PrepareSyncState();

        AppState? remoteState = null;
        if (remoteJson != null)
        {
            try
            {
                remoteState = SyncProtocol.Deserialize(remoteJson);
            }
            catch (Exception ex)
            {
                var msg = string.Format(Strings.Get("SyncFailed"), "Remote corrupted: " + ex.Message);
                _controller.State.LastSyncStatus = msg;
                SyncStateChanged?.Invoke(false, msg);
                return new SyncTestResult(false, msg);
            }
        }

        var localState = _controller.State;
        var needUpload = false;

        if (remoteState != null)
        {
            // 2. Merge local and remote
            var mergeResult = DataMergeEngine.Merge(localState, remoteState);

            if (mergeResult.HasChangesToLocal)
            {
                _controller.ApplySyncMergeResult(mergeResult.MergedState);
            }

            if (mergeResult.HasChangesToRemote || mergeResult.HasChangesToLocal || SyncProtocol.NeedsRewrite(remoteJson!))
            {
                needUpload = true;
            }
        }
        else
        {
            // Remote has no data yet -> upload local data
            needUpload = true;
        }

        if (needUpload)
        {
            var jsonToUpload = SyncProtocol.Serialize(_controller.State);
            if (!await provider.UploadDataJsonAsync(jsonToUpload, token))
                throw new InvalidOperationException("Remote storage did not confirm the upload.");
        }

        token.ThrowIfCancellationRequested();
        _controller.State.LastSyncTimeUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _controller.State.LastSyncStatus = Strings.Get("SyncSuccess");
        _controller.SaveNow(notifySync: false);

        SyncStateChanged?.Invoke(false, Strings.Get("SyncSuccess"));
        return new SyncTestResult(true, Strings.Get("SyncSuccess"));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _lifetime.Cancel();
        _debounceTimer.Stop();
        _periodicTimer.Stop();
        // An in-flight continuation still owns the semaphore and releases it in finally.
    }
}
