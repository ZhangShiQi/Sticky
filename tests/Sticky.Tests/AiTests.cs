using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Sticky.App;
using Sticky.App.AI;
using Sticky.App.Editor;
using Sticky.Storage;
using Sticky.Core;

internal static partial class Program
{
    private static AiConfiguration AiConfig(string url = "https://ai.example.test/v1", string key = "synthetic-test-key") => new()
    { Enabled = true, BaseUrl = url, Model = "test-model", ApiKey = key };
    private sealed class AiHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(request, token); }
    }
    private static HttpResponseMessage AiResponse(string text = "润色后的文字", string finish = "stop") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = finish } } }), Encoding.UTF8, "application/json") };
    private static void ExpectAiFailure(Action action, string? contains = null)
    {
        try { action(); throw new InvalidOperationException("Expected an AI configuration or request failure."); }
        catch (AiException ex) { Check(contains is null || ex.Message.Contains(contains), "AI error did not describe the failure safely."); }
    }
    private static void AiConfigurationAndStorage()
    {
        Check(AiConfig().CompletionUri().AbsoluteUri == "https://ai.example.test/v1/chat/completions", "AI base path was lost.");
        Check(AiConfig("https://ai.example.test/v1/chat/completions/").CompletionUri() == AiConfig().CompletionUri(), "AI endpoint suffix was duplicated.");
        foreach (var url in new[] { "http://ai.example.test/v1", "https://user:password@ai.example.test/v1", "https://ai.example.test/v1?key=private", "file:///tmp/key", "https://ai.example.test/v1#key" })
            ExpectAiFailure(() => AiConfig(url).Validate());
        AiConfig("http://127.0.0.1:11434/v1", "").Validate();
        ExpectAiFailure(() => AiConfig(key: "").Validate());
        ExpectAiFailure(() => AiConfig(key: "bad\nkey").Validate());
        var root = Path.Combine(_root, "ai-config"); var notes = new NoteStore(root); var settings = new AiSettingsStore(root);
        Check(!settings.Load().Enabled && !File.Exists(settings.FilePath), "AI was enabled or persisted before configuration.");
        settings.Save(AiConfig());
        var bytes = File.ReadAllText(settings.FilePath);
        Check(!bytes.Contains("synthetic-test-key") && settings.Load().ApiKey == "synthetic-test-key" && settings.Load().Enabled, "AI key was not protected with Windows user encryption.");
        Check(!File.Exists(settings.FilePath + ".bak") && !File.Exists(WorkspaceFiles.BackupPath(root, settings.FilePath)), "AI settings created a stale key backup.");
        File.WriteAllText(settings.FilePath, bytes.Replace("ai.example.test", "another.example.test"));
        Check(!settings.Load().CanRun && settings.Warning is not null && File.ReadAllText(settings.FilePath).Contains("another.example.test"), "A protected key was reused at another endpoint or a damaged config was overwritten.");
        settings.Save(AiConfig()); var note = Example(); notes.Save(note);
        var ordinary = Path.Combine(_root, "ai-ordinary.zip"); var history = Path.Combine(_root, "ai-history.zip");
        BackupService.Export(notes, ordinary); notes.History.Export(history);
        foreach (var path in new[] { ordinary, history })
        { using var zip = ZipFile.OpenRead(path); Check(zip.Entries.All(e => !e.FullName.Contains("ai.json")), "AI configuration or credentials leaked into a note/history backup."); }
        var captured = File.ReadAllText(settings.FilePath); File.WriteAllText(settings.FilePath, "{broken");
        Check(!settings.Load().Enabled && settings.Warning is not null && File.ReadAllText(settings.FilePath) == "{broken", "Broken AI settings changed notes or overwrote the source.");
        File.WriteAllText(settings.FilePath, captured);
        settings.Save(new AiConfiguration { BaseUrl = AiConfig().BaseUrl, Model = "test-model" });
        Check(settings.Load().ApiKey.Length == 0 && !settings.Load().Enabled, "Removing the key did not persist disabled AI.");
    }
    private static void AiRequestContract() => Task.Run(async () =>
    {
        var handler = new AiHttp(async (request, token) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri == AiConfig().CompletionUri(), "AI used an unexpected endpoint or method.");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "synthetic-test-key", "AI authentication was not scoped to the request.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = body.RootElement.GetProperty("messages");
            Check(body.RootElement.GetProperty("model").GetString() == "test-model" && !body.RootElement.GetProperty("stream").GetBoolean()
                && messages.GetArrayLength() == 2 && messages[1].GetProperty("content").GetString() == "仅选中的这句话。", "AI transmitted content outside the selected text or changed the model.");
            return AiResponse("仅选中的这句话。");
        });
        using var client = new AiClient(handler);
        var result = await client.PolishAsync(AiConfig(), "仅选中的这句话。");
        Check(result == "仅选中的这句话。" && handler.Calls == 1, "AI result parsing or request count was incorrect.");
        using var parts = new AiClient(new AiHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"第一段\"},{\"type\":\"text\",\"text\":\"第二段\"}]}}]}") })));
        Check(await parts.PolishAsync(AiConfig(), "原文") == "第一段第二段", "Text-part responses were not supported.");
    }).GetAwaiter().GetResult();
    private static void AiRequestFailures() => Task.Run(async () =>
    {
        foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            var handler = new AiHttp((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("synthetic-test-key: echo-private-input"), Headers = { Location = new Uri("https://another.example.test/") } }));
            using var client = new AiClient(handler);
            try { await client.PolishAsync(AiConfig(), "原文"); throw new InvalidOperationException("AI accepted an error response."); }
            catch (AiException ex) { Check(!ex.Message.Contains("synthetic-test-key") && !ex.Message.Contains("echo-private-input") && handler.Calls == 1, "AI leaked server errors, retried a billed request or followed a redirect."); }
        }
        foreach (var response in new[] { AiResponse("半句话", "length"), AiResponse(""), new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{bad-json") },
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) } })
        { using var client = new AiClient(new AiHttp((_, _) => Task.FromResult(response))); ExpectAiFailure(() => client.PolishAsync(AiConfig(), "原文").GetAwaiter().GetResult()); }
        using var offline = new AiClient(new AiHttp((_, _) => throw new HttpRequestException("private-details")));
        ExpectAiFailure(() => offline.PolishAsync(AiConfig(), "原文").GetAwaiter().GetResult(), "无法连接");
        using var waiting = new AiClient(new AiHttp(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return AiResponse(); }));
        using var cancellation = new CancellationTokenSource(30);
        var cancelled = false; try { await waiting.PolishAsync(AiConfig(), "原文", cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "AI cancellation did not reach the request.");
        ExpectAiFailure(() => waiting.PolishAsync(new AiConfiguration { Enabled = true, BaseUrl = AiConfig().BaseUrl, Model = "test-model", ApiKey = "test-key", TimeoutSeconds = 5 }, "原文").GetAwaiter().GetResult(), "超时");
    }).GetAwaiter().GetResult();
    private static void AiSelectionSafety()
    {
        var prefix = new Run("前文 ") { FontStyle = FontStyles.Italic }; var target = new Run("  这句话很不通顺  ") { FontWeight = FontWeights.Bold }; var suffix = new Run(" 后文");
        var image = new InlineUIContainer(new Image { Width = 16, Height = 16, Tag = new string('a', 64) + ".png" });
        var paragraph = new Paragraph(); paragraph.Inlines.Add(prefix); paragraph.Inlines.Add(target); paragraph.Inlines.Add(suffix); paragraph.Inlines.Add(image);
        var editor = new RichTextBox { Document = new FlowDocument(paragraph) };
        var host = new Window { Content = editor, Width = 400, Height = 300 }; host.Show(); editor.Focus(); Pump(30);
        try
        {
        editor.Selection.Select(target.ContentStart, target.ContentEnd);
        using (var selection = AiTextSelection.Capture(editor))
        {
            editor.Selection.Select(suffix.ContentStart, suffix.ContentEnd);
            Check(selection.Apply("  这句话已经通顺了。\n"), "Moving the caret prevented replacement of the original selection.");
            Check(new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.StartsWith("前文   这句话已经通顺了。   后文")
                && paragraph.Inlines.OfType<InlineUIContainer>().Single() == image && prefix.FontStyle == FontStyles.Italic,
                "AI replacement changed surrounding text, edge spacing, styles or images.");
            Check(DocumentCodec.Encode(editor.Document)[0].Inlines.Any(i => i.Text.Contains("已经通顺") && i.Bold), "AI replacement lost the starting text style.");
            Check(DocumentCodec.Encode(editor.Document)[0].Inlines.Where(i => i.Text.Contains("已经通顺")).All(i => i.UsesDefaultFontSize == true), "AI converted default text into a fixed font size.");
        }
        editor.Undo(); Check(new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Contains("这句话很不通顺"), "AI replacement was not one undoable edit.");
        editor.Selection.Select(editor.Document.ContentStart, editor.Document.ContentEnd);
        ExpectAiFailure(() => AiTextSelection.Capture(editor), "图片");
        var plain = new RichTextBox { Document = new FlowDocument(new Paragraph(new Run("原始文字"))) };
        plain.SelectAll(); using (var selection = AiTextSelection.Capture(plain))
        { plain.AppendText("新的改动"); Check(!selection.Apply("不能覆盖新内容"), "AI overwrote edits made while waiting."); }
        plain.SelectAll(); using (var selection = AiTextSelection.Capture(plain))
        { plain.Document = new FlowDocument(new Paragraph(new Run("同步传入"))); Check(!selection.Apply("不能覆盖同步"), "AI overwrote a document replaced by sync."); }
        }
        finally { host.Close(); }
    }
    private static void AiParagraphAndListSelection()
    {
        var first = new Paragraph(new Run("第一段原文。")); var second = new Paragraph(new Run("第二段保持。"));
        var document = new FlowDocument(); document.Blocks.Add(first); document.Blocks.Add(second);
        var editor = new RichTextBox { Document = document }; var host = new Window { Content = editor, Width = 400, Height = 300 }; host.Show(); editor.Focus(); Pump(20);
        try
        {
            editor.Selection.Select(first.ContentStart, second.ContentStart);
            using (var selection = AiTextSelection.Capture(editor)) Check(selection.Apply("第一段已修正。"), "Paragraph-ending selection could not be replaced.");
            Check(document.Blocks.Count == 2 && new TextRange(document.ContentStart, document.ContentEnd).Text == "第一段已修正。\r\n第二段保持。\r\n", "AI replacement duplicated a paragraph break or merged following text.");
            editor.Undo();
            var a = new Run("第一个条目"); var b = new Run("第二个条目");
            var list = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal };
            list.ListItems.Add(new ListItem(new Paragraph(a))); list.ListItems.Add(new ListItem(new Paragraph(b)));
            editor.Document = new FlowDocument(list); editor.Selection.Select(a.ContentStart, b.ContentEnd);
            using (var selection = AiTextSelection.Capture(editor))
            {
                Check(selection.OriginalText == "第一个条目\r\n第二个条目", "AI sent generated list numbering as literal text.");
                Check(selection.Apply("第一条已修正\n第二条已修正"), "List text could not be replaced.");
            }
            var encoded = DocumentCodec.Encode(editor.Document);
            Check(encoded.Count == 1 && encoded[0].Kind == "list" && encoded[0].Marker == "Decimal" && encoded[0].Children.Count == 2, "AI lost numbering or changed the number of list items.");
            var run = new Run("固定字号") { FontSize = 22, FontStyle = FontStyles.Italic };
            editor.Document = new FlowDocument(new Paragraph(run)); editor.Selection.Select(run.ContentStart, run.ContentEnd);
            using (var selection = AiTextSelection.Capture(editor)) Check(selection.Apply("保留固定字号"), "Explicit-size selection could not be replaced.");
            Check(DocumentCodec.Encode(editor.Document)[0].Inlines.Any(i => i.Text.Contains("保留固定") && i.FontSize == 22 && i.Italic && i.UsesDefaultFontSize == false), "AI lost an explicit font size or italic style.");
        }
        finally { host.Close(); }
    }
    private static void AiSettingsUi()
    {
        string? testPayload = null;
        var workspace = new StickyWorkspace(Path.Combine(_root, "ai-settings-ui"), aiClient: new AiClient(new AiHttp(async (request, token) =>
        { testPayload = await request.Content!.ReadAsStringAsync(token); return AiResponse(); })));
        try
        {
            workspace.ShowSettings(SettingsSection.AI); Pump(30);
            var view = (AiSettingsView)((ContentControl)workspace.Settings!.FindName("AiHost")).Content;
            ((TextBox)view.FindName("BaseUrlBox")).Text = "https://ai.example.test/v1";
            ((TextBox)view.FindName("ModelBox")).Text = "test-model";
            ((PasswordBox)view.FindName("KeyBox")).Password = "synthetic-test-key";
            ((CheckBox)view.FindName("EnabledBox")).IsChecked = true;
            ((Button)view.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(workspace.AiSettings.Load().Enabled && ((PasswordBox)view.FindName("KeyBox")).Password.Length == 0, "AI settings were not saved or left the key visible in the form.");
            var beforeTest = File.ReadAllText(workspace.AiSettings.FilePath);
            ((Button)view.FindName("TestButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(50);
            using var request = JsonDocument.Parse(testPayload ?? "{}");
            Check(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == "这是一条用于测试 AI 连接的示例文本。"
                && File.ReadAllText(workspace.AiSettings.FilePath) == beforeTest, "Testing the connection sent note content or changed saved settings.");
            workspace.Settings.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)workspace.Settings.ActualWidth, (int)workspace.Settings.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(workspace.Settings);
            using (var output = File.Create(Path.Combine(_root, "ai-settings.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output); }
            ((TextBox)view.FindName("ModelBox")).Text = "second-model";
            ((Button)view.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(workspace.AiSettings.Load().ApiKey == "synthetic-test-key" && workspace.AiSettings.Load().Model == "second-model", "Saving a model change removed the saved key.");
            ((TextBox)view.FindName("BaseUrlBox")).Text = "https://another.example.test/v1";
            ((Button)view.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(workspace.AiSettings.Load().BaseUrl == "https://ai.example.test/v1", "Changing endpoint silently forwarded the prior key.");
            ((Button)view.FindName("ForgetButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!workspace.AiSettings.Load().CanRun && workspace.AiSettings.Load().ApiKey.Length == 0, "Removing the saved key did not disable AI on this device.");
        }
        finally { workspace.PrepareExit(); }
    }
    private static void AiNotePreviewAndHistory()
    {
        string? submitted = null;
        var handler = new AiHttp(async (request, token) => { submitted = await request.Content!.ReadAsStringAsync(token); return AiResponse("这段话已经更加通顺了。"); });
        var workspace = new StickyWorkspace(Path.Combine(_root, "ai-note-ui"), aiClient: new AiClient(handler));
        try
        {
            workspace.AiSettings.Save(AiConfig()); workspace.CreateNote(); var note = workspace.Notes.Single(); var window = workspace.Windows[note.Id];
            var editor = (RichTextBox)window.FindName("Editor");
            var paragraph = new Paragraph(); var target = new Run("这段话嗯表达不太好。");
            paragraph.Inlines.Add(new Run("不要发送前文。")); paragraph.Inlines.Add(target); paragraph.Inlines.Add(new Run("不要发送后文。"));
            editor.Document = new FlowDocument(paragraph); Check(window.SaveNow(false), "AI source note could not be saved.");
            editor.Selection.Select(target.ContentStart, target.ContentEnd);
            var original = note.PlainText;
            Exception? failure = null; var start = DateTime.UtcNow;
            var driver = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            driver.Tick += (_, _) =>
            {
                var dialog = System.Windows.Application.Current.Windows.OfType<AiPolishWindow>().FirstOrDefault();
                if (dialog is null) return;
                try
                {
                    if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("AI preview did not finish.");
                    if (!((Button)dialog.FindName("ApplyButton")).IsEnabled) return;
                    Check(note.PlainText == original && !new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Contains("已经更加"), "AI changed the note before preview confirmation.");
                    using var request = JsonDocument.Parse(submitted ?? "{}");
                    Check(request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == "这段话嗯表达不太好。", "The note UI submitted text outside the selected range.");
                    dialog.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(dialog);
                    using (var output = File.Create(Path.Combine(_root, "ai-preview.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output); }
                    driver.Stop(); ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception ex) { failure = ex; driver.Stop(); dialog.Close(); }
            };
            driver.Start(); ClickAiAction(window, AiAction.PolishId); driver.Stop();
            if (failure is not null) throw failure;
            Check(note.PlainText == "不要发送前文。这段话已经更加通顺了。不要发送后文。" && handler.Calls == 1, "AI did not replace exactly the original selection.");
            Check(workspace.Store.History.ReadTimeline().Events.Last(e => e.Target.StartsWith("notes/", StringComparison.Ordinal)).Reason == "AI · 语法修正与润色", "AI replacement did not append a distinct history entry.");
            editor.Undo(); Check(window.SaveNow(false) && note.PlainText == original, "AI result could not be undone and saved.");
            editor.SelectAll();
            var durable = File.ReadAllBytes(Path.Combine(workspace.Store.NotesDirectory, note.Id + ".json"));
            var historyCount = workspace.Store.History.ReadTimeline().Events.Count;
            var cancel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            cancel.Tick += (_, _) =>
            {
                if (System.Windows.Application.Current.Windows.OfType<AiPolishWindow>().FirstOrDefault() is not { } dialog) return;
                cancel.Stop(); dialog.Close();
            };
            cancel.Start(); ClickAiAction(window, AiAction.PolishId); cancel.Stop(); Pump(30);
            Check(File.ReadAllBytes(Path.Combine(workspace.Store.NotesDirectory, note.Id + ".json")).SequenceEqual(durable)
                && workspace.Store.History.ReadTimeline().Events.Count == historyCount, "Cancelling a preview wrote a note or created a history event.");
        }
        finally { workspace.PrepareExit(); }
    }
    private static void ClickAiAction(NoteWindow window, Guid id)
    {
        var button = (Button)window.FindName("AiButton"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20);
        var menu = button.ContextMenu!; var item = menu.Items.OfType<MenuItem>().Single(i => i.Tag is Guid key && key == id);
        menu.IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
}
