using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Sticky.App.Controls;
using Sticky.Core;

namespace Sticky.App;

public partial class NoteWindow
{
    private IReadOnlyList<NoteTag> _footerTags = [];
    private double _footerWidth = -1;

    private void InitializeTags()
    {
        if (_workspace.IsVaultNote(_note.Id))
        {
            TagStrip.Visibility = Visibility.Collapsed;
            FooterBar.SetBinding(VisibilityProperty, new Binding(nameof(IsEditing)) { Source = this, Converter = (IValueConverter)FindResource("BoolVisibility") });
            return;
        }
        Loaded += (_, _) => RefreshTagStrip();
        _workspace.Changed += RefreshTagStrip;
        Closed += (_, _) => _workspace.Changed -= RefreshTagStrip;
    }

    private void TagStripSizeChanged(object sender, SizeChangedEventArgs e) => RefreshTagStrip();
    private void TagStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == TagStrip) EditTags();
    }

    private void RefreshTagStrip()
    {
        if (TagStripItems is null || !IsLoaded || _workspace.IsVaultNote(_note.Id)) return;
        var width = TagStrip.ActualWidth;
        var tags = _workspace.Tags.Where(t => !t.Deleted && _note.Tags.Contains(t.Id)).OrderBy(t => t.Name, NoteTagNames.Comparer).ToList();
        if (Math.Abs(width - _footerWidth) < .1 && _footerTags.SequenceEqual(tags)) return;
        _footerTags = tags;
        _footerWidth = width;
        TagStripItems.Children.Clear();
        if (tags.Count == 0)
        {
            TagStripItems.Children.Add(FooterButton(width < 60 ? "＋" : "＋ 标签", "添加便签标签", EditTags));
            return;
        }

        var pills = tags.Select(tag => TagPills.Create(tag, activate: EditTags, compact: true)).ToList();
        foreach (var pill in pills) pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        const double overflowWidth = 24;
        var limit = pills.Sum(pill => pill.DesiredSize.Width) <= width ? width : Math.Max(0, width - overflowWidth);
        var shown = 0;
        foreach (var pill in pills)
        {
            if (pill.DesiredSize.Width > limit) break;
            TagStripItems.Children.Add(pill);
            limit -= pill.DesiredSize.Width;
            shown++;
        }
        if (shown == tags.Count) return;

        Button? overflow = null;
        overflow = FooterButton("…", "显示其余标签", () => ShowHiddenTags(overflow!, tags.Skip(shown)));
        overflow.Width = Math.Min(overflowWidth, width);
        TagStripItems.Children.Add(overflow);
    }

    private static Button FooterButton(string text, string label, Action action)
    {
        var button = TagPills.Button(text, label, action);
        button.Foreground = Brush("#E8E8E8");
        button.ToolTip = label;
        button.Height = 24;
        return button;
    }

    private void ShowHiddenTags(Button owner, IEnumerable<NoteTag> hidden)
    {
        var menu = new ContextMenu { PlacementTarget = owner, Placement = PlacementMode.Bottom };
        foreach (var tag in hidden) menu.Items.Add(new MenuItem { Header = tag.Name, Icon = TagPills.Swatch(tag), StaysOpenOnClick = true });
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "编辑标签…" };
        edit.Click += (_, _) => EditTags();
        menu.Items.Add(edit);
        menu.Opened += (_, _) => { _menuOpen = true; UpdateHeaderState(); };
        menu.Closed += (_, _) => { _menuOpen = false; UpdateHeaderState(); };
        owner.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void EditTags()
    {
        if (!_workspace.CanEditTags(_note) || !SaveNow()) return;
        _modalDepth++;
        try { new TagEditorWindow(_workspace, _note) { Owner = this, Topmost = Topmost }.ShowDialog(); }
        finally { _modalDepth--; UpdateHeaderState(); }
    }
}
