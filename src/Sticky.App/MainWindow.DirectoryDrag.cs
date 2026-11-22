using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sticky.App.ViewModels;
using Sticky.Core;

namespace Sticky.App;

public partial class MainWindow
{
    private const string NoteDragFormat = "Sticky.NoteId";
    private (FrameworkElement Source, NoteCard Card, Point Start)? _noteDragCandidate;
    private Note? _draggedNote;
    private Button? _directoryDropTarget;
    private DispatcherTimer? _expandDropTarget;

    private void DirectoryTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is DirectoryItem item && (_viewModel.SelectedDirectoryId != item.Directory.Id || _viewModel.ShowDeleted || _viewModel.IsVaultSelected))
            _viewModel.SelectDirectory(item.Directory.Id);
    }

    private void NoteDragStart(object sender, MouseButtonEventArgs e)
    {
        _noteDragCandidate = null;
        if (e.ClickCount != 1 || sender is not FrameworkElement { DataContext: NoteCard card } source ||
            card.Note.DeletedAt is not null || _workspace.IsVaultNote(card.Note.Id)) return;

        // In preview mode the action buttons keep their own click behavior.
        for (var child = e.OriginalSource as DependencyObject; child is not null && child != source; child = ParentOf(child))
            if (child is Button) return;
        _noteDragCandidate = (source, card, e.GetPosition(this));
    }

    private static DependencyObject? ParentOf(DependencyObject child) => child is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(child) : child is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(child);

    private void NoteDragEnd(object sender, MouseButtonEventArgs e) => _noteDragCandidate = null;

    private void NoteDragMove(object sender, MouseEventArgs e)
    {
        if (_noteDragCandidate is not { } candidate) return;
        if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(candidate.Source.DataContext, candidate.Card))
        {
            _noteDragCandidate = null;
            return;
        }
        var delta = e.GetPosition(this) - candidate.Start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _noteDragCandidate = null;
        _draggedNote = candidate.Card.Note;
        candidate.Source.ReleaseMouseCapture();
        e.Handled = true;
        try { DragDrop.DoDragDrop(candidate.Source, new DataObject(NoteDragFormat, _draggedNote.Id), DragDropEffects.Move); }
        finally { _draggedNote = null; SetDirectoryDropTarget(null); }
    }

    private bool CanAcceptNoteDrag(DragEventArgs e) => _draggedNote is { DeletedAt: null } note &&
        !_workspace.IsVaultNote(note.Id) && e.Data.GetDataPresent(NoteDragFormat, autoConvert: false);

    private void DirectoryDragOver(object sender, DragEventArgs e)
    {
        var canDrop = CanAcceptNoteDrag(e);
        e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        SetDirectoryDropTarget(canDrop ? sender as Button : null);
        e.Handled = true;
    }

    private void DirectoryDragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _directoryDropTarget)) SetDirectoryDropTarget(null);
    }

    private void DirectoryDrop(object sender, DragEventArgs e)
    {
        SetDirectoryDropTarget(null);
        e.Effects = DragDropEffects.None;
        if (sender is Button { DataContext: DirectoryItem item } && CanAcceptNoteDrag(e))
        {
            if (_workspace.MoveNote(_draggedNote!, item.Directory.Id)) e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private void SetDirectoryDropTarget(Button? button)
    {
        if (ReferenceEquals(button, _directoryDropTarget)) return;
        _expandDropTarget?.Stop();
        _directoryDropTarget?.ClearValue(Control.BorderBrushProperty);
        _directoryDropTarget = button;
        if (button is null) return;
        button.BorderBrush = Brushes.SeaGreen;
        if (button.DataContext is not DirectoryItem { IsExpanded: false, Children.Count: > 0 }) return;
        if (_expandDropTarget is null)
        {
            _expandDropTarget = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
            _expandDropTarget.Tick += (_, _) =>
            {
                _expandDropTarget.Stop();
                if (_directoryDropTarget?.DataContext is DirectoryItem item) item.IsExpanded = true;
            };
        }
        _expandDropTarget.Start();
    }
}
