using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PaperTodo.Tests;

[TestClass]
public class SyncReliabilityTests
{
    [TestMethod]
    public void SharedPayloadExcludesCredentialsAndDeviceLayout()
    {
        var state = new AppState { SyncWebDavPassword = "private", SyncS3SecretKey = "private", SyncCustomToken = "private",
            Papers = new() { new() { Id = "p", X = 777, Content = "body" } } };
        var json = SyncProtocol.Serialize(state);
        using var doc = JsonDocument.Parse(json);
        Assert.IsFalse(json.Contains("private"));
        Assert.IsFalse(doc.RootElement.GetProperty("papers")[0].TryGetProperty("x", out _));
        Assert.AreEqual("body", SyncProtocol.Deserialize(json).Papers[0].Content);
    }

    [TestMethod]
    public void ApplyingMergePreservesPaperAndItemReferences()
    {
        var item = new PaperItem { Id = "i", Text = "old", UpdatedAt = 10 };
        var paper = new PaperData { Id = "p", Content = "old", UpdatedAt = 10, X = 345, Items = new() { item } };
        var local = new AppState { Papers = new() { paper } };
        var remote = SyncProtocol.Clone(local);
        remote.Papers[0].Content = "remote";
        remote.Papers[0].UpdatedAt = 20;
        remote.Papers[0].Items[0].Text = "remote";
        remote.Papers[0].Items[0].UpdatedAt = 20;
        SyncProtocol.Apply(local, DataMergeEngine.Merge(local, remote).MergedState);
        Assert.AreSame(paper, local.Papers[0]);
        Assert.AreSame(item, local.Papers[0].Items[0]);
        paper.Content = "edited after merge";
        item.Text = "edited after merge";
        var saved = SyncProtocol.Deserialize(SyncProtocol.Serialize(local));
        Assert.AreEqual("edited after merge", saved.Papers[0].Content);
        Assert.AreEqual("edited after merge", saved.Papers[0].Items[0].Text);
        Assert.AreEqual(345, paper.X);
    }

    [TestMethod]
    public void MissingLegacyFieldsDoNotDeleteCanvasOrColumnWidths()
    {
        var local = new AppState { Papers = new() { new() { Id = "p", UpdatedAt = 10,
            NoteCanvasElements = new() { new() { Text = "important" } },
            Items = new() { new() { Id = "i", UpdatedAt = 10, TodoColumnWidths = new() { 0.3, 0.7 } } } } } };
        var remote = SyncProtocol.Deserialize("""{"papers":[{"id":"p","content":"phone","updatedAt":20,"items":[{"id":"i","done":true,"updatedAt":20}]}]}""");
        var merged = DataMergeEngine.Merge(local, remote).MergedState.Papers[0];
        Assert.AreEqual("phone", merged.Content);
        Assert.AreEqual("important", merged.NoteCanvasElements.Single().Text);
        CollectionAssert.AreEqual(new[] { 0.3, 0.7 }, merged.Items[0].TodoColumnWidths);
    }

    [TestMethod]
    public void ExplicitEmptyAndNullFieldsDoDeleteContent()
    {
        var local = new AppState { Papers = new() { new() { Id = "p", UpdatedAt = 10,
            NoteCanvasElements = new() { new() { Text = "removed" } },
            Items = new() { new() { Id = "i", UpdatedAt = 10, LinkedNoteId = "n" } } } } };
        var remote = SyncProtocol.Deserialize("""{"papers":[{"id":"p","noteCanvasElements":[],"updatedAt":20,"items":[{"id":"i","linkedNoteId":null,"updatedAt":20}]}]}""");
        var merged = DataMergeEngine.Merge(local, remote).MergedState.Papers[0];
        Assert.AreEqual(0, merged.NoteCanvasElements.Count);
        Assert.IsNull(merged.Items[0].LinkedNoteId);
    }

    [TestMethod]
    public void ItemOnlyChangesAndTombstonesAreDetectedWithoutMutatingInputs()
    {
        var a = new AppState { Papers = new() { new() { Id = "p", UpdatedAt = 10,
            Items = new() { new() { Id = "i", Text = "new", UpdatedAt = 20 } } } } };
        var b = SyncProtocol.Clone(a); b.Papers[0].Items[0].Text = "old"; b.Papers[0].Items[0].UpdatedAt = 10;
        var before = SyncProtocol.Serialize(b);
        var merged = DataMergeEngine.Merge(a, b);
        Assert.IsTrue(merged.HasChangesToRemote);
        Assert.AreEqual(before, SyncProtocol.Serialize(b));
        a.SyncTombstones.Add(new() { Id = "ancient", Type = "paper", DeletedAt = 1 });
        Assert.IsTrue(DataMergeEngine.Merge(a, b).MergedState.SyncTombstones.Any(t => t.Id == "ancient"));
    }

    [TestMethod]
    public void EqualTimestampsConvergeAndUnknownFieldsSurvive()
    {
        var a = SyncProtocol.Deserialize("""{"papers":[{"id":"p","content":"中文😀","updatedAt":10,"future":{"value":1}}]}""");
        var b = SyncProtocol.Deserialize("""{"papers":[{"id":"p","content":"other","updatedAt":10}]}""");
        var ab = DataMergeEngine.Merge(a, b).MergedState;
        var ba = DataMergeEngine.Merge(b, a).MergedState;
        Assert.AreEqual(SyncProtocol.Serialize(ab), SyncProtocol.Serialize(ba));
        Assert.IsTrue(SyncProtocol.Serialize(ab).Contains("future"));
    }

    [TestMethod]
    public void SaveBoundaryTracksTitlesDueTimesColumnsAndDeletionsButNotGeometry()
    {
        var state = new AppState { Papers = new() { new() { Id = "p", UpdatedAt = 10,
            Items = new() { new() { Id = "i", UpdatedAt = 10 } } } } };
        var tracker = new SyncChangeTracker(); tracker.Capture(state);
        state.Papers[0].X = 800; tracker.Update(state);
        Assert.AreEqual(10, state.Papers[0].UpdatedAt);
        state.Papers[0].Title = "changed";
        state.Papers[0].Items[0].DueAtLocal = "2027-01-01T09:00:00";
        state.Papers[0].Items[0].TodoExtraColumns.Add("new column");
        tracker.Update(state);
        Assert.IsTrue(state.Papers[0].UpdatedAt > 10);
        var itemTime = state.Papers[0].Items[0].UpdatedAt;
        Assert.IsTrue(itemTime > 10);
        state.Papers[0].Items.Clear(); tracker.Update(state);
        Assert.IsTrue(state.SyncTombstones.Single().DeletedAt > itemTime);
    }
}
