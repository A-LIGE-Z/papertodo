using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaperTodo;

namespace PaperTodo.Tests;

[TestClass]
public class DataMergeEngineTests
{
    [TestMethod]
    public void TestLegacyDataJsonDeserialization_NoUpdatedAt()
    {
        // Old legacy data.json without updatedAt or sync fields
        var legacyJson = """
        {
          "papers": [
            {
              "id": "paper1",
              "type": "todo",
              "title": "Legacy Paper",
              "x": 100,
              "y": 100,
              "width": 280,
              "height": 340,
              "items": [
                {
                  "id": "item1",
                  "text": "Task 1",
                  "done": false,
                  "order": 0
                }
              ]
            }
          ]
        }
        """;

        var store = new StateStore();
        var state = store.DeserializeState(legacyJson);

        Assert.IsNotNull(state);
        Assert.AreEqual(1, state.Papers.Count);
        var p = state.Papers[0];
        Assert.AreEqual("paper1", p.Id);
        Assert.IsTrue(p.UpdatedAt > 0, "Paper UpdatedAt should have been populated");
        Assert.AreEqual(1, p.Items.Count);
        var item = p.Items[0];
        Assert.AreEqual("item1", item.Id);
        Assert.IsTrue(item.UpdatedAt > 0, "Item UpdatedAt should have been populated");
    }

    [TestMethod]
    public void TestMergeScenario1_ConcurrentNonConflictingEdits()
    {
        // PC marks item1 Done; Phone edits item2 Text
        var baseTime = 1700000000000L;

        var localState = new AppState
        {
            Papers = new List<PaperData>
            {
                new()
                {
                    Id = "paper1",
                    Title = "Todos",
                    UpdatedAt = baseTime + 100,
                    Items = new List<PaperItem>
                    {
                        new() { Id = "item1", Text = "Buy milk", Done = true, UpdatedAt = baseTime + 200, Order = 0 },
                        new() { Id = "item2", Text = "Read book", Done = false, UpdatedAt = baseTime, Order = 1 }
                    }
                }
            }
        };

        var remoteState = new AppState
        {
            Papers = new List<PaperData>
            {
                new()
                {
                    Id = "paper1",
                    Title = "Todos",
                    UpdatedAt = baseTime + 150,
                    Items = new List<PaperItem>
                    {
                        new() { Id = "item1", Text = "Buy milk", Done = false, UpdatedAt = baseTime, Order = 0 },
                        new() { Id = "item2", Text = "Read book chapter 2", Done = false, UpdatedAt = baseTime + 300, Order = 1 }
                    }
                }
            }
        };

        var result = DataMergeEngine.Merge(localState, remoteState);
        Assert.AreEqual(1, result.MergedState.Papers.Count);
        var paper = result.MergedState.Papers[0];
        Assert.AreEqual(2, paper.Items.Count);

        var item1 = paper.Items.Find(i => i.Id == "item1");
        var item2 = paper.Items.Find(i => i.Id == "item2");

        Assert.IsNotNull(item1);
        Assert.IsTrue(item1.Done, "item1 should be done (from local, newer)");

        Assert.IsNotNull(item2);
        Assert.AreEqual("Read book chapter 2", item2.Text, "item2 should have updated text (from remote, newer)");
    }

    [TestMethod]
    public void TestMergeScenario2_ItemDeletionPropagationViaTombstone()
    {
        // PC deleted item2 (created tombstone). Phone was offline and still has item2.
        var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 100000L;

        var localState = new AppState
        {
            Papers = new List<PaperData>
            {
                new()
                {
                    Id = "paper1",
                    UpdatedAt = baseTime + 100,
                    Items = new List<PaperItem>
                    {
                        new() { Id = "item1", Text = "Buy milk", UpdatedAt = baseTime, Order = 0 }
                    }
                }
            },
            SyncTombstones = new List<SyncTombstone>
            {
                new() { Id = "item2", Type = "item", ParentPaperId = "paper1", DeletedAt = baseTime + 200 }
            }
        };

        var remoteState = new AppState
        {
            Papers = new List<PaperData>
            {
                new()
                {
                    Id = "paper1",
                    UpdatedAt = baseTime,
                    Items = new List<PaperItem>
                    {
                        new() { Id = "item1", Text = "Buy milk", UpdatedAt = baseTime, Order = 0 },
                        new() { Id = "item2", Text = "Delete me", UpdatedAt = baseTime, Order = 1 }
                    }
                }
            }
        };

        var result = DataMergeEngine.Merge(localState, remoteState);
        var paper = result.MergedState.Papers[0];

        Assert.AreEqual(1, paper.Items.Count, "item2 should have been deleted");
        Assert.AreEqual("item1", paper.Items[0].Id);
    }

    [TestMethod]
    public void TestMergeScenario3_BothSidesAddPapers()
    {
        // PC added paper_local, Phone added paper_remote
        var baseTime = 1700000000000L;

        var localState = new AppState
        {
            Papers = new List<PaperData>
            {
                new() { Id = "p_local", Title = "Local Paper", UpdatedAt = baseTime + 10 }
            }
        };

        var remoteState = new AppState
        {
            Papers = new List<PaperData>
            {
                new() { Id = "p_remote", Title = "Remote Paper", UpdatedAt = baseTime + 20 }
            }
        };

        var result = DataMergeEngine.Merge(localState, remoteState);
        Assert.AreEqual(2, result.MergedState.Papers.Count);
        Assert.IsTrue(result.MergedState.Papers.Any(p => p.Id == "p_local"));
        Assert.IsTrue(result.MergedState.Papers.Any(p => p.Id == "p_remote"));
    }

    [TestMethod]
    public void TestMergeScenario4_EditAfterDeleteResurrectsItem()
    {
        // PC deleted item1 at T=100. Phone edited item1 at T=200. Phone's edit should win.
        var baseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 100000L;

        var localState = new AppState
        {
            Papers = new List<PaperData>
            {
                new() { Id = "paper1", UpdatedAt = baseTime }
            },
            SyncTombstones = new List<SyncTombstone>
            {
                new() { Id = "item1", Type = "item", ParentPaperId = "paper1", DeletedAt = baseTime + 100 }
            }
        };

        var remoteState = new AppState
        {
            Papers = new List<PaperData>
            {
                new()
                {
                    Id = "paper1",
                    UpdatedAt = baseTime + 200,
                    Items = new List<PaperItem>
                    {
                        new() { Id = "item1", Text = "Resurrected Task", UpdatedAt = baseTime + 200, Order = 0 }
                    }
                }
            }
        };

        var result = DataMergeEngine.Merge(localState, remoteState);
        var paper = result.MergedState.Papers[0];

        Assert.AreEqual(1, paper.Items.Count, "item1 should be resurrected by later edit");
        Assert.AreEqual("Resurrected Task", paper.Items[0].Text);
    }
}
