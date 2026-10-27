using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Sticky.App;

public partial class NoteWindow
{
    private ImageViewerWindow? _imageViewer;
    private void StartEditing(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != Editor; node = ParentOf(node))
        {
            if (node is not Image image) continue;
            if (e.ClickCount == 2)
            {
                e.Handled = true; OpenImageViewer(image); return;
            }
            // A single click on an image in reading mode must not start an edit session.
            if (!IsEditing) e.Handled = true;
            return;
        }
        if (!IsEditing) EnterEditMode();
    }
    private static DependencyObject? ParentOf(DependencyObject node) => node is FrameworkContentElement content
        ? content.Parent : node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private void OpenImageViewer(Image image)
    {
        if (image.Tag is not string id) return;
        try
        {
            var viewer = new ImageViewerWindow(_workspace.Store, id) { Owner = this, Topmost = Topmost };
            _imageViewer?.Close(); _imageViewer = viewer;
            viewer.Closed += (_, _) => { if (ReferenceEquals(_imageViewer, viewer)) _imageViewer = null; };
            viewer.Show(); viewer.Activate();
        }
        catch (Exception ex) { StickyWorkspace.ShowError("无法查看图片，便签内容已保留", ex); }
    }
}
