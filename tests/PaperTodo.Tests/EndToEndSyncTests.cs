using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaperTodo;

namespace PaperTodo.Tests;

[TestClass]
public class EndToEndSyncTests
{
    private static Process? _serverProcess;
    private static string _tempDataDir = "";
    private const int Port = 18099;
    private const string Token = "e2e-secret-token";
    private static readonly string ServerUrl = $"http://127.0.0.1:{Port}";

    [ClassInitialize]
    public static void StartServer(TestContext context)
    {
        _tempDataDir = Path.Combine(Path.GetTempPath(), "papertodo_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataDir);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        string? serverExe = null;
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "bin", "win-x64", "papertodo-server.exe");
            if (File.Exists(candidate))
            {
                serverExe = candidate;
                break;
            }
            current = current.Parent;
        }

        if (serverExe == null || !File.Exists(serverExe))
        {
            Assert.Inconclusive("Standalone server binary not found");
        }

        var psi = new ProcessStartInfo
        {
            FileName = serverExe,
            Arguments = $"-port={Port} -token={Token} -data-dir=\"{_tempDataDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _serverProcess = Process.Start(psi);
        Thread.Sleep(800); // Wait for server to bind
    }

    [ClassCleanup]
    public static void StopServer()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            try
            {
                _serverProcess.Kill();
                _serverProcess.WaitForExit(3000);
            }
            catch { }
        }

        if (Directory.Exists(_tempDataDir))
        {
            try { Directory.Delete(_tempDataDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task TestEndToEnd_PC_Upload_Mobile_Edit_PC_Merge_Success()
    {
        // 1. PC Client uploads initial state
        var pcState = new AppState
        {
            Papers = new List<PaperData>
            {
                new()
                {
                    Id = "paper_pc",
                    Title = "PC Todo Paper",
                    Items = new List<PaperItem>
                    {
                        new() { Id = "item_1", Text = "Buy groceries", Done = false, Order = 0 }
                    }
                }
            }
        };

        var pcProvider = new CustomHttpSyncProvider(ServerUrl, Token);
        var testRes = await pcProvider.TestConnectionAsync();
        Assert.IsTrue(testRes.Success, "Ping server should succeed: " + testRes.Message);

        var store = new StateStore();
        var pcJson = store.SerializeState(pcState);
        var uploadRes = await pcProvider.UploadDataJsonAsync(pcJson);
        Assert.IsTrue(uploadRes, "Initial upload should succeed");

        // 2. Mobile Client downloads remote state
        var mobileProvider = new CustomHttpSyncProvider(ServerUrl, Token);
        var downloadedJson = await mobileProvider.DownloadDataJsonAsync();
        Assert.IsNotNull(downloadedJson);
        var mobileState = store.DeserializeState(downloadedJson);
        Assert.IsNotNull(mobileState);
        Assert.AreEqual(1, mobileState.Papers.Count);

        // Mobile marks item_1 Done and adds item_2
        var paper = mobileState.Papers[0];
        paper.Items[0].Done = true;
        paper.Items[0].UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        paper.Items.Add(new PaperItem
        {
            Id = "item_2",
            Text = "Added from Mobile",
            Done = false,
            Order = 1,
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        paper.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var mobileJson = store.SerializeState(mobileState);
        var mobileUploadRes = await mobileProvider.UploadDataJsonAsync(mobileJson);
        Assert.IsTrue(mobileUploadRes, "Mobile upload should succeed");

        // 3. PC Client pulls and merges
        var latestRemoteJson = await pcProvider.DownloadDataJsonAsync();
        Assert.IsNotNull(latestRemoteJson);
        var latestRemoteState = store.DeserializeState(latestRemoteJson);
        Assert.IsNotNull(latestRemoteState);

        var mergeResult = DataMergeEngine.Merge(pcState, latestRemoteState);

        // 4. Verification
        var mergedPaper = mergeResult.MergedState.Papers.FirstOrDefault(p => p.Id == "paper_pc");
        Assert.IsNotNull(mergedPaper);
        Assert.AreEqual(2, mergedPaper.Items.Count, "Should contain both items after sync");

        var item1 = mergedPaper.Items.Find(i => i.Id == "item_1");
        var item2 = mergedPaper.Items.Find(i => i.Id == "item_2");

        Assert.IsNotNull(item1);
        Assert.IsTrue(item1.Done, "item_1 should be marked Done by Mobile");

        Assert.IsNotNull(item2);
        Assert.AreEqual("Added from Mobile", item2.Text, "item_2 should exist from Mobile");
    }

    [TestMethod]
    public async Task TestEndToEnd_ServerOffline_DoesNotCrash()
    {
        // Connecting to an offline server port should return graceful error, never crash
        var offlineProvider = new CustomHttpSyncProvider("http://127.0.0.1:19999", "any");
        var res = await offlineProvider.TestConnectionAsync();
        Assert.IsFalse(res.Success);
        Assert.IsNotNull(res.Message);
    }

    [TestMethod]
    public async Task WrongTokenCannotPassConnectionTest()
    {
        using var provider = new CustomHttpSyncProvider(ServerUrl, "wrong-token");
        Assert.IsFalse((await provider.TestConnectionAsync()).Success);
    }

    [TestMethod]
    public async Task StaleUploadMustReadAndMergeAgain()
    {
        using var a = new CustomHttpSyncProvider(ServerUrl, Token);
        using var b = new CustomHttpSyncProvider(ServerUrl, Token);
        var state = new AppState { Papers = new() { new() { Id = "conditional", Content = "seed", UpdatedAt = 10 } } };
        await a.UploadDataJsonAsync(SyncProtocol.Serialize(state));
        await a.DownloadDataJsonAsync();
        await b.DownloadDataJsonAsync();
        state.Papers[0].Content = "newer"; state.Papers[0].UpdatedAt = 20;
        await a.UploadDataJsonAsync(SyncProtocol.Serialize(state));
        await Assert.ThrowsExceptionAsync<SyncConflictException>(() => b.UploadDataJsonAsync(SyncProtocol.Serialize(state)));
        var latest = SyncProtocol.Deserialize((await b.DownloadDataJsonAsync())!);
        Assert.AreEqual("newer", latest.Papers[0].Content);
        Assert.IsTrue(await b.UploadDataJsonAsync(SyncProtocol.Serialize(latest)));
    }
}
