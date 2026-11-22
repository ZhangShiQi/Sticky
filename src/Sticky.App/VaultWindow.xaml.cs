using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Sticky.App.Editor;
using Sticky.Core;

namespace Sticky.App;

public partial class VaultWindow : Window
{
    private readonly StickyWorkspace _workspace;
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<Window> _privateDialogs = [];
    private Window DialogOwner => (Window?)_workspace.Manager ?? this;
    private bool _busy;
    private bool _lockAfterOperation;
    private DateTimeOffset _lastInput = DateTimeOffset.UtcNow;
    private Storage.VaultStore Vault => _workspace.Vault;
    public VaultWindow(StickyWorkspace workspace)
    {
        InitializeComponent(); _workspace = workspace;
        _idleTimer.Tick += (_, _) =>
        {
            if (Vault.IsUnlocked && (_lockAfterOperation || DateTimeOffset.UtcNow - _lastInput >= TimeSpan.FromMinutes(5))) LockVault();
        };
        SessionEventsSubscribe();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
        };
        _clipboardTimer.Tick += (_, _) => ClearCopiedText();
        PreviewKeyDown += (_, _) => Touch(); PreviewMouseDown += (_, _) => Touch();
        _idleTimer.Start(); RefreshState();
    }
    private void SessionEventsSubscribe()
    {
        SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged;
    }
    private void SessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.BeginInvoke(() => { _lockAfterOperation = true; LockVault(); });
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.BeginInvoke(() => { _lockAfterOperation = true; LockVault(); });
    }
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private string? _copiedText;
    public void CopyPrivateText(string text)
    {
        Clipboard.SetText(text); _copiedText = text;
        _clipboardTimer.Stop(); _clipboardTimer.Start();
    }
    private void ClearCopiedText()
    {
        _clipboardTimer.Stop();
        try { if (_copiedText is not null && Clipboard.ContainsText() && Clipboard.GetText() == _copiedText) Clipboard.Clear(); }
        catch (System.Runtime.InteropServices.ExternalException) { }
        _copiedText = null;
    }
    public void Touch() => _lastInput = DateTimeOffset.UtcNow;
    private void RefreshState()
    {
        UnlockButton.IsEnabled = !Vault.IsUnlocked;
        UnlockButton.Content = Vault.Exists ? "解锁" : "创建保险箱";
        RecoveryButton.IsEnabled = Vault.Exists && !Vault.IsUnlocked;
        LockButton.IsEnabled = PasswordButton.IsEnabled = Vault.IsUnlocked;
        ExportButton.IsEnabled = Vault.Exists; ImportButton.IsEnabled = !Vault.Exists;
        Status.Text = Vault.IsUnlocked ? "已解锁 · 编辑内容自动加密保存" : "已锁定 · 输入主密码后查看";
    }
    public async Task<bool> EnsureUnlockedAsync()
    {
        if (_workspace.VaultViewsHidden || _busy) return false;
        if (Vault.IsUnlocked) return true;
        var create = !Vault.Exists;
        var password = VaultPasswordPrompt.Show(DialogOwner, create ? "创建保险箱" : "解锁保险箱", confirm: create);
        if (password is null) return false;
        string? recovery = null;
        if (!await CryptoAsync(() => { if (create) recovery = Vault.Create(password); else Vault.Unlock(password); })) return false;
        _workspace.RefreshPrivateNotes();
        if (recovery is not null) ShowRecovery(recovery);
        return Vault.IsUnlocked;
    }
    private async Task<bool> CryptoAsync(Action operation)
    {
        if (_busy) return false;
        _busy = true; IsEnabled = false; Status.Text = "正在处理加密数据…";
        try { if (Vault.IsUnlocked) operation(); else await Task.Run(operation); return true; }
        catch (Exception ex) { StickyWorkspace.ShowError("无法打开或更新保险箱", ex); return false; }
        finally
        {
            _busy = false; IsEnabled = true; Touch(); RefreshState();
            if (_lockAfterOperation) LockVault();
        }
    }
    private async void UnlockClicked(object sender, RoutedEventArgs e) => await EnsureUnlockedAsync();
    private async void RecoveryClicked(object sender, RoutedEventArgs e)
    {
        var secret = VaultPasswordPrompt.Show(DialogOwner, "使用恢复密钥解锁", recovery: true);
        if (secret is not null && await CryptoAsync(() => Vault.UnlockWithRecovery(secret))) _workspace.RefreshPrivateNotes();
    }
    private void ShowRecovery(string secret)
    {
        if (!Vault.IsUnlocked) return;
        var dialog = new Window { Owner = DialogOwner, Title = "保存恢复密钥", Width = 580, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "请抄写或离线保存这份恢复密钥，并与保险箱分开保管。它可以解锁整个保险箱。主密码和恢复密钥都丢失后无法找回内容。此密钥仅在创建时显示。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { Text = secret, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 16) });
        var saved = new CheckBox { Content = "我已单独保存恢复密钥" }; panel.Children.Add(saved);
        var close = new Button { Content = "完成", IsEnabled = false };
        saved.Checked += (_, _) => close.IsEnabled = true; saved.Unchecked += (_, _) => close.IsEnabled = false;
        close.Click += (_, _) => dialog.Close(); panel.Children.Add(close); dialog.Content = panel;
        _privateDialogs.Add(dialog); dialog.ShowDialog(); _privateDialogs.Remove(dialog);
        panel.Children.Clear();
    }
    private async void PasswordClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) return;
        var password = VaultPasswordPrompt.Show(DialogOwner, "修改主密码", confirm: true);
        if (password is not null) await CryptoAsync(() => Vault.ChangePassword(password));
    }
    private void LockClicked(object sender, RoutedEventArgs e) => LockVault();
    public bool SaveCurrent() => _workspace.SavePrivateNotes();
    public bool LockVault()
    {
        if (_busy) { _lockAfterOperation = true; return false; }
        ClearCopiedText();
        foreach (var dialog in _privateDialogs.ToArray()) dialog.Close();
        if (!_workspace.LockPrivateNotes())
        {
            _lockAfterOperation = true;
            Status.Text = "保存失败，已隐藏私密窗口，正在重试保存并锁定。";
            _workspace.NotifyChanged();
            return false;
        }
        _lockAfterOperation = false; RefreshState(); return true;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_workspace.IsExiting) { if (!LockVault()) e.Cancel = true; }
        else { e.Cancel = true; Hide(); }
    }
    public void RefreshNotes(Guid? select = null) => _workspace.RefreshPrivateNotes();
    public void ShowNoteHistory(Guid id)
    {
        if (!SaveCurrent() || !Vault.IsUnlocked) return;
        var versions = Vault.Versions(id).Reverse().ToList();
        var dialog = new Window { Owner = DialogOwner, Title = "保险箱 · 历史版本", Width = 700, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.PreviewKeyDown += (_, _) => Touch(); dialog.PreviewMouseDown += (_, _) => Touch();
        var panel = new DockPanel { Margin = new Thickness(20) };
        var list = new ListBox { Height = 150, ItemsSource = versions };
        list.ItemTemplate = new DataTemplate(typeof(Note))
        {
            VisualTree = new FrameworkElementFactory(typeof(TextBlock))
        };
        var label = new System.Windows.Data.MultiBinding { StringFormat = "{0:yyyy-MM-dd HH:mm:ss} · {1}" };
        label.Bindings.Add(new System.Windows.Data.Binding(nameof(Note.ModifiedAt)));
        label.Bindings.Add(new System.Windows.Data.Binding(nameof(Note.FormatLabel)));
        list.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, label);
        DockPanel.SetDock(list, Dock.Top); panel.Children.Add(list);
        var restore = new Button { Content = "恢复选中版本", IsEnabled = false }; DockPanel.SetDock(restore, Dock.Bottom); panel.Children.Add(restore);
        var preview = new RichTextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(preview);
        list.SelectionChanged += (_, _) =>
        {
            restore.IsEnabled = list.SelectedItem is Note;
            if (list.SelectedItem is Note note) preview.Document = DocumentCodec.Decode(note.GetBodyBlocks(), asset => new MemoryStream(Vault.ReadImage(asset), false), renderMarkdown: note.IsMarkdown);
        };
        restore.Click += (_, _) =>
        {
            if (!Vault.IsUnlocked || list.SelectedItem is not Note note) return;
            try { note.ModifiedAt = DateTimeOffset.UtcNow; note.DeletedAt = null; Vault.Save(note); dialog.Close();
                var current = _workspace.PrivateNotes.Single(n => n.Id == id);
                current.Title = note.Title; current.CopyBodyFrom(note); current.Color = note.Color; current.ModifiedAt = note.ModifiedAt; current.DeletedAt = note.DeletedAt;
                if (_workspace.Windows.TryGetValue(id, out var editor)) editor.ReloadAfterSync(true, "已恢复私密历史版本");
                _workspace.NotifyChanged(); }
            catch (Exception ex) { StickyWorkspace.ShowError("无法恢复私密历史", ex); }
        };
        dialog.Content = panel; _privateDialogs.Add(dialog); dialog.ShowDialog(); _privateDialogs.Remove(dialog);
        list.ItemsSource = null; preview.Document = new FlowDocument();
    }
    public void MoveOut(Guid id)
    {
        if (!SaveCurrent()) return;
        if (MessageBox.Show(DialogOwner, "移出后，当前内容和图片将以明文保存，并可能参与 Git 同步。保险箱内的该便签及其历史将移除。继续？", "移出保险箱", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            if (!Vault.IsUnlocked || _workspace.VaultViewsHidden || !SaveCurrent()) return;
            var note = Vault.Notes.Single(n => n.Id == id); var oldId = note.Id;
            foreach (var asset in note.GetImageIds().Distinct()) _workspace.Store.AddImage(Vault.ReadImage(asset));
            note.Id = Guid.NewGuid(); note.DirectoryId = _workspace.EnsureInboxDirectory(); note.ConflictOf = null; note.ModifiedAt = DateTimeOffset.UtcNow;
            note.SetTags([]);
            _workspace.Store.Save(note); _workspace.Notes.Add(note); _workspace.NotifyChanged();
            if (_workspace.Windows.TryGetValue(oldId, out var editor)) { editor.Close(); editor.ClearPrivateContent(); }
            Vault.Remove(oldId); RefreshNotes();
        }
        catch (Exception ex) { StickyWorkspace.ShowError("移出未完成；请检查普通便签和保险箱，已有副本会保留", ex); }
    }
    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) return;
        var picker = new SaveFileDialog { Filter = "加密保险箱|*.stickyvault", FileName = "保险箱.stickyvault" };
        if (picker.ShowDialog(DialogOwner) != true) return;
        if (!SaveCurrent()) return;
        try { Vault.Export(picker.FileName); Status.Text = "已导出加密保险箱；换电脑后导入并输入同一主密码。"; }
        catch (Exception ex) { StickyWorkspace.ShowError("无法导出保险箱", ex); }
    }
    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "加密保险箱或 Sticky 备份|*.stickyvault;*.zip" };
        if (picker.ShowDialog(DialogOwner) != true) return;
        var password = VaultPasswordPrompt.Show(DialogOwner, "输入此保险箱的主密码");
        if (password is null) return;
        if (await CryptoAsync(() => Vault.Import(picker.FileName, password))) Status.Text = "已导入加密保险箱，点击解锁查看。";
    }
}
