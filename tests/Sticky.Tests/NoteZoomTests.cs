using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;
using Sticky.App.Editor;
using Sticky.Core;

internal static partial class Program
{
    private static void NoteZoomLifecycle()
    {
        var path = Path.Combine(_root, "note-zoom");
        var workspace = new StickyWorkspace(path);
        var bitmap = BitmapSource.Create(100, 20, 96, 96, PixelFormats.Bgr24, null, new byte[6000], 300);
        var asset = DocumentCodec.StoreBitmap(bitmap, workspace.Store);
        var rich = new Note { Blocks = [
            new NoteBlock { Inlines = [new NoteInline { Text = "富文本正文", FontSize = 22, Bold = true }] },
            new NoteBlock { Inlines = [new NoteInline { Kind = "image", AssetId = asset, Width = 480 }] },
            .. Enumerable.Range(1, 40).Select(i => new NoteBlock { Inlines = [new NoteInline { Text = $"段落 {i}：这里是用于检查滚动位置的中文正文。" }] })
        ] };
        var markdown = new Note();
        markdown.SetMarkdown("## Markdown\n\n编辑时保留选区与源码");
        foreach (var note in new[] { rich, markdown })
        {
            workspace.Store.Save(note);
            workspace.Notes.Add(note);
        }
        var richPath = Path.Combine(workspace.Store.NotesDirectory, rich.Id + ".json");
        var markdownPath = Path.Combine(workspace.Store.NotesDirectory, markdown.Id + ".json");
        var richBytes = File.ReadAllBytes(richPath);
        var markdownBytes = File.ReadAllBytes(markdownPath);

        try
        {
            workspace.GetWindowState(rich.Id).Width = 280;
            workspace.OpenNote(rich);
            var window = workspace.Windows[rich.Id];
            var editor = (RichTextBox)window.FindName("Editor");
            var badge = (Border)window.FindName("ZoomBadge");
            var label = (TextBlock)window.FindName("ZoomBadgeText");
            var header = (Border)window.FindName("HeaderBar");
            var document = editor.Document;
            var run = ((Paragraph)document.Blocks.FirstBlock).Inlines.OfType<Run>().Single();
            var image = (Image)((Paragraph)document.Blocks.ElementAt(1)).Inlines.OfType<InlineUIContainer>().Single().Child;
            Pump(50);
            var headerHeight = header.ActualHeight;
            var originalWidth = editor.ActualWidth;
            var eventsBeforeZoom = workspace.Store.History.ReadTimeline().Events.Count;

            ZoomKey(editor, Key.OemPlus);
            Check(ZoomPercent(window) == 110 && label.Text == "110%" && badge.IsVisible && !window.IsEditing, "Ctrl+= did not zoom the reading view or show its current ratio.");
            Pump(1100);
            ZoomWheel(editor, 120);
            Check(ZoomPercent(window) == 120 && label.Text == "120%", "Ctrl+wheel did not increase the zoom.");
            Pump(1100);
            Check(badge.IsVisible, "The badge disappeared before two seconds had elapsed since the latest change.");
            Pump(1000);
            Check(!badge.IsVisible && workspace.Store.LoadNoteZoom(rich.Id) == 120, "The badge did not time out or the zoom did not persist.");

            ZoomWheel(editor, 120 * 50);
            window.UpdateLayout();
            Check(ZoomPercent(window) == 400 && editor.ActualWidth < originalWidth / 3 && header.ActualHeight == headerHeight,
                "400% zoom did not reflow the body independently of the header.");
            Check(image.ActualWidth * 4 <= originalWidth + 1 && image.Width == 480, "The zoomed image exceeds the viewport or its stored width changed.");
            Check(ReferenceEquals(editor.Document, document) && run.FontSize == 22, "Zoom replaced or reformatted the document.");

            ZoomKey(editor, Key.D0);
            Pump(30);
            editor.ScrollToVerticalOffset(400);
            Pump(30);
            var anchor = editor.GetPositionFromPoint(new Point(1, 1), true)!;
            ZoomKey(editor, Key.OemPlus);
            Pump(30);
            var top = anchor.GetCharacterRect(anchor.LogicalDirection).Top;
            Check(Math.Abs(top) < 30, $"Reading zoom lost the visible paragraph (anchor y={top}).");
            ZoomWheel(editor, -120 * 50);
            Check(ZoomPercent(window) == 50, "Ctrl+wheel did not clamp to 50%.");
            ZoomKey(editor, Key.OemMinus);
            Check(ZoomPercent(window) == 50, "Ctrl+- passed the lower limit.");
            ZoomKey(editor, Key.D0);
            Check(ZoomPercent(window) == 100, "Ctrl+0 did not reset the zoom.");
            ZoomKey(editor, Key.OemMinus);
            Check(ZoomPercent(window) == 90, "Ctrl+- did not reduce zoom.");
            window.EnterEditMode();
            editor.Selection.Select(run.ContentStart, run.ContentEnd);
            ZoomKey(editor, Key.OemPlus);
            Check(ZoomPercent(window) == 100 && window.IsEditing && editor.Selection.Text == run.Text && run.FontSize == 22,
                "Zoom changed the rich-text selection, font size or editing mode.");
            ZoomKey(editor, Key.OemMinus);
            window.FinishEditing();
            Check(workspace.Store.History.ReadTimeline().Events.Count == eventsBeforeZoom, "Zoom added a history event.");

            workspace.OpenNote(markdown, edit: true);
            var markdownWindow = workspace.Windows[markdown.Id];
            var source = (MarkdownLiveEditor)markdownWindow.FindName("MarkdownEditor");
            source.Select(3, 8);
            var fontSize = source.FontSize;
            Check(ZoomPercent(markdownWindow) == 100 && ZoomPercent(window) == 90, "Notes shared a zoom ratio.");
            CheckImeKeysAreIgnored(source, markdownWindow);
            ZoomKey(source, Key.OemPlus);
            Check(ZoomPercent(markdownWindow) == 110 && source.FontSize == fontSize && source.SelectionStart == 3
                && source.SelectionLength == 8 && markdownWindow.IsEditing, "Zoom changed the Markdown font size, selection or editing mode.");
            markdownWindow.FinishEditing();
            Check(ZoomPercent(markdownWindow) == 110, "Switching to reading mode reset the Markdown zoom.");
            window.Close();
            workspace.OpenNote(rich);
            Check(ZoomPercent(workspace.Windows[rich.Id]) == 90, "Closing before the badge timeout lost the rich-text zoom.");
        }
        finally { workspace.PrepareExit(); }

        var restarted = new StickyWorkspace(path);
        try
        {
            restarted.RestoreWindows();
            Pump(30);
            Check(ZoomPercent(restarted.Windows[rich.Id]) == 90 && ZoomPercent(restarted.Windows[markdown.Id]) == 110,
                "Restart did not restore each note's zoom.");
            Check(!((Border)restarted.Windows[rich.Id].FindName("ZoomBadge")).IsVisible, "Opening a note displayed a zoom badge.");
            Check(File.ReadAllBytes(richPath).SequenceEqual(richBytes) && File.ReadAllBytes(markdownPath).SequenceEqual(markdownBytes),
                "Zoom changed a note's on-disk content.");
        }
        finally { restarted.PrepareExit(); }
    }

    private static void NoteZoomInVault()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "private-note-zoom"));
        try
        {
            var note = new Note { Title = "保险箱比例" };
            workspace.Store.Save(note);
            workspace.Store.SaveNoteZoom(note.Id, 160);
            workspace.Vault.Create(VaultPassword);
            workspace.VaultMigrations.MoveIn(workspace.Vault, note);
            Check(workspace.Store.LoadNoteZoom(note.Id) is null, "Moving to the vault left a zoom preference on disk.");

            workspace.RefreshPrivateNotes();
            var privateNote = workspace.PrivateNotes.Single();
            workspace.OpenNote(privateNote);
            var window = workspace.Windows[note.Id];
            var editor = (RichTextBox)window.FindName("Editor");
            ZoomKey(editor, Key.OemPlus);
            window.Close();
            workspace.OpenNote(privateNote);
            Check(ZoomPercent(workspace.Windows[note.Id]) == 110 && workspace.Store.LoadNoteZoom(note.Id) is null,
                "Closing a private note lost its session zoom or wrote it to disk.");

            Check(workspace.LockPrivateNotes(), "Could not lock the vault.");
            workspace.Vault.Unlock(VaultPassword);
            workspace.RefreshPrivateNotes();
            workspace.OpenNote(workspace.PrivateNotes.Single());
            Check(ZoomPercent(workspace.Windows[note.Id]) == 100, "Locking the vault retained private zoom state.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static int ZoomPercent(NoteWindow window) => (int)Math.Round(((ScaleTransform)window.FindName("ContentScale")).ScaleX * 100);

    private static void CheckImeKeysAreIgnored(UIElement editor, NoteWindow window) => WithZoomControlKey(() =>
    {
        var initialZoom = ZoomPercent(window);
        var source = PresentationSource.FromVisual(editor)!;
        foreach (var key in new[] { Key.LeftCtrl, Key.LeftShift, Key.Space, Key.OemPlus })
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (key == Key.OemPlus)
                typeof(KeyEventArgs).GetMethod("MarkImeProcessed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(args, null);
            editor.RaiseEvent(args);
            Check(!args.Handled && ZoomPercent(window) == initialZoom, $"Zoom intercepted an input method key: {key}.");
        }
    });

    private static void ZoomKey(UIElement target, Key key) => WithZoomControlKey(() =>
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        Check(args.Handled, $"Ctrl+{key} was not handled.");
    });

    private static void ZoomWheel(UIElement target, int delta) => WithZoomControlKey(() =>
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        target.RaiseEvent(args);
        Check(args.Handled, "Ctrl+wheel was not handled.");
    });

    private static void WithZoomControlKey(Action action)
    {
        var original = new byte[256];
        Check(GetKeyboardState(original), "Could not read the test thread's keyboard state.");
        var pressed = (byte[])original.Clone();
        pressed[0x11] = pressed[0xA2] = 0x80;
        Check(SetKeyboardState(pressed), "Could not set the test thread's Ctrl key state.");
        try { action(); }
        finally { SetKeyboardState(original); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);
}
