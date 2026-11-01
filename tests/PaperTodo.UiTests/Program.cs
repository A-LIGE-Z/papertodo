using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using PaperTodo;

internal static class Program
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); Console.WriteLine("PASS " + description); }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(() => frame.Continue = false, DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
    [STAThread]
    static int Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "evidence");
        Directory.CreateDirectory(output);
        if (args.Contains("--verify-roundtrip"))
        {
            var before = SyncProtocol.Deserialize(File.ReadAllText(Path.Combine(output, "cross-platform-fixture.json")));
            var phone = SyncProtocol.Deserialize(File.ReadAllText(Path.Combine(output, "phone-roundtrip.json")));
            var merged = DataMergeEngine.Merge(before, phone).MergedState;
            Check(merged.Papers.Single(p => p.Id == "note").NoteCanvasElements.Count == 1, "Dart roundtrip retains code canvas");
            var item = merged.Papers.Single(p => p.Id == "todo").Items.Single(i => i.Id == "item");
            Check(item.Done && item.TodoColumnWidths.SequenceEqual(new[] { 0.3, 0.7 }), "Dart edit and column widths reach Windows together");
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestResources.xaml"));
            var resources = new XElement(ns + "ResourceDictionary", xaml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration),
                xaml.Root.Element(ns + "Application.Resources")!.Elements());
            app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
            using var controller = new AppController();
            controller.State.Papers.Clear(); controller.State.SyncTombstones.Clear();
            controller.State.SyncEnabled = false; controller.State.UseCapsuleMode = false;
            controller.State.UseDeepCapsuleMode = false; controller.State.EnableAnimations = false;
            var note = new PaperData { Id = "note", Type = "note", Content = "before", UpdatedAt = 10,
                NoteCanvasElements = new() { new() { Id = "code", Text = "preserved code" } } };
            var todo = new PaperData { Id = "todo", Type = "todo", UpdatedAt = 10,
                Items = new() { new() { Id = "item", Text = "task", UpdatedAt = 10,
                    TodoColumnCount = 2, TodoExtraColumns = new() { "extra" }, TodoColumnWidths = new() { 0.3, 0.7 } } } };
            controller.State.Papers.AddRange(new[] { note, todo });
            var noteWindow = controller.GetOrCreatePaperWindow(note);
            var todoWindow = controller.GetOrCreatePaperWindow(todo);
            var incoming = SyncProtocol.Clone(controller.State);
            incoming.Papers[0].Content = "remote"; incoming.Papers[0].UpdatedAt = 20;
            controller.ApplySyncMergeResult(DataMergeEngine.Merge(controller.State, incoming).MergedState);
            var editor = (MarkdownTextBox)typeof(PaperWindow).GetField("_noteBox", Private)!.GetValue(noteWindow)!;
            editor.Document.Replace(0, editor.Document.TextLength, "local edit after sync");
            controller.UpdatePaperTitle(note, "local");
            note.X = 345; note.Width = 420;
            todoWindow.AddItemAfter(todo.Items[0], "added after sync");
            controller.SaveNow(sync: true, notifySync: false);
            var saved = controller.Store.DeserializeState(File.ReadAllText(controller.Store.FilePath))!;
            Check(ReferenceEquals(note, controller.State.Papers[0]), "live paper identity survives sync");
            Check(saved.Papers[0].Content == editor.Text && saved.Papers[0].Content == "local edit after sync", "actual Markdown editor changes reach disk after sync");
            Check(saved.Papers[0].Title == "local" && saved.Papers[0].X == 345 && saved.Papers[0].Width == 420, "title and geometry reach disk after sync");
            Check(saved.Papers[1].Items.Any(i => i.Text == "added after sync"), "actual todo insertion reaches disk after sync");

            var outline = new OutlineWindow(controller);
            typeof(OutlineWindow).GetField("_selectedPaperId", Private)!.SetValue(outline, note.Id);
            outline.RefreshContent();
            var hosts = (Dictionary<string, Grid>)typeof(OutlineWindow).GetField("_noteHosts", Private)!.GetValue(outline)!;
            var outlineEditor = hosts[note.Id].Children.OfType<MarkdownTextBox>().Single();
            var originalText = outlineEditor.Text;
            outlineEditor.Document.Insert(outlineEditor.Document.TextLength, " outline edit");
            var undoStack = outlineEditor.Document.UndoStack;
            typeof(OutlineWindow).GetField("_notePreviewMode", Private)!.SetValue(outline, true);
            outline.RefreshContent();
            Check(ReferenceEquals(outlineEditor, hosts[note.Id].Children.OfType<MarkdownTextBox>().Single()), "outline editing and preview use one editor");
            Check(outlineEditor.IsPreviewMode && ReferenceEquals(outlineEditor.Document.UndoStack, undoStack) && undoStack.CanUndo, "preview retains undo history");
            controller.State.Theme = "dark"; Theme.Invalidate(); outline.RefreshThemeAndContent();
            Check(ReferenceEquals(outlineEditor, hosts[note.Id].Children.OfType<MarkdownTextBox>().Single()), "theme change retains outline editor");
            undoStack.Undo();
            Check(outlineEditor.Text == originalText, "undo works after preview and theme change");
            undoStack.Redo();
            outline.Close();
            noteWindow.CloseForReal(); todoWindow.CloseForReal();
            Pump();
            var fixture = new AppState { Papers = new() { note, todo }, SyncS3SecretKey = "test-secret-must-not-leak" };
            File.WriteAllText(Path.Combine(output, "cross-platform-fixture.json"), SyncProtocol.Serialize(fixture));
            var roundtrip = Path.Combine(output, "phone-roundtrip.json");
            if (File.Exists(roundtrip))
            {
                var phone = SyncProtocol.Deserialize(File.ReadAllText(roundtrip));
                var merged = DataMergeEngine.Merge(fixture, phone).MergedState;
                Check(merged.Papers.Single(p => p.Id == "note").NoteCanvasElements.Count == 1, "actual phone roundtrip retains canvas");
                Check(merged.Papers.Single(p => p.Id == "todo").Items.Single(i => i.Id == "item").TodoColumnWidths.SequenceEqual(new[] { 0.3, 0.7 }), "actual phone roundtrip retains column widths");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { app.Shutdown(); }
    }
}
