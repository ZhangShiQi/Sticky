using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App;
using Sticky.App.AI;
using Sticky.Core;
using Sticky.Storage;
using Sticky.Sync.Git;

internal static partial class Program
{
    private static void AiThinkingAndActions() => Task.Run(async () =>
    {
        var action = new AiAction { Name = "整理清单", Prompt = "提取待办事项，用清单呈现。", Icon = "list" };
        foreach (var mode in new[] { "auto", "deepseek", "effort", "none" })
        foreach (var enabled in new[] { false, true })
        {
            var config = new AiConfiguration { Enabled = true, BaseUrl = "https://api.deepseek.com/v1", Model = "test-model", ApiKey = "synthetic-test-key", ThinkingControl = mode, ThinkingEnabled = enabled };
            if (enabled && mode == "none") { ExpectAiFailure(() => config.Validate(), "思考"); continue; }
            var handler = new AiHttp(async (request, token) =>
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); var body = json.RootElement;
                var messages = body.GetProperty("messages");
                Check(messages[0].GetProperty("content").GetString()!.EndsWith(action.Prompt)
                    && messages[1].GetProperty("content").GetString() == "明天买牛奶，后天交报告。", "Custom instructions or selection were missing or mixed together.");
                if (mode is "auto" or "deepseek") Check(body.GetProperty("thinking").GetProperty("type").GetString() == (enabled ? "enabled" : "disabled") && !body.TryGetProperty("reasoning_effort", out _), "DeepSeek thinking switch was not explicit.");
                else if (mode == "effort") Check(body.GetProperty("reasoning_effort").GetString() == (enabled ? "high" : "none") && !body.TryGetProperty("thinking", out _), "Reasoning effort did not match the switch.");
                else Check(!body.TryGetProperty("thinking", out _) && !body.TryGetProperty("reasoning_effort", out _), "Unsupported thinking parameters were sent.");
                return AiResponse();
            });
            using var client = new AiClient(handler); await client.ExecuteAsync(config, action, "明天买牛奶，后天交报告。");
            Check(handler.Calls == 1, "AI action retried unexpectedly.");
        }
        Check(!new AiConfiguration().ThinkingEnabled && AiConfig().EffectiveThinkingControl == "none", "Thinking did not default off or an unknown host was guessed.");
        action.Enabled = false;
        using var noRequest = new AiClient(new AiHttp((_, _) => throw new InvalidOperationException("Disabled action made a request.")));
        ExpectAiFailure(() => noRequest.ExecuteAsync(AiConfig(), action, "原文").GetAwaiter().GetResult(), "停用");
    }).GetAwaiter().GetResult();

    private static void AiMigrationAndDraftConflicts()
    {
        var root = Path.Combine(_root, "ai-migration"); var store = new AiSettingsStore(root);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        var encrypted = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("legacy-synthetic-key"),
            Encoding.UTF8.GetBytes("Sticky.AI/v1|" + AiConfig().CompletionUri().AbsoluteUri), DataProtectionScope.CurrentUser));
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(new { Format = 1, Enabled = true, BaseUrl = AiConfig().BaseUrl, Model = "legacy-model", TimeoutSeconds = 70, ProtectedKey = encrypted }));
        var loaded = store.Load(); var catalog = store.EditingCatalog();
        Check(loaded.CanRun && loaded.ApiKey == "legacy-synthetic-key" && !loaded.ThinkingEnabled && loaded.Model == "legacy-model"
            && catalog.Actions.Single().Id == AiAction.PolishId, "Legacy migration lost credentials, model or default action.");
        Check(JsonDocument.Parse(File.ReadAllText(store.FilePath)).RootElement.GetProperty("Format").GetInt32() == 2, "Legacy migration did not finish the local format upgrade.");
        Check(!Encoding.UTF8.GetString(SyncJson.Bytes(catalog)).Contains("legacy-synthetic-key")
            && !Encoding.UTF8.GetString(SyncJson.Bytes(catalog)).Contains(encrypted), "Shared migration exported a credential.");
        var remote = SyncJson.Read<AiCatalog>(SyncJson.Bytes(catalog)); remote.Actions[0].Prompt = "远端改成只检查标点。";
        store.ApplyShared(remote);
        var draft = SyncJson.Read<AiAction>(SyncJson.Bytes(catalog.Actions[0])); draft.Prompt = "本机改成只整理表达。";
        var selectedId = store.SaveAction(draft, catalog); var merged = store.EditingCatalog();
        Check(merged.Actions.Count == 2 && merged.Actions.Any(a => a.Prompt == remote.Actions[0].Prompt)
            && merged.Actions.Single(a => a.Id == selectedId).Prompt == draft.Prompt, "Saving an open draft overwrote incoming changes or selected the wrong conflict copy.");
        var again = AiCatalogMerger.Merge(catalog, new AiCatalog { Connections = catalog.Connections, Actions = [draft] }, merged).Catalog!;
        Check(again.Actions.Count == 2, "Retrying a conflict created another identical action.");
        var independent = new AiAction { Name = "翻译", Prompt = "翻译成英文。", Icon = "translate" };
        store.SaveAction(independent, catalog);
        Check(store.EditingCatalog().Actions.Count == 3 && store.Load().ApiKey == "legacy-synthetic-key", "Saving an independent draft removed other actions or the local key.");
        var unknownField = "{\"schemaVersion\":1,\"actions\":[],\"connections\":[],\"apiKey\":\"secret\"}";
        try { NoteStore.ParseAiCatalog(Encoding.UTF8.GetBytes(unknownField)); throw new InvalidOperationException("Unknown credential field was accepted."); }
        catch (JsonException) { }
    }

    private static void AiCustomActionUi()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "ai-actions-ui"));
        try
        {
            workspace.ShowSettings(SettingsSection.AI); Pump(30);
            var view = (AiSettingsView)((ContentControl)workspace.Settings!.FindName("AiHost")).Content;
            ((TabControl)view.FindName("AiTabs")).SelectedIndex = 1;
            ((Button)view.FindName("NewActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBox)view.FindName("ActionNameBox")).Text = "翻译成英文";
            ((TextBox)view.FindName("PromptBox")).Text = "翻译成英文，保留段落结构。";
            ((ComboBox)view.FindName("IconBox")).SelectedValue = "translate";
            ((Button)view.FindName("SaveActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20);
            var action = workspace.AiSettings.EditingCatalog().Actions.Single(a => a.Name == "翻译成英文");
            Check(action.Icon == "translate" && action.Prompt.Contains("保留段落") && action.Enabled, "Custom action form did not persist the selected SVG and prompt.");
            ((TextBox)view.FindName("PromptBox")).Text = "尚未保存的草稿";
            var remote = workspace.AiSettings.EditingCatalog(); remote.Actions.Single(a => a.Id == action.Id).Prompt = "另一台设备的修改";
            workspace.AiSettings.ApplyShared(remote); Pump(20);
            Check(((TextBox)view.FindName("PromptBox")).Text == "尚未保存的草稿" && ((TextBlock)view.FindName("RefreshNotice")).Text.Contains("草稿"), "Incoming sync erased the open form draft.");
            ((Button)view.FindName("SaveActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20);
            var catalog = workspace.AiSettings.EditingCatalog();
            Check(catalog.Actions.Count(a => a.Prompt is "尚未保存的草稿" or "另一台设备的修改") == 2, "The editor did not preserve both conflicting prompts.");
            workspace.Settings.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)workspace.Settings.ActualWidth, (int)workspace.Settings.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(workspace.Settings);
            using (var output = File.Create(Path.Combine(_root, "ai-custom-actions.png"))) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); png.Save(output); }
            ((CheckBox)view.FindName("ActionEnabledBox")).IsChecked = false;
            ((Button)view.FindName("SaveActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var selected = (AiAction)((ListBox)view.FindName("ActionList")).SelectedItem;
            var selectedId = selected.Id;
            workspace.CreateNote(); var window = workspace.Windows.Values.Single();
            var editor = (RichTextBox)window.FindName("Editor"); editor.AppendText("测试文字"); editor.SelectAll();
            var button = (Button)window.FindName("AiButton"); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(20);
            var items = button.ContextMenu!.Items.OfType<MenuItem>().Where(i => i.Tag is Guid).ToList();
            Check(items.Count == catalog.Actions.Count - 1 && items.All(i => (Guid)i.Tag != selectedId && i.Icon is Viewbox), "The toolbar menu did not use active actions and shared SVG icons.");
            button.ContextMenu.IsOpen = false;
            ((Button)view.FindName("DeleteActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(workspace.AiSettings.EditingCatalog().Actions.Single(a => a.Id == selectedId).DeletedAt is not null, "Deleting an action did not retain a synchronization tombstone.");
        }
        finally { workspace.PrepareExit(); }
    }
}
