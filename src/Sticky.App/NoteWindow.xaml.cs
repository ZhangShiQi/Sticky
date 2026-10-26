using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Sticky.App.Editor;
using Sticky.App.ViewModels;
using Sticky.Core;
using WpfImage = System.Windows.Controls.Image;

namespace Sticky.App;

public partial class NoteWindow : Window
{
    private readonly StickyWorkspace _workspace;
    private readonly Note _note;
    private readonly Core.WindowState _state;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _loading = true;
    private bool _dirty;
    private string _color;
    private int _modalDepth;
    private bool _menuOpen;
    private bool _pointerOver;
    private bool _dragging;
    private bool _nativeMoving;
    private string _historyBatch = Guid.NewGuid().ToString("N");
    private bool _showHeaderOnHover;
    private static readonly DependencyPropertyKey IsHeaderExpandedPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsHeaderExpanded), typeof(bool), typeof(NoteWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsHeaderExpandedProperty = IsHeaderExpandedPropertyKey.DependencyProperty;
    public bool IsHeaderExpanded => (bool)GetValue(IsHeaderExpandedProperty);
    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(nameof(IsEditing), typeof(bool), typeof(NoteWindow), new PropertyMetadata(false));
    public bool IsEditing => (bool)GetValue(IsEditingProperty);

    public NoteWindow(StickyWorkspace workspace, Note note, Core.WindowState state, NoteWindow? near = null)
    {
        _workspace = workspace;
        _note = note;
        _state = state;
        _color = note.Color;
        _showHeaderOnHover = workspace.EditorPreferences.ShowHeaderOnHover;
        InitializeComponent();
        TitleBox.Text = note.Title;
        Title = note.DisplayTitle + " · Sticky";
        ApplyTheme();
        Editor.Document = DocumentCodec.Decode(note.Blocks, workspace.Store, darkTheme: true, workspace.EditorPreferences);
        Editor.ContextMenu = BuildNoteMenu();
        AttachImageMenus();
        Width = FiniteClamp(state.Width, 280, 1200, 390);
        Height = FiniteClamp(state.Height, 180, 1200, 320);
        Left = FiniteClamp(near is null ? state.Left : near.Left + 32, -1_000_000, 1_000_000, 190);
        Top = FiniteClamp(near is null ? state.Top : near.Top + 32, -1_000_000, 1_000_000, 130);
        Loaded += (_, _) =>
        {
            NoteWindowPlacement.Place(this, near, _workspace.Windows.Values);
            CaptureWindowState();
            _workspace.SaveDevice();
            _pointerOver = IsMouseOver;
            UpdateHeaderState();
        };
        SourceInitialized += (_, _) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(TrackNativeMove);
        Topmost = state.Topmost;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(false); };
        _layoutTimer.Tick += (_, _) => { _layoutTimer.Stop(); CaptureWindowState(); _workspace.SaveDevice(); };
        CommandBindings.Add(new CommandBinding(ApplicationCommands.New, (_, _) => CreateNearbyNote()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => SaveNow()));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, PasteExecuted, (_, e) => e.CanExecute = true));
        InputBindings.Add(new KeyBinding(ApplicationCommands.New, Key.N, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, Key.S, ModifierKeys.Control));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => _workspace.ShowSearch()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Find, Key.F, ModifierKeys.Control));
        // Cancel WPF's default XAML/RTF import. Only text and normalized raster images enter the document.
        DataObject.AddPastingHandler(Editor, (_, e) => { e.CancelCommand(); PasteClipboard(); });
        _loading = false;
        MouseEnter += (_, _) => { _pointerOver = true; UpdateHeaderState(); };
        MouseLeave += (_, _) => { _pointerOver = false; UpdateHeaderState(); };
        UpdateCounters();
        Deactivated += (_, _) => { if (_modalDepth == 0 && !_menuOpen) FinishEditing(); };
    }

    private static double FiniteClamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : Math.Clamp(fallback, min, max);
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    public void EnterEditMode()
    {
        if (!IsEditing) _historyBatch = Guid.NewGuid().ToString("N");
        SetValue(IsEditingProperty, true);
        Editor.IsReadOnly = false;
        Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ApplyTheme();
        Editor.Focus();
        UpdateFormatButtons();
        UpdateHeaderState();
    }

    public void FinishEditing()
    {
        if (!SaveNow(false)) return;
        SetValue(IsEditingProperty, false);
        Editor.IsReadOnly = true;
        Editor.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        ApplyTheme();
        UpdateHeaderState();
    }

    public void ReloadAfterSync(bool reloadBody, string status = "已从其他设备同步")
    {
        _loading = true;
        try
        {
            TitleBox.Text = _note.Title; _color = _note.Color; Title = _note.DisplayTitle + " · Sticky";
            if (reloadBody) { Editor.Document = DocumentCodec.Decode(_note.Blocks, _workspace.Store, darkTheme: true, _workspace.EditorPreferences); AttachImageMenus(); }
            ApplyTheme(); _dirty = false;
            SaveStatus.Text = status; SaveIndicator.Content = "✓"; UpdateCounters();
        }
        finally { _loading = false; }
    }

    private void ApplyTheme()
    {
        HeaderBar.Background = Brush(NotePalette.Accent(_color));
        ColorSwatch.Background = Brush(NotePalette.Background(_color));
        NoteSurface.BorderBrush = Brush(IsEditing ? NotePalette.Accent(_color) : "#555555");
    }

    public void ApplyEditorPreferences(EditorPreferences preferences)
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            DocumentCodec.ApplyDefaults(Editor.Document, preferences);
            _showHeaderOnHover = preferences.ShowHeaderOnHover;
            UpdateHeaderState();
        }
        finally { _loading = wasLoading; }
    }

    // IME candidate windows can temporarily take mouse hit-testing away from the note.
    // Keep the editor's geometry stable until editing ends; hover only controls reading mode.
    private void UpdateHeaderState() => SetValue(IsHeaderExpandedPropertyKey,
        IsEditing || _menuOpen || _dragging || (_showHeaderOnHover && _pointerOver));

    private void StartEditing(object sender, MouseButtonEventArgs e) { if (!IsEditing) EnterEditMode(); }
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_menuOpen) { FinishEditing(); e.Handled = true; }
        else if (e.Key == Key.F2) { EnterEditMode(); e.Handled = true; }
    }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node is not null && node != HeaderBar)
        {
            if (node is ButtonBase) return;
            node = VisualTreeHelper.GetParent(node);
        }
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _dragging = true; UpdateHeaderState();
            try { DragMove(); e.Handled = true; }
            finally { _dragging = false; _pointerOver = IsMouseOver; UpdateHeaderState(); _layoutTimer.Stop(); CaptureWindowState(); _workspace.SaveDevice(); }
        }
    }
    private void EditorSelectionChanged(object sender, RoutedEventArgs e) { if (!_loading) UpdateFormatButtons(); }
    private void UpdateFormatButtons()
    {
        BoldButton.IsChecked = Editor.Selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight && weight.ToOpenTypeWeight() >= 600;
        ItalicButton.IsChecked = Editor.Selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style && style == FontStyles.Italic;
        var decorations = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        UnderlineButton.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
        StrikeButton.IsChecked = decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
    }

    private void ContentChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || Editor is null) return;
        MarkDirty();
        UpdateCounters();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        SaveStatus.Text = "正在编辑…";
        SaveStatus.Foreground = Brush("#807D70");
        SaveIndicator.Content = "·";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void UpdateCounters()
    {
        var text = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.Trim();
        var hasImage = EnumerateImages(Editor.Document.Blocks).Any();
        Placeholder.Visibility = text.Length == 0 && !hasImage ? Visibility.Visible : Visibility.Collapsed;
        WordCount.Text = $"{text.Length} 字";
    }

    public bool SaveNow(bool showError = true)
    {
        _saveTimer.Stop();
        if (!_dirty || _loading) return true;
        var oldTitle = _note.Title;
        var oldColor = _note.Color;
        var oldBlocks = _note.Blocks;
        var oldModified = _note.ModifiedAt;
        try
        {
            _note.Blocks = DocumentCodec.Encode(Editor.Document);
            _note.Title = TitleBox.Text;
            _note.Color = _color;
            _note.ModifiedAt = DateTimeOffset.UtcNow;
            using (var history = _workspace.Store.History.WithContext("local", _historyBatch)) _workspace.Store.Save(_note);
            _dirty = false;
            SaveStatus.Text = "已保存 · " + DateTime.Now.ToString("HH:mm:ss");
            SaveStatus.Foreground = Brush("#526B4C");
            SaveIndicator.Content = "✓";
            SaveStatus.ToolTip = _workspace.Store.Root;
            Title = _note.DisplayTitle + " · Sticky";
            _workspace.NotifyChanged();
            return true;
        }
        catch (Exception ex)
        {
            _note.Title = oldTitle;
            _note.Color = oldColor;
            _note.Blocks = oldBlocks;
            _note.ModifiedAt = oldModified;
            SaveStatus.Text = "保存失败 · Ctrl+S 重试";
            SaveStatus.Foreground = Brush("#AF343C");
            SaveStatus.ToolTip = ex.Message;
            SaveIndicator.Content = "⚠";
            if (showError) StickyWorkspace.ShowError("保存失败，编辑内容仍保留在窗口中", ex);
            return false;
        }
    }

    public void CaptureWindowState()
    {
        var bounds = WindowState == System.Windows.WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _state.Left = bounds.Left;
        _state.Top = bounds.Top;
        _state.Width = bounds.Width;
        _state.Height = bounds.Height;
        _state.Topmost = Topmost;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!SaveNow()) { e.Cancel = true; return; }
        _saveTimer.Stop();
        _layoutTimer.Stop();
        CaptureWindowState();
        _state.IsOpen = _workspace.IsExiting && _note.DeletedAt is null;
        if (!_workspace.IsExiting) _workspace.SaveDevice();
        _workspace.Windows.Remove(_note.Id);
        _workspace.NotifyChanged();
    }

    private void LayoutChanged(object? sender, EventArgs e)
    {
        if (_loading || _workspace.IsExiting || _dragging || _nativeMoving) return;
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }
    private nint TrackNativeMove(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0231) { _nativeMoving = true; _layoutTimer.Stop(); }
        else if (message == 0x0232)
        {
            _nativeMoving = false;
            if (!_loading && !_workspace.IsExiting) { CaptureWindowState(); _workspace.SaveDevice(); }
        }
        return 0;
    }

    private void OpenMenu(object sender, RoutedEventArgs e)
    {
        var menu = BuildNoteMenu(); menu.PlacementTarget = MoreButton; menu.IsOpen = true;
    }
    private void SetNoteColor(string key)
    {
        if (_color == key) return;
        _color = key; ApplyTheme(); MarkDirty();
    }
    private void OpenColorPalette(object sender, RoutedEventArgs e)
    {
        const double paletteWidth = 180;
        var menu = new ContextMenu { PlacementTarget = ColorButton, Placement = PlacementMode.Bottom, Width = paletteWidth,
            HorizontalOffset = ColorButton.ActualWidth - paletteWidth, VerticalOffset = 4 };
        foreach (var (label, key) in NotePalette.Choices)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = key == _color,
                Icon = new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(2),
                    Background = Brush(NotePalette.Background(key)), BorderBrush = Brush("#80908B80"), BorderThickness = new Thickness(1) } };
            item.Click += (_, _) => { SetNoteColor(key); Activate(); Editor.Focus(); };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => { _menuOpen = true; UpdateHeaderState(); };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            UpdateHeaderState();
            Dispatcher.BeginInvoke(() => { if (IsVisible && !IsActive && _modalDepth == 0) FinishEditing(); }, DispatcherPriority.Background);
        };
        menu.IsOpen = true;
    }
    private ContextMenu BuildNoteMenu()
    {
        var menu = new ContextMenu();
        void Populate()
        {
            menu.Items.Clear();
            void Add(string header, Action action)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => action(); menu.Items.Add(item);
            }
            Add("编辑便签    F2", EnterEditMode);
            Add("完成编辑    Esc", FinishEditing);
            Add("修改标题…", RenameTitle);
            var move = new MenuItem { Header = "移动到分组 · " + _workspace.GroupName(_note.GroupId) };
            NoteMenus.FillMoveItems(move.Items, _workspace, _note); menu.Items.Add(move);
            var colors = new MenuItem { Header = "便签颜色" };
            foreach (var (label, key) in NotePalette.Choices)
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = key == _color };
                item.Click += (_, _) => SetNoteColor(key); colors.Items.Add(item);
            }
            menu.Items.Add(colors);
            var fontSize = new MenuItem { Header = "字号" };
            foreach (var size in new[] { 14, 16, 18, 22, 28 })
            {
                var item = new MenuItem { Header = size.ToString() };
                item.Click += (_, _) => { EnterEditMode(); Editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, (double)size); }; fontSize.Items.Add(item);
            }
            menu.Items.Add(fontSize);
            var foreground = new MenuItem { Header = "文字颜色" };
            foreach (var (label, color) in new[] { ("默认", "#ECECEC"), ("红色", "#FF9EAD"), ("绿色", "#ADE0B0"), ("蓝色", "#9FCBFF") })
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => { EnterEditMode(); Editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Brush(color)); }; foreground.Items.Add(item);
            }
            menu.Items.Add(foreground);
            var pin = new MenuItem { Header = "始终置顶", IsCheckable = true, IsChecked = Topmost };
            pin.Click += (_, _) => { Topmost = !Topmost; LayoutChanged(this, EventArgs.Empty); }; menu.Items.Add(pin);
            menu.Items.Add(new Separator());
            Add("便签管理", _workspace.ShowManager);
            Add("搜索便签    Ctrl+F", _workspace.ShowSearch);
            Add("历史版本与重放…", () => _workspace.ShowHistory(_note.Id));
            Add("设置…", _workspace.ShowSettings);
            Add("立即保存    Ctrl+S", () => SaveNow());
            Add("移入回收站", () => _workspace.Delete(_note));
            Add("关闭窗口（保留便签）", Close);
        }
        menu.Opened += (_, _) => { _menuOpen = true; UpdateHeaderState(); Populate(); };
        menu.Closed += (_, _) => { _menuOpen = false; UpdateHeaderState(); if (!IsActive && _modalDepth == 0) FinishEditing(); };
        Populate();
        return menu;
    }
    private void RenameTitle()
    {
        _modalDepth++;
        try
        {
            var title = TextPrompt.Show(this, "修改标题", "便签标题（留空时使用正文首行）", TitleBox.Text, 500, allowEmpty: true);
            if (title is not null) TitleBox.Text = title;
        }
        finally { _modalDepth--; }
    }
    private void CreateNearbyNote() => _workspace.CreateNote(_note.GroupId, this);
    private void NewNote(object sender, RoutedEventArgs e) => CreateNearbyNote();
    private void ManualSave(object sender, RoutedEventArgs e) => SaveNow();
    private void CloseNote(object sender, RoutedEventArgs e) => Close();

    private void ToggleStrike(object sender, RoutedEventArgs e)
    {
        var current = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var next = current?.Clone() ?? [];
        var strike = next.FirstOrDefault(d => d.Location == TextDecorationLocation.Strikethrough);
        if (strike is null) next.Add(TextDecorations.Strikethrough[0]); else next.Remove(strike);
        Editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
        Editor.Focus();
        UpdateFormatButtons();
    }

    private void InsertImage(object sender, RoutedEventArgs e)
    {
        EnterEditMode();
        var dialog = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff", Multiselect = true };
        _modalDepth++;
        try { if (dialog.ShowDialog(this) == true) InsertImageFiles(dialog.FileNames); }
        finally { _modalDepth--; }
    }

    private void InsertImageFiles(IEnumerable<string> files)
    {
        try
        {
            foreach (var file in files)
            {
                if (new FileInfo(file).Length > 20 * 1024 * 1024) throw new InvalidDataException("单张图片不能超过 20 MB。");
                using var stream = File.OpenRead(file);
                InsertBitmap(DocumentCodec.LoadBitmap(stream));
            }
        }
        catch (Exception ex) { StickyWorkspace.ShowError("无法插入图片", ex); }
    }

    private void InsertBitmap(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        EnterEditMode();
        var id = DocumentCodec.StoreBitmap(bitmap, _workspace.Store);
        var image = DocumentCodec.CreateImage(id, Math.Min(280, bitmap.PixelWidth), _workspace.Store);
        Editor.BeginChange();
        try
        {
            Editor.Selection.Text = "";
            var position = Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            var container = new InlineUIContainer(image, position) { BaselineAlignment = BaselineAlignment.Center };
            Editor.CaretPosition = container.ElementEnd.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally { Editor.EndChange(); }
        AttachImageMenu(image);
        MarkDirty();
        UpdateCounters();
        Editor.Focus();
    }

    private void PasteExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        // Preserve ordinary paste in the title TextBox.
        if (TitleBox.IsKeyboardFocusWithin) { TitleBox.Paste(); e.Handled = true; return; }
        PasteClipboard();
        e.Handled = true;
    }
    private void PasteClipboard()
    {
        EnterEditMode();
        try
        {
            if (Clipboard.ContainsFileDropList()) InsertImageFiles(Clipboard.GetFileDropList().Cast<string>());
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap) InsertBitmap(bitmap);
            else if (Clipboard.ContainsText())
            {
                Editor.BeginChange();
                try { Editor.Selection.Text = Clipboard.GetText(); Editor.CaretPosition = Editor.Selection.End; }
                finally { Editor.EndChange(); }
                Editor.Focus();
            }
        }
        catch (Exception ex) when (ex is ExternalException or IOException or ArgumentException or InvalidOperationException or NotSupportedException)
        { StickyWorkspace.ShowError("粘贴失败", ex); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor), true);
            if (position is not null) Editor.CaretPosition = position;
            InsertImageFiles(files);
        }
        e.Handled = true;
    }

    private static IEnumerable<WpfImage> EnumerateImages(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
                foreach (var image in ImagesIn(p.Inlines)) yield return image;
            if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var image in EnumerateImages(item.Blocks)) yield return image;
            if (block is Section section)
                foreach (var image in EnumerateImages(section.Blocks)) yield return image;
        }
    }
    private static IEnumerable<WpfImage> ImagesIn(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is InlineUIContainer { Child: WpfImage image }) yield return image;
            if (inline is Span span) foreach (var child in ImagesIn(span.Inlines)) yield return child;
        }
    }
    private void AttachImageMenus() { foreach (var image in EnumerateImages(Editor.Document.Blocks)) AttachImageMenu(image); }
    private void AttachImageMenu(WpfImage image)
    {
        var menu = new ContextMenu();
        foreach (var (label, width) in new[] { ("小图 · 160", 160), ("中图 · 280", 280), ("大图 · 480", 480) })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => { image.Width = width; MarkDirty(); };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "移除图片" };
        remove.Click += (_, _) =>
        {
            if (image.Parent is InlineUIContainer container)
            {
                Editor.BeginChange();
                try { new TextRange(container.ElementStart, container.ElementEnd).Text = ""; } finally { Editor.EndChange(); }
                MarkDirty(); UpdateCounters();
            }
        };
        menu.Items.Add(remove);
        menu.Opened += (_, _) => { _menuOpen = true; UpdateHeaderState(); };
        menu.Closed += (_, _) => { _menuOpen = false; UpdateHeaderState(); if (!IsActive && _modalDepth == 0) FinishEditing(); };
        image.ContextMenu = menu;
    }
}
