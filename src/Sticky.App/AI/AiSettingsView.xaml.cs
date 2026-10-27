using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sticky.Core;
using Sticky.Sync.Git;

namespace Sticky.App.AI;

public partial class AiSettingsView : UserControl
{
    private readonly StickyWorkspace _workspace;
    private AiConfiguration _saved = new();
    private AiCatalog _connectionBaseline = new(), _actionBaseline = new();
    private AiAction? _draft;
    private CancellationTokenSource? _test;
    private bool _loading, _saving, _subscribed;
    public AiSettingsView(StickyWorkspace workspace)
    {
        _workspace = workspace; InitializeComponent(); IconBox.ItemsSource = AiActionIcons.Choices;
        Reload();
        Loaded += (_, _) => { if (!_subscribed) { workspace.AiSettings.Changed += SharedChanged; _subscribed = true; } };
        Unloaded += (_, _) => { _test?.Cancel(); if (_subscribed) { workspace.AiSettings.Changed -= SharedChanged; _subscribed = false; } };
    }
    private void SharedChanged()
    {
        if (_saving) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(SharedChanged); return; }
        RefreshNotice.Text = "AI 配置有更新。未保存的草稿保持不变，可点击右侧按钮载入。";
    }
    private void ReloadClicked(object sender, RoutedEventArgs e) => Reload();
    private void Reload()
    {
        try
        {
            _test?.Cancel(); FillConnection(null);
            _actionBaseline = _workspace.AiSettings.EditingCatalog(); FillActions(AiAction.PolishId);
            RefreshNotice.Text = "共享配置随 GitHub 同步；API 密钥只保存在本机。";
            StatusText.Text = _workspace.AiSettings.Warning ?? "尚未发起任何 AI 请求。";
        }
        catch (Exception) { StatusText.Text = "AI 配置无法读取，原文件已保留，请检查本地配置。"; }
    }
    private void FillConnection(Guid? id)
    {
        _loading = true;
        try
        {
            _saved = _workspace.AiSettings.Load(id);
            _connectionBaseline = _workspace.AiSettings.EditingCatalog();
            var choices = _connectionBaseline.Connections.Where(c => c.DeletedAt is null).ToList();
            if (choices.Count == 0) choices.Add(new AiConnection());
            ConnectionBox.ItemsSource = choices; ConnectionBox.SelectedValue = _saved.ConnectionId;
            EnabledBox.IsChecked = _saved.Enabled; BaseUrlBox.Text = _saved.BaseUrl; ModelBox.Text = _saved.Model;
            TimeoutBox.Text = _saved.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ThinkingBox.IsChecked = _saved.ThinkingEnabled; ThinkingControlBox.SelectedValue = _saved.ThinkingControl;
            KeyBox.Clear(); RefreshKeyHint();
        }
        finally { _loading = false; }
    }
    private void ConnectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ConnectionBox.SelectedValue is not Guid id) return;
        try { _workspace.AiSettings.SelectConnection(id); FillConnection(id); StatusText.Text = _workspace.AiSettings.Warning ?? "已切换本机使用的连接。"; }
        catch (Exception) { ShowStatus("无法切换连接，请检查本机配置。", true); }
    }
    private void RefreshKeyHint() => KeyHint.Text = _saved.ApiKey.Length > 0
        ? "本机已保存密钥。留空保留现有密钥，输入新值可替换。" : "当前设备尚无此服务的密钥；免密本机服务可以留空。";
    private AiConfiguration ReadFields(bool forTest)
    {
        if (!int.TryParse(TimeoutBox.Text, out var seconds)) throw new AiException("请求超时请输入 5–180 秒。");
        var key = KeyBox.Password.Trim();
        var config = new AiConfiguration { ConnectionId = _saved.ConnectionId, ConnectionName = _saved.ConnectionName,
            Enabled = forTest || EnabledBox.IsChecked == true, BaseUrl = BaseUrlBox.Text.Trim(), Model = ModelBox.Text.Trim(), TimeoutSeconds = seconds,
            ThinkingEnabled = ThinkingBox.IsChecked == true, ThinkingControl = (ThinkingControlBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto" };
        if (key.Length == 0 && _saved.ApiKey.Length > 0 && config.BaseUrl.Length > 0)
        {
            if (config.CompletionUri() == _saved.CompletionUri()) key = _saved.ApiKey;
            else throw new AiException("API 地址已更改，请重新输入密钥，避免将旧服务的密钥发送到新地址。");
        }
        config = new() { ConnectionId = config.ConnectionId, ConnectionName = config.ConnectionName, Enabled = config.Enabled, BaseUrl = config.BaseUrl, Model = config.Model,
            TimeoutSeconds = seconds, ApiKey = key, ThinkingEnabled = config.ThinkingEnabled, ThinkingControl = config.ThinkingControl };
        config.Validate(connectionRequired: forTest); return config;
    }
    private void ShowStatus(string message, bool error = false)
    { StatusText.Foreground = error ? Brushes.Firebrick : Brushes.DarkGreen; StatusText.Text = message; }
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        _saving = true;
        try
        {
            var config = ReadFields(false); _workspace.AiSettings.Save(config, _connectionBaseline); FillConnection(null);
            ShowStatus("连接已保存；共享参数将参与同步，密钥仅保存在本机。");
        }
        catch (Exception ex) { ShowStatus(ex is AiException ? ex.Message : "无法保存连接，请检查参数与数据目录。", true); }
        finally { _saving = false; }
    }
    private async void TestClicked(object sender, RoutedEventArgs e)
    {
        if (_test is not null) return;
        using var source = new CancellationTokenSource(); _test = source; TestButton.IsEnabled = false; CancelTestButton.IsEnabled = true;
        try
        {
            var config = ReadFields(true); ShowStatus("正在测试连接，仅发送固定示例文本…");
            await _workspace.AiClient.PolishAsync(config, "这是一条用于测试 AI 连接的示例文本。", source.Token);
            ShowStatus("连接成功。尚未保存的配置请点击「保存连接」。");
        }
        catch (OperationCanceledException) { ShowStatus("已取消连接测试。"); }
        catch (Exception ex) { ShowStatus(ex is AiException ? ex.Message : "测试未完成，请检查配置后重试。", true); }
        finally { _test = null; TestButton.IsEnabled = true; CancelTestButton.IsEnabled = false; }
    }
    private void CancelTest(object sender, RoutedEventArgs e) => _test?.Cancel();
    private void ForgetKey(object sender, RoutedEventArgs e)
    {
        _saving = true;
        try
        {
            _test?.Cancel(); _workspace.AiSettings.ForgetKey(_saved);
            _saved = _workspace.AiSettings.Load(_saved.ConnectionId); KeyBox.Clear(); RefreshKeyHint();
            ShowStatus("已移除本机密钥，其他设备的连接不受影响。");
        }
        catch (Exception) { ShowStatus("未能移除本机密钥，请检查配置文件是否可写。", true); }
        finally { _saving = false; }
    }
    private void FillActions(Guid? select)
    {
        _loading = true;
        var actions = _actionBaseline.Actions.Where(a => a.DeletedAt is null).OrderBy(a => a.Order).ThenBy(a => a.Id).ToList();
        ActionList.ItemsSource = actions; ActionList.SelectedItem = actions.FirstOrDefault(a => a.Id == select) ?? actions.FirstOrDefault();
        _loading = false;
        if (ActionList.SelectedItem is AiAction action) EditAction(action); else NewAction();
    }
    private void ActionSelected(object sender, SelectionChangedEventArgs e)
    { if (!_loading && ActionList.SelectedItem is AiAction action) EditAction(action); }
    private void EditAction(AiAction action)
    {
        _draft = SyncJson.Read<AiAction>(SyncJson.Bytes(action)); ActionEditorTitle.Text = "编辑功能";
        ActionNameBox.Text = action.Name; PromptBox.Text = action.Prompt; IconBox.SelectedValue = action.Icon; ActionEnabledBox.IsChecked = action.Enabled;
        DeleteActionButton.IsEnabled = true; ActionStatus.Text = "";
    }
    private void NewAction()
    {
        _loading = true; ActionList.SelectedIndex = -1; _loading = false;
        _draft = new AiAction { Order = Math.Min(100000, _actionBaseline.Actions.Select(a => a.Order).DefaultIfEmpty(-1).Max() + 1) };
        ActionEditorTitle.Text = "新建功能"; ActionNameBox.Clear(); PromptBox.Clear(); IconBox.SelectedValue = "sparkles"; ActionEnabledBox.IsChecked = true;
        DeleteActionButton.IsEnabled = false; ActionStatus.Text = "填写名称、提示词并保存，即可在便签 AI 菜单中使用。";
    }
    private void NewActionClicked(object sender, RoutedEventArgs e) => NewAction();
    private void SaveActionClicked(object sender, RoutedEventArgs e)
    {
        if (_draft is null) return;
        var action = SyncJson.Read<AiAction>(SyncJson.Bytes(_draft));
        action.Name = ActionNameBox.Text.Trim(); action.Prompt = PromptBox.Text.Trim(); action.Icon = IconBox.SelectedValue as string ?? "sparkles"; action.Enabled = ActionEnabledBox.IsChecked == true;
        SaveAction(action, false);
    }
    private void DeleteActionClicked(object sender, RoutedEventArgs e)
    {
        if (_draft is null) return;
        var action = SyncJson.Read<AiAction>(SyncJson.Bytes(_draft)); action.DeletedAt = DateTimeOffset.UtcNow;
        SaveAction(action, true);
    }
    private void SaveAction(AiAction action, bool deleted)
    {
        _saving = true;
        try
        {
            var id = _workspace.AiSettings.SaveAction(action, _actionBaseline);
            _actionBaseline = _workspace.AiSettings.EditingCatalog(); FillActions(id);
            ActionStatus.Foreground = Brushes.DarkGreen; ActionStatus.Text = deleted ? "已删除，删除记录会参与同步。" : "已保存，便签 AI 菜单立即可用；并发修改会保留冲突副本。";
        }
        catch (Exception ex)
        { ActionStatus.Foreground = Brushes.Firebrick; ActionStatus.Text = ex is System.IO.InvalidDataException or AiException ? ex.Message : "功能保存失败，草稿已保留。"; }
        finally { _saving = false; }
    }
}
