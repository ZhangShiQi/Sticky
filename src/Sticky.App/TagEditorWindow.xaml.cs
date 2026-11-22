using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Sticky.App.Controls;
using Sticky.Core;

namespace Sticky.App;

public partial class TagEditorWindow : Window
{
    private readonly StickyWorkspace _workspace;
    private readonly Note _note;
    private readonly List<NoteTag> _choices;
    private readonly HashSet<Guid> _original;
    private readonly HashSet<Guid> _selected;

    public TagEditorWindow(StickyWorkspace workspace, Note note)
    {
        _workspace = workspace;
        _note = note;
        _choices = workspace.Tags.Where(t => !t.Deleted).ToList();
        _original = note.Tags.Intersect(_choices.Select(t => t.Id)).ToHashSet();
        _selected = _original.ToHashSet();
        InitializeComponent();
        RefreshPills();
        Loaded += (_, _) => TagFilterBox.Focus();
    }

    private void RefreshPills()
    {
        AppliedTags.Children.Clear();
        AvailableTags.Children.Clear();
        var query = NoteTagNames.Normalize(TagFilterBox.Text);
        foreach (var tag in _choices.OrderBy(t => t.Name, NoteTagNames.Comparer))
            if (_selected.Contains(tag.Id)) AppliedTags.Children.Add(TagPills.Create(tag, remove: () => ChangeTag(tag.Id, false)));
            else if (tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                AvailableTags.Children.Add(TagPills.Create(tag, activate: () => ChangeTag(tag.Id, true)));
    }

    private void ChangeTag(Guid id, bool selected)
    {
        if (selected) _selected.Add(id); else _selected.Remove(id);
        RefreshPills();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (TagFilterBox is not null) RefreshPills();
    }

    private void FilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var name = NoteTagNames.Normalize(TagFilterBox.Text);
        if (name.Length == 0) return;
        if (!NoteTagNames.IsValid(name))
        {
            TagEditStatus.Text = "标签名称须为 1–48 字。";
            return;
        }
        var tag = _choices.FirstOrDefault(t => NoteTagNames.Comparer.Equals(t.Name, name));
        if (tag is null) { tag = new NoteTag(Guid.NewGuid(), name); _choices.Add(tag); }
        ChangeTag(tag.Id, true);
        TagFilterBox.Clear();
        TagEditStatus.Text = "输入名称筛选，按回车添加。关闭时自动保存。";
    }

    private void SaveOnClose(object? sender, CancelEventArgs e)
    {
        if (_selected.SetEquals(_original) || !_workspace.CanEditTags(_note)) return;
        try { _workspace.SaveNoteTags(_note, _original, _choices.Where(t => _selected.Contains(t.Id)).ToList()); }
        catch (Exception ex)
        {
            e.Cancel = true;
            TagEditStatus.Text = "标签保存失败，编辑已保留。再次关闭可重试。" + Environment.NewLine + ex.Message;
        }
    }
}
