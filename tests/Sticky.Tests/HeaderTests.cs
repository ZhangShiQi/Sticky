using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;
using Sticky.Core;
using Sticky.Sync.Git;

internal static partial class Program
{
    private static void HeaderTitlesAndVisibility()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "header-titles"));
        try
        {
            workspace.CreateNote(); var note = workspace.Notes[0]; var window = workspace.Windows[note.Id]; Pump(25);
            var editor = (RichTextBox)window.FindName("Editor"); var titleBox = (TextBox)window.FindName("TitleBox");
            var title = (TextBlock)window.FindName("HeaderTitle"); var header = (Border)window.FindName("HeaderBar");
            void Leave()
            { window.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent }); window.UpdateLayout(); }
            window.FinishEditing(); Leave();
            Check(window.IsHeaderExpanded && header.ActualHeight == 32 && title.IsVisible && title.Text == "未命名便签", "A new idle note hid the title bar or displayed no placeholder title.");
            Check(!((Border)window.FindName("FormattingBar")).IsVisible && editor.IsReadOnly, "A visible title bar entered editing or showed formatting tools.");
            window.EnterEditMode(); editor.Document = new FlowDocument(new Paragraph(new Run("自动标题\n第二行")));
            Check(title.Text == "自动标题" && window.Title == "自动标题 · Sticky", "The first line did not update the visible and taskbar titles before autosave.");
            titleBox.Text = "  手动标题  "; Check(title.Text == "手动标题", "The visible header did not prefer the trimmed manual title.");
            editor.AppendText("正文的新内容"); Check(title.Text == "手动标题", "Editing the body overwrote a manual title.");
            titleBox.Text = "";
            var paragraph = new Paragraph(); paragraph.Inlines.Add(new Bold(new Run("富文本"))); paragraph.Inlines.Add(new Run("首行")); paragraph.Inlines.Add(new LineBreak()); paragraph.Inlines.Add(new Run("下一行"));
            var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal }; list.ListItems.Add(new ListItem(paragraph));
            var document = new FlowDocument(); document.Blocks.Add(new Paragraph(new InlineUIContainer(new Image { Width = 16, Height = 16, Tag = new string('a', 64) + ".png" }))); document.Blocks.Add(new Paragraph(new Run("  "))); document.Blocks.Add(list);
            editor.Document = document;
            Check(title.Text == "富文本首行" && window.SaveNow(false) && title.Text == note.DisplayTitle && note.Title == "", "Derived header included images, blank lines, generated list numbering, or persisted a manual title.");
            var before = File.ReadAllBytes(Path.Combine(workspace.Store.NotesDirectory, note.Id + ".json")); var modified = note.ModifiedAt;
            workspace.ShowSettings(SettingsSection.Typography); var settings = workspace.Settings!;
            ((CheckBox)settings.FindName("AutoHideHeaderCheckBox")).IsChecked = true; ClickSaveTypography(settings);
            window.FinishEditing(); Leave(); Check(!window.IsHeaderExpanded && header.ActualHeight == 8, "Enabling auto-hide did not collapse an existing idle window.");
            ((CheckBox)settings.FindName("AutoHideHeaderCheckBox")).IsChecked = false; ClickSaveTypography(settings); Leave();
            Check(window.IsHeaderExpanded && header.ActualHeight == 32 && workspace.Store.LoadEditorPreferences().AutoHideHeader == false, "Disabling auto-hide did not immediately reveal an existing title bar.");
            Check(note.ModifiedAt == modified && File.ReadAllBytes(Path.Combine(workspace.Store.NotesDirectory, note.Id + ".json")).AsSpan().SequenceEqual(before), "Changing header visibility rewrote note content or modification time.");
            var captured = workspace.CaptureForSync(); var incoming = captured.Clone();
            incoming.Notes[note.Id].Title = "同步传入的标题"; workspace.ApplySyncedSnapshot(captured, incoming);
            Check(title.Text == "同步传入的标题", "Incoming sync did not refresh the visible title.");
            note.Title = ""; note.Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "同步后的正文首行" }] }]; workspace.Store.Save(note); window.ReloadAfterSync(true);
            Check(title.Text == "同步后的正文首行", "A synced body did not refresh the derived header.");
            titleBox.Text = "第一人称的做饭游戏 · 标题很长时省略，悬停可以查看完整标题"; Check(window.SaveNow(false), "Title could not be saved.");
            window.Width = 280; window.FinishEditing(); Leave();
            var close = ((DockPanel)header.Child).Children.OfType<Button>().Single(b => b.Content?.ToString() == "×");
            Check(title.ActualWidth > 0 && title.TextTrimming == TextTrimming.CharacterEllipsis && title.ToolTip?.ToString() == title.Text
                && close.TranslatePoint(new Point(close.ActualWidth, 0), window).X <= window.ActualWidth, "A long title hid buttons, failed to ellipsize or lost the full-title tooltip.");
            window.Width = 390; window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            using (var output = File.Create(Path.Combine(_root, "note-header-title.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output); }
            var expected = title.Text; window.Close(); workspace.OpenNote(note); var reopened = workspace.Windows[note.Id]; reopened.FinishEditing(); Pump(25);
            Check(((TextBlock)reopened.FindName("HeaderTitle")).Text == expected && reopened.IsHeaderExpanded, "Reopening lost the title or default visibility.");
        }
        finally { workspace.PrepareExit(); }
        var restarted = new StickyWorkspace(workspace.Store.Root);
        try { restarted.RestoreWindows(); Pump(20); Check(restarted.Windows.Count == 1 && restarted.Windows.Values.All(w => w.IsHeaderExpanded) && !restarted.EditorPreferences.AutoHideHeader, "Restart changed the persisted visible-title preference."); }
        finally { restarted.PrepareExit(); }
    }
}
