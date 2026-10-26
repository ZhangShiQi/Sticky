using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sticky.App.Controls;
using Sticky.App.Editor;
using Sticky.App.ViewModels;
using Sticky.Core;
using Sticky.Storage;

namespace Sticky.App;

public partial class HistoryWindow : Window
{
    private readonly StickyWorkspace _workspace;
    private readonly DispatcherTimer _timer = new();
    private HistoryTimeline? _timeline;
    private List<HistoryRow> _rows = [];
    private List<HistoryNoteChoice> _choices = [];
    private HistoryFrame? _frame;
    private Note? _snapshot;
    private Guid? _detailNote;
    private bool _loading = true, _playing, _initialized;
    public int FrameIndex => (int)ProgressSlider.Value;
    public long PreviewSequence => _frame?.Sequence ?? 0;
    public bool IsPlaying => _playing;
    public IReadOnlySet<Guid> SelectedNoteIds => _choices.Where(n => n.IsSelected).Select(n => n.Id).ToHashSet();

    public sealed class HistoryNoteChoice(Guid id, string title, string description, string color, bool selected) : INotifyPropertyChanged
    {
        private bool _selected = selected;
        public Guid Id { get; } = id;
        public string Title { get; } = title;
        public string Description { get; } = description;
        public string Color { get; } = color;
        public Brush Accent => Paint(Color);
        public bool IsSelected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public HistoryWindow(StickyWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        TimelineView.FrameRequested += index => { Pause(); Seek(index, true); };
        _timer.Tick += (_, _) => { var next = NextIndex(1); if (next < 0) Pause(); else { Seek(next, true); Schedule(); } };
        workspace.Store.History.Changed += HistoryChanged;
        Closed += (_, _) =>
        {
            _timer.Stop(); workspace.Store.History.Changed -= HistoryChanged;
            foreach (var choice in _choices) choice.PropertyChanged -= SelectionChanged;
        };
        _loading = false;
        RefreshTimeline();
    }

    public void SelectNote(Guid? id)
    {
        _loading = true;
        foreach (var choice in _choices) choice.IsSelected = id is null || choice.Id == id;
        _loading = false;
        _detailNote = id;
        ApplySelection();
    }
    private void SelectAll(object sender, RoutedEventArgs e) => SelectNote(null);
    private void ClearSelection(object sender, RoutedEventArgs e)
    {
        _loading = true;
        foreach (var choice in _choices) choice.IsSelected = false;
        _loading = false; ApplySelection();
    }
    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    { if (!_loading) ApplySelection(); }
    private void ApplySelection()
    {
        Pause();
        TimelineView.SetData(_rows, _choices.Where(c => c.IsSelected).Select(c => new HistoryTrack(c.Id, c.Title, c.Color)).ToList());
        SelectionCount.Text = $"已选 {SelectedNoteIds.Count} / {_choices.Count} 张";
        Render();
    }
    private void HistoryChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(HistoryChanged); return; }
        StatusText.Text = "有新记录，点击「刷新记录」查看。当前重放保持在打开时的历史视图。";
    }
    private void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (!_workspace.SaveAllNotes()) return;
        RefreshTimeline();
    }
    private void RefreshTimeline()
    {
        Pause();
        var selected = SelectedNoteIds;
        var sequence = PreviewSequence;
        var wasAtEnd = !_initialized || FrameIndex == _rows.Count - 1;
        try
        {
            _workspace.Store.History.Reconcile();
            var timeline = _workspace.Store.History.ReadTimeline();
            var last = timeline.Frame(timeline.Events.LastOrDefault()?.Sequence ?? 0);
            var baseline = timeline.Frame(0);
            var notes = baseline.Notes.Values.Concat(last.Notes.Values).Concat(_workspace.Notes).GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.Last());
            var names = notes.ToDictionary(p => p.Key, p => p.Value.DisplayTitle);
            foreach (var entry in timeline.Events)
                foreach (var id in entry.NoteIds)
                {
                    if (!names.ContainsKey(id)) names[id] = "历史便签 " + id.ToString("N")[..6];
                    if (entry.Target.StartsWith("notes/", StringComparison.Ordinal) && entry.NoteTitle.Length > 0) names[id] = entry.NoteTitle;
                }
            foreach (var choice in _choices) choice.PropertyChanged -= SelectionChanged;
            _choices = names.OrderBy(p => p.Value).ThenBy(p => p.Key).Select(p =>
            {
                notes.TryGetValue(p.Key, out var note);
                var group = note?.GroupId is { } g ? last.Groups.FirstOrDefault(n => n.Id == g)?.Name ?? "未分组" : "未分组";
                var state = note is null ? " · 已移除" : note.DeletedAt is not null ? " · 回收站" : "";
                return new HistoryNoteChoice(p.Key, p.Value, group + state, NotePalette.Accent(note?.Color ?? "yellow"), !_initialized || selected.Contains(p.Key));
            }).ToList();
            foreach (var choice in _choices) choice.PropertyChanged += SelectionChanged;
            _timeline = timeline; _frame = null;
            _rows = timeline.UserRows();
            var initial = new HistoryEvent { Sequence = 0, Time = timeline.StartedAt, Kind = "baseline", Source = "baseline", Summary = "开始记录 · 初始状态", Changes = ["从启用历史功能时的本地内容开始记录。"], NoteIds = baseline.Notes.Keys.ToList() };
            _rows.Insert(0, new(initial, initial, 1));
            _loading = true;
            NoteList.ItemsSource = _choices;
            ProgressSlider.Maximum = _rows.Count - 1;
            ProgressSlider.Value = wasAtEnd ? _rows.Count - 1 : Math.Max(0, _rows.FindLastIndex(r => r.Sequence <= sequence));
            _loading = false; _initialized = true;
            EventCount.Text = $"{_rows.Count - 1} 个变化批次 · 全部便签";
            ApplySelection();
            StatusText.Text = "本地历史 · 回放不会修改当前便签 · 窗口位置、大小、置顶及打开关闭不在时间轴中展示";
            if (timeline.Events.Count == 0) StatusText.Text += " · 从此刻开始积累变化";
            if (_workspace.Store.History.Warnings.Count > 0) StatusText.Text += "\n" + string.Join("\n", _workspace.Store.History.Warnings.Distinct());
        }
        catch (Exception ex) { _loading = false; StatusText.Text = "无法读取历史：" + ex.Message + " 当前便签未被修改。"; }
    }
    private void ProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!_loading) { Pause(); Render(follow: true); } }
    private void Seek(int index, bool follow)
    {
        _loading = true; ProgressSlider.Value = Math.Clamp(index, 0, Math.Max(0, _rows.Count - 1)); _loading = false;
        Render(follow);
    }
    private static SolidColorBrush Paint(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private void Render(bool follow = false)
    {
        if (_timeline is null || _rows.Count == 0) return;
        _snapshot = null; RestoreCopyButton.IsEnabled = RestoreCurrentButton.IsEnabled = false;
        try
        {
            var row = _rows[FrameIndex];
            if (_frame?.Sequence != row.Sequence) _frame = _timeline.Frame(row.Sequence);
            TimelineView.SetIndex(FrameIndex, follow);
            DrawSnapshots(_frame);
            var selected = SelectedNoteIds;
            var candidate = row.Last.NoteIds.FirstOrDefault(id => selected.Contains(id));
            if (candidate == Guid.Empty) candidate = _detailNote is { } detail && selected.Contains(detail) ? detail : selected.FirstOrDefault();
            ShowDetail(candidate == Guid.Empty ? null : candidate);
            CurrentTime.Text = row.Last.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            PlaybackStatus.Text = $"{FrameIndex} / {_rows.Count - 1} · " + (selected.Count > 0 ? $"回看 {selected.Count} 张便签" : "全局浏览");
            PlayButton.IsEnabled = NextIndex(1) >= 0 || PlaybackIndices().Any(i => i > 0);
        }
        catch (Exception ex)
        {
            _loading = false; Pause(); _frame = null; _snapshot = null;
            RestoreCopyButton.IsEnabled = RestoreCurrentButton.IsEnabled = false;
            SnapshotPanel.Children.Clear(); BeforePreview.Document = new(); AfterPreview.Document = new();
            StatusText.Text = "历史预览未完成：" + ex.Message + " 当前数据未被修改。";
        }
    }
    private void DrawSnapshots(HistoryFrame frame)
    {
        SnapshotPanel.Children.Clear();
        var selected = _choices.Where(c => c.IsSelected).ToList();
        PreviewEmpty.Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var choice in selected)
        {
            var note = frame.Notes.GetValueOrDefault(choice.Id);
            var active = _rows[FrameIndex].Last.NoteIds.Contains(choice.Id);
            var card = new Border { Background = Paint("#333333"), BorderBrush = Paint(active ? "#315448" : "#CCD3C6"), BorderThickness = new Thickness(active ? 2 : 1),
                CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 10), Tag = choice.Id };
            var content = new DockPanel();
            var header = new Button { Content = new TextBlock { Text = note?.DisplayTitle ?? choice.Title, TextTrimming = TextTrimming.CharacterEllipsis }, Background = Paint(choice.Color),
                Foreground = Paint("#293D31"), Margin = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = "查看这张便签的变化与恢复选项" };
            header.Click += (_, _) =>
            {
                try { _detailNote = choice.Id; ShowDetail(choice.Id); PreviewTabs.SelectedIndex = 1; }
                catch (Exception ex) { _snapshot = null; RestoreCopyButton.IsEnabled = RestoreCurrentButton.IsEnabled = false; StatusText.Text = "无法读取该版本：" + ex.Message; }
            };
            DockPanel.SetDock(header, Dock.Top); content.Children.Add(header);
            var group = note?.GroupId is { } id ? frame.Groups.FirstOrDefault(g => g.Id == id)?.Name ?? "未分组" : "未分组";
            var footer = new TextBlock { Text = note is null ? "此时尚未创建，或已被移除" : group + (note.DeletedAt is not null ? " · 已移入回收站" : ""),
                Foreground = Paint("#BAC2B5"), FontSize = 11, Margin = new Thickness(10, 6, 10, 10), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(footer, Dock.Bottom); content.Children.Add(footer);
            var body = new RichTextBox { Document = note is null ? Preview(null) : DocumentCodec.Decode(Excerpt(note.Blocks), _workspace.Store, darkTheme: true, _workspace.EditorPreferences),
                IsReadOnly = true, IsDocumentEnabled = false, AllowDrop = false, IsHitTestVisible = false, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(8), Height = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Opacity = note?.DeletedAt is not null ? 0.6 : 1 };
            content.Children.Add(body); card.Child = content; SnapshotPanel.Children.Add(card);
            if (_playing && active && SystemParameters.ClientAreaAnimation)
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(200)));
        }
        SizeSnapshots();
    }
    private void SnapshotSizeChanged(object sender, SizeChangedEventArgs e) => SizeSnapshots();
    private void SizeSnapshots()
    {
        var width = SnapshotPanel.ActualWidth;
        if (width <= 0) return;
        var columns = Math.Max(1, (int)(width / 260));
        foreach (Border card in SnapshotPanel.Children) card.Width = Math.Max(1, width / columns - 10);
    }
    private static List<NoteBlock> Excerpt(List<NoteBlock> original)
    {
        var remaining = 1400; var blocks = 12; var images = 1;
        List<NoteBlock> Copy(List<NoteBlock> source)
        {
            var result = new List<NoteBlock>();
            foreach (var block in source)
            {
                if (remaining <= 0 || blocks-- <= 0) break;
                var next = new NoteBlock { Kind = block.Kind, Alignment = block.Alignment, Marker = block.Marker };
                foreach (var inline in block.Inlines)
                {
                    if (inline.Kind == "image") { if (images-- > 0) next.Inlines.Add(inline); continue; }
                    if (remaining <= 0) break;
                    var text = inline.Text[..Math.Min(remaining, inline.Text.Length)]; remaining -= text.Length;
                    next.Inlines.Add(new NoteInline { Text = text, Bold = inline.Bold, Italic = inline.Italic, Underline = inline.Underline, Strike = inline.Strike,
                        Foreground = inline.Foreground, FontSize = inline.FontSize, UsesDefaultFontSize = inline.UsesDefaultFontSize });
                }
                next.Children = Copy(block.Children); result.Add(next);
            }
            return result;
        }
        return Copy(original);
    }
    private void ShowDetail(Guid? id)
    {
        if (_timeline is null || _frame is null) return;
        _snapshot = null;
        RestoreCopyButton.IsEnabled = RestoreCurrentButton.IsEnabled = false;
        if (id is null)
        {
            _detailNote = null; DetailHeading.Text = "从左侧选择一张便签"; DetailTime.Text = "";
            ChangesText.Text = "主时间线保留全部便签的变化。勾选便签或点击内容卡片查看详情。";
            BeforePreview.Document = Preview(null); AfterPreview.Document = Preview(null); return;
        }
        var row = _rows.LastOrDefault(r => r.Sequence <= _frame.Sequence && r.Last.NoteIds.Contains(id.Value)) ?? _rows[0];
        var before = _timeline.Frame(Math.Max(0, row.First.Sequence - 1));
        var previous = row.Sequence == 0 ? null : before.Notes.GetValueOrDefault(id.Value);
        var current = _frame.Notes.GetValueOrDefault(id.Value);
        _snapshot = current ?? previous;
        _detailNote = id;
        DetailHeading.Text = _snapshot?.DisplayTitle ?? "此时还没有这张便签";
        DetailTime.Text = row.Last.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + HistoryDescriptions.SourceLabel(row.Last.Source);
        if (row.Last.Source == "sync" && current is not null) DetailTime.Text += " · 原内容修改于 " + current.ModifiedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        BeforePreview.Document = Preview(previous); AfterPreview.Document = Preview(current);
        var changes = previous is not null && current is not null ? HistoryDescriptions.TextDifference(previous.PlainText, current.PlainText) : default;
        ChangesText.Text = row.Heading + "\n" + row.Description + "\n\n" + string.Join("\n\n", row.First.Changes.Concat(row.Last.Changes).Distinct())
            + (changes.Removed?.Length > 0 ? "\n\n删除文字：\n" + changes.Removed : "") + (changes.Added?.Length > 0 ? "\n\n新增文字：\n" + changes.Added : "")
            + (row.Last.Reason is { } reason ? "\n\n" + reason : "");
        if (previous is not null && current is not null)
        {
            string GroupLabel(HistoryFrame f, Guid? group) => group is { } key ? f.Groups.FirstOrDefault(g => g.Id == key)?.Name ?? "未分组" : "未分组";
            if (GroupLabel(before, previous.GroupId) != GroupLabel(_frame, current.GroupId))
                ChangesText.Text += "\n\n分组：" + GroupLabel(before, previous.GroupId) + " → " + GroupLabel(_frame, current.GroupId);
        }
        RestoreCopyButton.IsEnabled = _snapshot is not null;
        RestoreCurrentButton.IsEnabled = _snapshot is not null && _workspace.Notes.Any(n => n.Id == _snapshot.Id);
    }
    private FlowDocument Preview(Note? note) => note is null ? new FlowDocument(new Paragraph(new Run("此时没有这张便签"))) { Foreground = Brushes.LightGray }
        : DocumentCodec.Decode(note.Blocks, _workspace.Store, darkTheme: true, _workspace.EditorPreferences);
    private void RestoreCopy(object sender, RoutedEventArgs e) => Restore(asCopy: true);
    private void ExportHistory(object sender, RoutedEventArgs e)
    {
        Pause(); if (!_workspace.SaveAllNotes()) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "便签与历史备份 (*.zip)|*.zip", FileName = "Sticky-history-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
        if (dialog.ShowDialog(this) != true) return;
        try { _workspace.Store.History.Export(dialog.FileName); StatusText.Text = "已导出便签与完整本地历史。恢复时请退出 Sticky，将备份完整解压到空的 Data 目录；现有数据不会被本次导出修改。"; }
        catch (Exception ex) { StatusText.Text = "导出未完成：" + ex.Message; }
    }
    private void RestoreCurrent(object sender, RoutedEventArgs e)
    {
        Pause();
        if (MessageBox.Show(this, "将用选中的历史内容替换当前便签。当前内容会保留在时间轴中，之后仍可恢复。", "恢复历史版本", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) Restore(asCopy: false);
    }
    private void Restore(bool asCopy)
    {
        Pause(); if (_snapshot is null || _frame is null) return;
        try { _workspace.RestoreHistoryNote(_snapshot, _frame.Groups, _rows[FrameIndex].Sequence, asCopy); RefreshTimeline(); StatusText.Text = asCopy ? "已恢复为新便签，原便签保持不变。" : "已恢复所选内容；本次恢复也已记入时间轴。"; }
        catch (Exception ex) { StatusText.Text = "恢复未完成：" + ex.Message; }
    }
    private IEnumerable<int> PlaybackIndices()
    {
        var selected = SelectedNoteIds;
        return Enumerable.Range(0, _rows.Count).Where(i => i == 0 || selected.Count == 0 || _rows[i].Last.NoteIds.Any(selected.Contains));
    }
    private int NextIndex(int direction) => direction > 0
        ? PlaybackIndices().FirstOrDefault(i => i > FrameIndex, -1)
        : PlaybackIndices().LastOrDefault(i => i < FrameIndex, -1);
    private void TogglePlayback(object sender, RoutedEventArgs e)
    {
        if (_playing) { Pause(); return; }
        if (!PlaybackIndices().Any(i => i > 0)) return;
        if (NextIndex(1) < 0) Seek(0, true);
        _playing = true; PlayButton.Content = "Ⅱ 暂停"; Schedule();
    }
    private void Pause()
    {
        _timer.Stop(); _playing = false;
        if (PlayButton is not null) PlayButton.Content = SelectedNoteIds.Count > 0 ? "▶ 播放所选" : "▶ 播放全部";
    }
    private void Schedule()
    {
        var next = NextIndex(1);
        if (!_playing || next < 0) { Pause(); return; }
        var speed = double.Parse((SpeedBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1", CultureInfo.InvariantCulture);
        var seconds = Math.Max(0.4, (_rows[next].Last.Time - _rows[FrameIndex].Last.Time).TotalSeconds);
        if (SkipIdle.IsChecked == true) seconds = Math.Min(2, seconds);
        _timer.Interval = TimeSpan.FromSeconds(Math.Min(TimeSpan.FromDays(20).TotalSeconds, seconds / speed)); _timer.Start();
    }
    private void SpeedChanged(object sender, RoutedEventArgs e) { if (!_loading && _playing) Schedule(); }
    private void Previous(object sender, RoutedEventArgs e) { Pause(); var next = NextIndex(-1); if (next >= 0) Seek(next, true); }
    private void Next(object sender, RoutedEventArgs e) { Pause(); var next = NextIndex(1); if (next >= 0) Seek(next, true); }
}
