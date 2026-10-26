using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Sticky.Storage;

namespace Sticky.App.Controls;

public sealed record HistoryTrack(Guid Id, string Title, string Color);

/// <summary>One scroll surface, day axis and cursor shared by the workspace and selected notes.</summary>
public sealed class HistoryTimelineView : Grid
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Canvas _labels = new();
    private readonly Border _labelViewport = new()
    {
        Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush("#F2F4EE"),
        BorderBrush = Brush("#DCE3D8"), BorderThickness = new Thickness(0, 0, 1, 0), ClipToBounds = true, IsHitTestVisible = false,
        Margin = new Thickness(0, 0, 0, SystemParameters.HorizontalScrollBarHeight)
    };
    private readonly Line _cursor = new() { Stroke = Brush("#56715C"), StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Dictionary<int, double> _positions = [];
    private readonly List<(TextBlock Tick, int Index)> _ticks = [];
    private readonly List<(Button Button, int Index, Brush Accent, bool Workspace)> _nodes = [];
    private IReadOnlyList<HistoryRow> _rows = [];
    private IReadOnlyList<HistoryTrack> _tracks = [];
    private int _index;
    private double _layoutWidth;
    public event Action<int>? FrameRequested;
    public int TrackCount => _tracks.Count;
    public int WorkspaceNodeCount => _nodes.Count(n => n.Workspace);
    public int VisibleEventCount => _rows.Count;

    public HistoryTimelineView()
    {
        _scroll.Content = _canvas; Children.Add(_scroll);
        _labelViewport.Child = _labels; Children.Add(_labelViewport);
        _scroll.ScrollChanged += (_, _) => _labels.RenderTransform = new TranslateTransform(0, -_scroll.VerticalOffset);
        _scroll.SizeChanged += (_, _) => { if (Math.Abs(_scroll.ActualWidth - _layoutWidth) > 1) Draw(); };
    }
    public void SetData(IReadOnlyList<HistoryRow> rows, IReadOnlyList<HistoryTrack> tracks)
    { _rows = rows; _tracks = tracks; Draw(); }
    public void SetIndex(int index, bool follow)
    {
        _index = index;
        foreach (var (button, i, accent, _) in _nodes)
        {
            button.Background = i == index ? Brush("#315448") : accent;
            button.Foreground = i == index ? Brushes.White : Brush("#293D31");
            button.Opacity = i > index ? 0.5 : 1;
        }
        foreach (var (tick, i) in _ticks)
        {
            tick.Foreground = Brush(i == index ? "#315448" : "#727D70");
            tick.FontWeight = i == index ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (!_positions.TryGetValue(index, out var x)) return;
        _cursor.X1 = _cursor.X2 = x;
        if (follow && (x < _scroll.HorizontalOffset + 140 || x > _scroll.HorizontalOffset + _scroll.ViewportWidth - 40))
            _scroll.ScrollToHorizontalOffset(Math.Max(0, x - Math.Max(140, _scroll.ViewportWidth / 2)));
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private void Put(UIElement element, double x, double y)
    { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); _canvas.Children.Add(element); }
    private void Label(string text, double x, double y, double width, bool strong = false)
    { Put(new TextBlock { Text = text, Width = width, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brush(strong ? "#293D31" : "#727D70"), FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = text }, x, y); }
    private void TrackLabel(string text, double y)
    {
        var label = new TextBlock { Text = text, Width = 118, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = Brush("#293D31"), FontWeight = FontWeights.SemiBold };
        Canvas.SetLeft(label, 6); Canvas.SetTop(label, y); _labels.Children.Add(label);
    }
    private void Draw()
    {
        _layoutWidth = _scroll.ActualWidth;
        var offset = _scroll.HorizontalOffset;
        _canvas.Children.Clear(); _labels.Children.Clear(); _positions.Clear(); _nodes.Clear(); _ticks.Clear();
        const double labelWidth = 130, rowHeight = 48;
        // Persistence order remains authoritative if the machine clock moves backwards.
        var buckets = new List<List<int>>();
        for (var i = 0; i < _rows.Count; i++)
        {
            if (i == 0 || _rows[i].Last.Time.LocalDateTime.Date != _rows[i - 1].Last.Time.LocalDateTime.Date) buckets.Add([]);
            buckets[^1].Add(i);
        }
        var widths = buckets.Select(b => Math.Max(160d, b.Count * 88d)).ToArray();
        var extra = Math.Max(0, _layoutWidth - labelWidth - 28 - widths.Sum());
        if (widths.Length > 0) for (var i = 0; i < widths.Length; i++) widths[i] += extra / widths.Length;
        _canvas.Width = Math.Max(1, labelWidth + widths.Sum() + 12);
        _canvas.Height = 120 + _tracks.Count * rowHeight + (_tracks.Count == 0 ? 50 : 0);
        _labels.Height = _canvas.Height;
        _cursor.Y1 = 43; _cursor.Y2 = _canvas.Height - 8; _canvas.Children.Add(_cursor);
        TrackLabel("工作空间", 70);
        for (var t = 0; t < _tracks.Count; t++) TrackLabel(_tracks[t].Title, 130 + t * rowHeight);
        var left = labelWidth;
        for (var d = 0; d < buckets.Count; d++)
        {
            var bucket = buckets[d]; var width = widths[d];
            Label(_rows[bucket[0]].Last.Time.ToLocalTime().ToString("yyyy-MM-dd"), left + 8, 0, width - 16);
            Put(new Rectangle { Width = 1, Height = _canvas.Height - 43, Fill = Brush("#DCE3D8"), IsHitTestVisible = false }, left, 43);
            Put(new Rectangle { Width = width, Height = 1, Fill = Brush("#DCE3D8"), IsHitTestVisible = false }, left, 78);
            for (var j = 0; j < bucket.Count; j++) _positions[bucket[j]] = left + width * (j + 0.5) / bucket.Count;
            foreach (var i in bucket)
            {
                var tick = new TextBlock { Text = _rows[i].Last.Time.ToLocalTime().ToString("HH:mm:ss"), FontSize = 11, Width = 80, TextAlignment = TextAlignment.Center,
                    ToolTip = _rows[i].Last.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + _rows[i].Last.Summary };
                AutomationProperties.SetAutomationId(tick, "HistoryTimeTick-" + _rows[i].Sequence);
                Put(tick, _positions[i] - 40, 22); _ticks.Add((tick, i));
                AddNode(i, null, 51);
            }
            for (var t = 0; t < _tracks.Count; t++)
            {
                var track = _tracks[t]; var y = 120 + t * rowHeight;
                Put(new Rectangle { Width = width, Height = 1, Fill = Brush("#DCE3D8"), IsHitTestVisible = false }, left, y + 18);
                foreach (var i in bucket.Where(i => _rows[i].Last.NoteIds.Contains(track.Id)))
                {
                    AddNode(i, track, y);
                }
            }
            left += width;
        }
        if (_tracks.Count == 0) Label("在左侧勾选便签，将它们的轨道展开在这里。", labelWidth + 6, 128, Math.Max(350, _layoutWidth - labelWidth - 20));
        SetIndex(_index, false); _scroll.ScrollToHorizontalOffset(offset);
    }
    private void AddNode(int index, HistoryTrack? track, double top)
    {
        var row = _rows[index]; var workspace = track is null;
        var title = row.Last.NoteTitle;
        if (string.IsNullOrWhiteSpace(title)) title = row.Last.Kind == "baseline" ? "开始记录" : row.Last.Summary;
        var content = new StackPanel();
        content.Children.Add(HistoryActionIcons.Create(row.Last.Kind));
        if (workspace) content.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, Width = 76, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
        var node = new Button { Content = content, Width = workspace ? 84 : 36, Height = workspace ? 54 : 36,
            Padding = new Thickness(2), Margin = new Thickness(0), FontSize = 12,
            ToolTip = ActionLabel(row.Last.Kind) + " · " + (workspace ? title : track!.Title) + "\n" + row.Description + "\n" + string.Join("\n", row.Last.Changes) };
        node.Click += (_, _) => FrameRequested?.Invoke(index);
        AutomationProperties.SetName(node, (workspace ? "工作空间 " + title : track!.Title) + " " + row.Description + " " + ActionLabel(row.Last.Kind));
        AutomationProperties.SetAutomationId(node, workspace ? "HistoryWorkspaceNode-" + row.Sequence : "HistoryNoteNode-" + track!.Id + "-" + row.Sequence);
        Put(node, _positions[index] - node.Width / 2, top);
        _nodes.Add((node, index, Brush(workspace ? "#E3EADC" : track!.Color), workspace));
    }
    public static string ActionLabel(string kind) => kind switch
    { "baseline" => "初始状态", "noteCreated" => "新建", "noteDeleted" or "noteRemoved" or "externalRemoved" => "删除", "noteRestored" => "恢复", "noteMoved" => "移动分组", "historyRestored" => "恢复历史", "groupsChanged" => "分组变更", _ => "修改" };
}
