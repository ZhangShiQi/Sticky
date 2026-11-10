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
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, byte[]> _newImages = [];
    private readonly List<Window> _privateDialogs = [];
    private Note? _selected;
    private bool _loading;
    private bool _dirty;
    private bool _busy;
    private bool _lockAfterOperation;
    private string? _copiedText;
    private DateTimeOffset _lastInput = DateTimeOffset.UtcNow;
    private Storage.VaultStore Vault => _workspace.Vault;
    public VaultWindow(StickyWorkspace workspace)
    {
        InitializeComponent(); _workspace = workspace;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveCurrent(); };
        _idleTimer.Tick += (_, _) =>
        {
            if (Vault.IsUnlocked && (_lockAfterOperation || DateTimeOffset.UtcNow - _lastInput >= TimeSpan.FromMinutes(5))) LockVault();
        };
        _clipboardTimer.Tick += (_, _) => ClearCopiedText();
        PreviewKeyDown += (_, _) => Touch(); PreviewMouseDown += (_, _) => Touch(); PreviewMouseMove += (_, _) => Touch();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => SaveCurrent()));
        Editor.AddHandler(DataObject.PastingEvent, new DataObjectPastingEventHandler(Pasting));
        // Avoid WPF's implicit clipboard export of embedded images/RTF to other apps.
        Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, e) => { CopySelection(); e.Handled = true; }));
        Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, (_, e) => { CopySelection(); Editor.Selection.Text = ""; e.Handled = true; }));
        SessionEventsSubscribe();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _saveTimer.Stop(); _idleTimer.Stop(); ClearCopiedText();
            SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
        };
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
    private void Touch() => _lastInput = DateTimeOffset.UtcNow;
    private void RefreshState()
    {
        UnlockedPanel.Visibility = Vault.IsUnlocked ? Visibility.Visible : Visibility.Collapsed;
        LockedHint.Visibility = Vault.IsUnlocked ? Visibility.Collapsed : Visibility.Visible;
        UnlockButton.IsEnabled = !Vault.IsUnlocked;
        UnlockButton.Content = Vault.Exists ? "解锁" : "创建保险箱";
        RecoveryButton.IsEnabled = Vault.Exists && !Vault.IsUnlocked;
        LockButton.IsEnabled = PasswordButton.IsEnabled = Vault.IsUnlocked;
        ExportButton.IsEnabled = Vault.Exists; ImportButton.IsEnabled = !Vault.Exists;
        Status.Text = Vault.IsUnlocked ? "已解锁 · 编辑内容自动加密保存" : "已锁定 · 输入主密码后查看";
    }
    public async Task<bool> EnsureUnlockedAsync()
    {
        if (Vault.IsUnlocked) return true;
        var create = !Vault.Exists;
        var password = VaultPasswordPrompt.Show(this, create ? "创建保险箱" : "解锁保险箱", confirm: create);
        if (password is null) return false;
        string? recovery = null;
        if (!await CryptoAsync(() => { if (create) recovery = Vault.Create(password); else Vault.Unlock(password); })) return false;
        RefreshNotes();
        if (recovery is not null) ShowRecovery(recovery);
        return Vault.IsUnlocked;
    }
    private async Task<bool> CryptoAsync(Action operation)
    {
        if (_busy) return false;
        _busy = true; IsEnabled = false; Status.Text = "正在处理加密数据…";
        try { await Task.Run(operation); return true; }
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
        var secret = VaultPasswordPrompt.Show(this, "使用恢复密钥解锁", recovery: true);
        if (secret is not null && await CryptoAsync(() => Vault.UnlockWithRecovery(secret))) RefreshNotes();
    }
    private void ShowRecovery(string secret)
    {
        if (!Vault.IsUnlocked) return;
        var dialog = new Window { Owner = this, Title = "保存恢复密钥", Width = 580, SizeToContent = SizeToContent.Height,
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
        var password = VaultPasswordPrompt.Show(this, "修改主密码", confirm: true);
        if (password is not null) await CryptoAsync(() => Vault.ChangePassword(password));
    }
    private void LockClicked(object sender, RoutedEventArgs e) => LockVault();
    public bool LockVault()
    {
        if (_busy) { _lockAfterOperation = true; return false; }
        foreach (var dialog in OwnedWindows.Cast<Window>().ToArray()) dialog.Close();
        if (!SaveCurrent())
        {
            // Do not claim a lock succeeded or silently discard unsaved work.
            _lockAfterOperation = true; UnlockedPanel.Visibility = Visibility.Collapsed;
            Status.Text = "保存失败，已隐藏私密内容；正在重试保存并锁定，请勿退出。";
            return false;
        }
        _saveTimer.Stop(); _loading = true;
        NoteList.ItemsSource = null; _selected = null; TitleBox.Clear(); Editor.Document = new FlowDocument();
        EditorPanel.IsEnabled = false; WipeNewImages(); ClearCopiedText(); Vault.Lock();
        _loading = false; _dirty = false; _lockAfterOperation = false; RefreshState(); return true;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!LockVault()) e.Cancel = true;
    }
    public void RefreshNotes(Guid? select = null)
    {
        if (!Vault.IsUnlocked) return;
        _loading = true;
        NoteList.ItemsSource = Vault.Notes.Where(n => (n.DeletedAt is not null) == (ShowTrash.IsChecked == true)).OrderByDescending(n => n.ModifiedAt).ToList();
        NoteList.SelectedItem = NoteList.Items.Cast<Note>().FirstOrDefault(n => n.Id == select);
        _loading = false; LoadSelection(NoteList.SelectedItem as Note);
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var chosen = NoteList.SelectedItem as Note;
        if (!SaveCurrent())
        {
            _loading = true; NoteList.SelectedItem = NoteList.Items.Cast<Note>().FirstOrDefault(n => n.Id == _selected?.Id); _loading = false; return;
        }
        LoadSelection(chosen);
    }
    private void LoadSelection(Note? note)
    {
        _loading = true; _selected = note; WipeNewImages();
        TitleBox.Text = note?.Title ?? "";
        Editor.Document = DocumentCodec.Decode(note?.Blocks ?? [], id => new MemoryStream(Vault.ReadImage(id), writable: false), preferences: _workspace.EditorPreferences);
        EditorPanel.IsEnabled = note is not null; Editor.IsReadOnly = note?.DeletedAt is not null;
        TitleBox.IsReadOnly = note?.DeletedAt is not null;
        DeleteButton.Content = note?.DeletedAt is null ? "移入回收站" : "恢复便签";
        _dirty = false; _loading = false;
    }
    private void Edited(object sender, TextChangedEventArgs e)
    {
        if (_loading || _selected is null || !Vault.IsUnlocked) return;
        _dirty = true; Touch(); _saveTimer.Stop(); _saveTimer.Start();
    }
    public bool SaveCurrent()
    {
        if (!_dirty || _selected is null) return true;
        try
        {
            var note = new Note { Id = _selected.Id, Title = TitleBox.Text, Blocks = DocumentCodec.Encode(Editor.Document),
                Color = _selected.Color, GroupId = _selected.GroupId, ConflictOf = _selected.ConflictOf,
                CreatedAt = _selected.CreatedAt, ModifiedAt = DateTimeOffset.UtcNow, DeletedAt = _selected.DeletedAt };
            Vault.Save(note, _newImages); _selected = note; _dirty = false; WipeNewImages();
            Status.Text = "已加密保存 · " + DateTime.Now.ToString("HH:mm:ss");
            // Refresh labels without resetting the active editor or its caret.
            _loading = true;
            NoteList.ItemsSource = Vault.Notes.Where(n => (n.DeletedAt is not null) == (ShowTrash.IsChecked == true)).OrderByDescending(n => n.ModifiedAt).ToList();
            NoteList.SelectedItem = NoteList.Items.Cast<Note>().FirstOrDefault(n => n.Id == note.Id);
            _loading = false; return true;
        }
        catch (Exception ex) { _loading = false; Status.Text = "未保存：" + ex.Message; return false; }
    }
    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) MessageBox.Show(this, Status.Text, "保险箱保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    private void NewClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) return;
        try { var note = new Note(); Vault.Save(note); ShowTrash.IsChecked = false; RefreshNotes(note.Id); TitleBox.Focus(); }
        catch (Exception ex) { StickyWorkspace.ShowError("无法创建私密便签", ex); }
    }
    private void TrashClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) { ShowTrash.IsChecked = !ShowTrash.IsChecked; return; } RefreshNotes();
    }
    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent() || _selected is null) return;
        try
        {
            var next = Vault.Notes.Single(n => n.Id == _selected.Id);
            next.DeletedAt = next.DeletedAt is null ? DateTimeOffset.UtcNow : null; next.ModifiedAt = DateTimeOffset.UtcNow;
            Vault.Save(next); RefreshNotes();
        }
        catch (Exception ex) { StickyWorkspace.ShowError("无法更新保险箱回收站", ex); }
    }
    private void ImageClicked(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selected.DeletedAt is not null) return;
        var picker = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            using var stream = File.OpenRead(picker.FileName);
            if (stream.Length > 20 * 1024 * 1024) throw new IOException("请选择小于 20 MB 的图片。");
            var bitmap = DocumentCodec.LoadBitmap(stream);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); var bytes = output.ToArray();
            var id = Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".png"; _newImages[id] = bytes;
            var image = DocumentCodec.CreateImage(id, 280, _ => new MemoryStream(bytes, writable: false));
            Editor.CaretPosition = new InlineUIContainer(image, Editor.CaretPosition).ElementEnd; _dirty = true; SaveCurrent();
        }
        catch (Exception ex) { StickyWorkspace.ShowError("无法插入图片", ex); }
    }
    private void Pasting(object sender, DataObjectPastingEventArgs e)
    {
        // Import text only, so clipboard RTF cannot reference external files or leave
        // unmanaged image assets. Images use the explicit in-memory import above.
        if (e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)) e.FormatToApply = DataFormats.UnicodeText;
        else e.CancelCommand();
    }
    private void CopyClicked(object sender, RoutedEventArgs e) => CopySelection();
    private void CopySelection()
    {
        try
        {
            var text = Editor.Selection.Text; if (text.Length == 0) return;
            Clipboard.SetText(text); _copiedText = text; _clipboardTimer.Stop(); _clipboardTimer.Start();
        }
        catch (System.Runtime.InteropServices.ExternalException) { Status.Text = "剪贴板忙，请重试。"; }
    }
    private void ClearCopiedText()
    {
        _clipboardTimer.Stop();
        try { if (_copiedText is not null && Clipboard.ContainsText() && Clipboard.GetText() == _copiedText) Clipboard.Clear(); }
        catch (System.Runtime.InteropServices.ExternalException) { }
        _copiedText = null;
    }
    private void WipeNewImages() { foreach (var bytes in _newImages.Values) CryptographicOperations.ZeroMemory(bytes); _newImages.Clear(); }
    private void HistoryClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent() || _selected is null) return;
        var id = _selected.Id; var versions = Vault.Versions(id).Reverse().ToList();
        var dialog = new Window { Owner = this, Title = "保险箱 · 历史版本", Width = 700, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new Thickness(20) };
        var list = new ListBox { Height = 150, DisplayMemberPath = "ModifiedAt", ItemsSource = versions };
        DockPanel.SetDock(list, Dock.Top); panel.Children.Add(list);
        var restore = new Button { Content = "恢复选中版本", IsEnabled = false }; DockPanel.SetDock(restore, Dock.Bottom); panel.Children.Add(restore);
        var preview = new RichTextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(preview);
        list.SelectionChanged += (_, _) =>
        {
            restore.IsEnabled = list.SelectedItem is Note;
            if (list.SelectedItem is Note note) preview.Document = DocumentCodec.Decode(note.Blocks, asset => new MemoryStream(Vault.ReadImage(asset), false));
        };
        restore.Click += (_, _) =>
        {
            if (!Vault.IsUnlocked || list.SelectedItem is not Note note) return;
            try { note.ModifiedAt = DateTimeOffset.UtcNow; Vault.Save(note); dialog.Close(); RefreshNotes(id); }
            catch (Exception ex) { StickyWorkspace.ShowError("无法恢复私密历史", ex); }
        };
        dialog.Content = panel; _privateDialogs.Add(dialog); dialog.ShowDialog(); _privateDialogs.Remove(dialog);
        list.ItemsSource = null; preview.Document = new FlowDocument();
    }
    private void MoveOutClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent() || _selected is null) return;
        if (MessageBox.Show(this, "移出后，当前内容和图片将以明文保存，并可能参与 Git 同步。保险箱内的该便签及其历史将移除。继续？", "移出保险箱", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            var note = Vault.Notes.Single(n => n.Id == _selected.Id); var oldId = note.Id;
            foreach (var asset in Storage.VaultStore.ImageIds(note.Blocks).Distinct()) _workspace.Store.AddImage(Vault.ReadImage(asset));
            note.Id = Guid.NewGuid(); note.GroupId = null; note.ConflictOf = null; note.ModifiedAt = DateTimeOffset.UtcNow;
            _workspace.Store.Save(note); _workspace.Notes.Add(note); _workspace.NotifyChanged();
            Vault.Remove(oldId); RefreshNotes();
        }
        catch (Exception ex) { StickyWorkspace.ShowError("移出未完成；请检查普通便签和保险箱，已有副本会保留", ex); }
    }
    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (!SaveCurrent()) return;
        var picker = new SaveFileDialog { Filter = "加密保险箱|*.stickyvault", FileName = "保险箱.stickyvault" };
        if (picker.ShowDialog(this) != true) return;
        try { Vault.Export(picker.FileName); Status.Text = "已导出加密保险箱；换电脑后导入并输入同一主密码。"; }
        catch (Exception ex) { StickyWorkspace.ShowError("无法导出保险箱", ex); }
    }
    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "加密保险箱或 Sticky 备份|*.stickyvault;*.zip" };
        if (picker.ShowDialog(this) != true) return;
        var password = VaultPasswordPrompt.Show(this, "输入此保险箱的主密码");
        if (password is null) return;
        if (await CryptoAsync(() => Vault.Import(picker.FileName, password))) Status.Text = "已导入加密保险箱，点击解锁查看。";
    }
}
