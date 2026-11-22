using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;
using Sticky.App;
using Sticky.App.Editor;
using Sticky.App.ViewModels;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static partial class Program
{
    private static string MarkdownTestImage(NoteStore store) => store.AddImage(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aZioAAAAASUVORK5CYII="));

    private static void MarkdownLifecycle()
    {
        var store = Store("markdown-lifecycle");
        File.WriteAllText(Path.Combine(store.Root, "device", "editor.json"), "{\"schemaVersion\":1,\"defaultFontSize\":18}");
        var legacy = Example();
        store.Save(legacy);
        var legacyPath = Path.Combine(store.NotesDirectory, legacy.Id + ".json");
        var workspace = new StickyWorkspace(store.Root);
        var legacyBytes = File.ReadAllBytes(legacyPath);
        try
        {
            workspace.CreateNote();
            var note = workspace.Notes.First();
            var window = workspace.Windows[note.Id];
            var markdown = (MarkdownLiveEditor)window.FindName("MarkdownEditor");
            var image = MarkdownTestImage(store);
            var source = "# Markdown 标题\n\n**关键词**、~~旧计划~~\n\n" + MarkdownBody.ImageSyntax(image, 160);
            Check(note.IsMarkdown && markdown.IsVisible && markdown.FontSize == 18, "Legacy preferences did not default to Markdown or lost the stored font size.");
            markdown.Text = source;
            window.FinishEditing();
            var loaded = store.LoadNotes().Single(n => n.Id == note.Id);
            Check(loaded.IsMarkdown && loaded.MarkdownSource == source && loaded.Blocks.Count == 0, "The saved note did not preserve Markdown as its source.");
            Check(MarkdownLine(markdown, 3).Elements.Any(run => run.TextRunProperties.Typeface.Weight == FontWeights.Bold),
                "Reading did not render Markdown emphasis.");
            Check(markdown.IsVisible && markdown.IsReadOnly && new NoteCard(loaded, true).Title == "Markdown 标题",
                "Reading swapped away the Markdown editor or the derived title showed Markdown syntax.");
            using var search = new SearchViewModel(workspace);
            search.Query = "关键词";
            Check(search.Results.Single().Note.Id == note.Id, "Search did not find rendered Markdown text.");
            var zip = Path.Combine(store.Root, "markdown.zip");
            BackupService.Export(store, zip);
            var target = Store("markdown-import");
            var imported = BackupService.Import(target, zip).Single(n => n.Id == note.Id);
            Check(imported.MarkdownSource == source && imported.GetImageIds().Single() == image
                && File.Exists(target.GetAssetPath(image)), "Backup lost Markdown source or an attachment.");

            workspace.ShowSettings(SettingsSection.Typography);
            var settings = workspace.Settings!;
            var format = (ComboBox)settings.FindName("NewNoteFormatBox");
            Check((string)format.SelectedValue == "markdown", "Settings did not display Markdown as the default.");
            format.SelectedValue = "richText";
            ClickSaveTypography(settings);
            workspace.CreateNote();
            Check(!workspace.Notes[0].IsMarkdown && workspace.Notes.Single(n => n.Id == note.Id).IsMarkdown
                && store.LoadEditorPreferences().NewNoteFormat == "richText", "The saved default changed the wrong notes or did not persist.");
            Check(legacyBytes.AsSpan().SequenceEqual(File.ReadAllBytes(legacyPath)), "Creating notes or saving preferences rewrote existing rich text.");

            window.Close();
            workspace.OpenNote(loaded, edit: true);
            Check(((MarkdownLiveEditor)workspace.Windows[note.Id].FindName("MarkdownEditor")).Text == source, "Reopening changed the source.");
        }
        finally { workspace.PrepareExit(); }
    }

    private static void MarkdownLiveEditing()
    {
        var workspace = new StickyWorkspace(Store("markdown-live-editing").Root);
        try
        {
            workspace.CreateNote();
            var note = workspace.Notes.Single();
            var window = workspace.Windows[note.Id];
            var markdown = (MarkdownLiveEditor)window.FindName("MarkdownEditor");
            var document = markdown.Document;
            markdown.TextArea.PerformTextInput("# ");
            var title = "标题" + new string('字', 40);
            markdown.TextArea.PerformTextInput(title);
            Check(window.IsEditing && !markdown.IsReadOnly && markdown.Text == "# " + title && IsRawLine(1)
                && MarkdownLine(markdown, 1).TextLines.Count > 1
                && markdown.TextArea.Caret.CaretBrush is SolidColorBrush { Color.R: > 200 },
                "Typing or wrapping the heading changed its syntax or ordinary text size.");
            window.FinishEditing();
            Check(MarkdownLine(markdown, 1).Elements.OfType<FormattedTextElement>().Any(),
                "Finishing a one-line edit did not render the only line.");
            window.EnterEditMode();
            Check(IsRawLine(1), "Reentering the only line did not reveal its source.");
            markdown.TextArea.PerformTextInput("\n");
            var heading = MarkdownLine(markdown, 1).Elements;
            Check(document.LineCount == 2 && markdown.CaretOffset == document.TextLength
                && heading.OfType<FormattedTextElement>().Any()
                && heading.Any(part => part.TextRunProperties.FontRenderingEmSize > markdown.FontSize
                    && part.TextRunProperties.ForegroundBrush is SolidColorBrush { Color.R: > 200 }),
                "Leaving a heading with Enter did not render it in the same editor.");

            var imageId = MarkdownTestImage(workspace.Store);
            var content = "# 标题\r\n\r\n**粗体** *斜体* ~~划掉~~ `代码`\r\n\r\n- 列表\r\n\r\n" + MarkdownBody.ImageSyntax(imageId, 160)
                + "\r\n\r\n1. 第一项\r\n1. 第二项\r\n\r\n```text\r\n代码块\r\n```";
            document.Replace(0, document.TextLength, content);
            markdown.CaretOffset = content.Length;
            var emphasis = MarkdownLine(markdown, 3).Elements;
            Check(emphasis.OfType<FormattedTextElement>().Count() == 8
                && emphasis.Any(part => part.TextRunProperties.Typeface.Weight == FontWeights.Bold)
                && emphasis.Any(part => part.TextRunProperties.Typeface.Style == FontStyles.Italic)
                && emphasis.Any(part => part.TextRunProperties.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true)
                && emphasis.Any(part => part.TextRunProperties.Typeface.FontFamily.Source.Contains("Consolas")),
                "The editable view did not format emphasis or inline code and hide their delimiters.");
            Check(MarkdownLine(markdown, 5).Elements.OfType<FormattedTextElement>().Any()
                && MarkdownLine(markdown, 7).Elements.OfType<InlineObjectElement>().Any(),
                "The editable view did not render a list marker and an attachment inline.");
            Check(MarkdownBody.GetVisuals(content).Replacements.Any(part => part.Text == "2. ")
                && MarkdownLine(markdown, 12).Elements.OfType<FormattedTextElement>().Any()
                && MarkdownLine(markdown, 13).Elements.Any(part => part.TextRunProperties.Typeface.FontFamily.Source.Contains("Consolas")),
                "The live view did not number ordered items or render the fenced code block.");

            var boldOffset = content.IndexOf("粗体", StringComparison.Ordinal);
            markdown.CaretOffset = boldOffset + 1;
            Check(IsRawLine(3) && markdown.Text == content
                && MarkdownLine(markdown, 1).Elements.OfType<FormattedTextElement>().Any(),
                "Moving the caret did not reveal the whole current line without changing its source or other lines.");
            markdown.Select(boldOffset, 2);
            Check(IsRawLine(3), "Selecting text within the line triggered preview formatting.");
            markdown.TextArea.PerformTextInput("原位编辑");
            Check(markdown.Text.Contains("**原位编辑**") && IsRawLine(3), "Editing this line changed the wrong range or triggered preview formatting.");
            markdown.Undo();
            Check(markdown.Text == content, "Undo did not restore the exact Markdown after live formatting.");
            markdown.CaretOffset = document.GetLineByNumber(4).Offset;
            Check(MarkdownLine(markdown, 3).Elements.OfType<FormattedTextElement>().Count() == 8,
                "Moving to another line did not render the previous line.");
            markdown.Select(boldOffset, 0);
            window.FinishEditing();
            Check(markdown.IsVisible && markdown.IsReadOnly && ReferenceEquals(document, markdown.Document)
                && MarkdownLine(markdown, 3).Elements.OfType<FormattedTextElement>().Count() == 8,
                "Finishing editing replaced the viewport or left active delimiters revealed.");
            Check(workspace.Store.LoadNotes().Single().MarkdownSource == content, "The live editor did not save the original Markdown source.");

            bool IsRawLine(int line) => MarkdownLine(markdown, line).Elements.All(part => part is VisualLineText
                && part.TextRunProperties.FontRenderingEmSize == markdown.FontSize
                && part.TextRunProperties.Typeface.Weight == FontWeights.Normal
                && part.TextRunProperties.Typeface.Style == FontStyles.Normal);
        }
        finally { workspace.PrepareExit(); }
    }

    private static VisualLine MarkdownLine(MarkdownLiveEditor editor, int line)
    {
        editor.UpdateLayout();
        return editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(line));
    }

    private static void MarkdownConversionHistory()
    {
        var store = Store("markdown-conversion");
        var group = new NoteDirectory { Name = "转换组" };
        store.SaveDirectories([group]);
        var tag = new NoteTag(Guid.NewGuid(), "转换测试");
        store.SaveTags([tag]);
        var id = MarkdownTestImage(store);
        var original = new Note { Title = "固定标题", DirectoryId = group.Id, Color = "rose", Blocks =
        [
            new NoteBlock { Alignment = "Center", Inlines =
            [
                new NoteInline { Text = "字面 **，" },
                new NoteInline { Text = "加粗", Bold = true, Foreground = "#FF998877", Underline = true },
                new NoteInline { Text = "、" },
                new NoteInline { Text = "删除线", Strike = true },
                new NoteInline { Kind = "image", AssetId = id, Width = 160 }
            ] },
            new NoteBlock { Kind = "list", Marker = "Decimal", Children =
            [
                new NoteBlock { Kind = "listItem", Children =
                [
                    new NoteBlock { Inlines = [new NoteInline { Text = "条目\n第二行" }] },
                    new NoteBlock { Kind = "list", Children =
                    [
                        new NoteBlock { Kind = "listItem", Children = [new NoteBlock { Inlines = [new NoteInline { Text = "子项" }] }] }
                    ] }
                ] }
            ] }
        ] };
        original.SetTags([tag.Id]);
        store.Save(original);
        var workspace = new StickyWorkspace(store.Root);
        try
        {
            var note = workspace.Notes.Single();
            workspace.OpenNote(note);
            var window = workspace.Windows[note.Id];
            var before = store.History.ReadTimeline().Events.Last().Sequence;
            void ConvertFromMenu(string target, bool confirm, string expectedWarning)
            {
                var more = (Button)window.FindName("MoreButton");
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var menu = more.ContextMenu;
                var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == $"转换为{(target == "Markdown" ? " " : "")}{target}…");
                var observed = false;
                var warning = false;
                var previews = 0;
                window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                {
                    var dialog = window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsVisible);
                    if (dialog?.Content is not Grid grid)
                    {
                        // Close a failure dialog too, so a conversion regression cannot hang the suite.
                        dialog?.Close();
                        return;
                    }
                    observed = dialog.Title == (target == "Markdown" ? "转换为 Markdown" : "转换为富文本");
                    warning = grid.Children.OfType<TextBlock>().Any(label => label.Text.Contains(expectedWarning));
                    previews = grid.Children.OfType<DockPanel>().SelectMany(panel => panel.Children.OfType<RichTextBox>()).Count();
                    if (confirm) grid.Children.OfType<StackPanel>().Single().Children.OfType<Button>()
                        .Single(b => b.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                });
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
                Check(observed && warning && previews == 2, "The conversion menu did not show both previews and the expected change.");
            }

            ConvertFromMenu("Markdown", false, "下划线");
            Check(!note.IsMarkdown && store.History.ReadTimeline().Events.Last().Sequence == before, "Cancelling a preview wrote a conversion.");
            ConvertFromMenu("Markdown", true, "下划线");
            Check(note.IsMarkdown && note.GetImageIds().Single() == id && note.GetBodyBlocks()[0].Inlines.Any(i => i.Bold && i.Text == "加粗")
                && note.GetBodyBlocks()[0].Inlines.Any(i => i.Strike && i.Text == "删除线") && note.PlainText == original.PlainText,
                "Rich text conversion lost a supported style, list text, or attachment.");
            Check(note.Id == original.Id && note.Title == original.Title && note.DirectoryId == original.DirectoryId && note.Color == original.Color
                && note.Tags.Single() == tag.Id && note.SchemaVersion == Note.TaggedMarkdownVersion,
                "Conversion changed the note's identity or properties.");

            window.EnterEditMode();
            var markdown = (MarkdownLiveEditor)window.FindName("MarkdownEditor");
            markdown.AppendText("\n\n## 代码\n\n```text\nalpha();\n```\n\n[文档](https://example.com)");
            Check(window.SaveNow(false), "Editing converted Markdown failed.");
            var markdownSource = note.MarkdownSource;
            var markdownVersion = store.History.ReadTimeline().Events.Last().Sequence;
            ConvertFromMenu("富文本", true, "代码保留为文字");
            Check(!note.IsMarkdown && note.GetImageIds().Single() == id && note.PlainText.Contains("alpha();")
                && note.PlainText.Contains("https://example.com"), "Markdown conversion lost code, link text or an attachment.");

            var timeline = store.History.ReadTimeline();
            Check(timeline.Events.Count(e => e.Kind == "noteFormatConverted") == 2, "Conversions were not recorded as distinct history events.");
            workspace.ShowHistory(note.Id);
            var history = workspace.History!;
            ((TabControl)history.FindName("PreviewTabs")).SelectedIndex = 1;
            history.UpdateLayout();
            var beforeLabel = ((TextBlock)history.FindName("BeforeLabel")).Text;
            Check(((Button)history.FindName("RestoreBeforeConversionButton")).Visibility == Visibility.Visible && beforeLabel.Contains("Markdown"),
                $"The history comparison did not expose the original format or its restore action ({beforeLabel}, sequence {history.PreviewSequence}).");
            history.Close();
            var markdownFrame = timeline.Frame(markdownVersion);
            workspace.RestoreHistoryNote(markdownFrame.Notes[note.Id], markdownFrame.Directories, markdownVersion, asCopy: false);
            Check(note.IsMarkdown && note.MarkdownSource == markdownSource, "Restoring history did not recover exact Markdown source.");
            var originalFrame = timeline.Frame(before);
            workspace.RestoreHistoryNote(originalFrame.Notes[note.Id], originalFrame.Directories, before, asCopy: false);
            Check(!note.IsMarkdown && SyncJson.Equal(original.Blocks, note.Blocks) && store.LoadNotes().Single().SchemaVersion == Note.TaggedRichTextVersion && note.Tags.Single() == tag.Id,
                "Restoring the original history did not recover its original rich formatting and attachments.");
        }
        finally { workspace.PrepareExit(); }
    }
}
