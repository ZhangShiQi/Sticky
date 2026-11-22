using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sticky.App.Editor;
using Sticky.Core;

namespace Sticky.App;

public partial class NoteWindow
{
    private ImageViewerWindow? _imageViewer;
    private readonly Dictionary<string, BitmapSource> _markdownBitmaps = [];

    private void FitBodyImages()
    {
        var width = Editor.ViewportWidth;
        if (!double.IsFinite(width) || width <= 0) return;
        foreach (var image in EnumerateImages(Editor.Document.Blocks))
            FitBodyImage(image, width);
    }

    private static void FitBodyImage(Image image, double viewportWidth)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0) return;
        // Limit the displayed width while retaining Image.Width for formatting and persistence.
        for (var parent = image.Parent as FrameworkContentElement; parent is not null; parent = parent.Parent as FrameworkContentElement)
        {
            if (parent is not Block block) continue;
            viewportWidth -= HorizontalSpace(block.Margin) + HorizontalSpace(block.Padding) + HorizontalSpace(block.BorderThickness);
            if (block is Paragraph paragraph && paragraph.TextIndent > 0) viewportWidth -= paragraph.TextIndent;
        }
        image.MaxWidth = Math.Clamp(viewportWidth, 1, 800);

        static double HorizontalSpace(Thickness thickness) =>
            (double.IsNaN(thickness.Left) ? 0 : thickness.Left) + (double.IsNaN(thickness.Right) ? 0 : thickness.Right);
    }

    private void StartEditing(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != sender; node = ParentOf(node))
        {
            if (node is not Image image) continue;
            if (e.ClickCount == 2)
            {
                e.Handled = true; OpenImageViewer(image); return;
            }
            // A single click on an image in reading mode must not start an edit session.
            if (!IsEditing || _note.IsMarkdown) e.Handled = true;
            return;
        }
        if (!IsEditing) EnterEditMode();
    }
    private Image CreateMarkdownImage(MarkdownReplacement replacement)
    {
        if (_markdownBitmaps.Count >= 16 && !_markdownBitmaps.ContainsKey(replacement.ImageId!)) _markdownBitmaps.Clear();
        var image = DocumentCodec.CreateImage(replacement.ImageId!, replacement.ImageWidth, _workspace.StoreFor(_note), _markdownBitmaps);
        if (image.Source is not null) image.ToolTip = "双击放大查看";
        var menu = new ContextMenu();
        var view = new MenuItem { Header = "查看图片" };
        view.Click += (_, _) => OpenImageViewer(image);
        var edit = new MenuItem { Header = "编辑图片引用" };
        edit.Click += (_, _) =>
        {
            EnterEditMode();
            MarkdownEditor.Select(replacement.Start + 1, 0);
        };
        menu.Items.Add(view);
        menu.Items.Add(edit);
        menu.Opened += (_, _) => { _menuOpen = true; UpdateHeaderState(); };
        menu.Closed += (_, _) => { _menuOpen = false; UpdateHeaderState(); };
        image.ContextMenu = menu;
        return image;
    }
    private static DependencyObject? ParentOf(DependencyObject node) => node is FrameworkContentElement content
        ? content.Parent : node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    private void OpenImageViewer(Image image)
    {
        if (image.Tag is not string id) return;
        try
        {
            var viewer = new ImageViewerWindow(_workspace.StoreFor(_note), id) { Owner = this, Topmost = Topmost };
            if (_workspace.IsVaultNote(_note.Id))
            {
                viewer.PreviewKeyDown += (_, _) => _workspace.TouchVault();
                viewer.PreviewMouseDown += (_, _) => _workspace.TouchVault();
                viewer.PreviewMouseWheel += (_, _) => _workspace.TouchVault();
            }
            _imageViewer?.Close(); _imageViewer = viewer;
            viewer.Closed += (_, _) => { if (ReferenceEquals(_imageViewer, viewer)) _imageViewer = null; };
            viewer.Show(); viewer.Activate();
        }
        catch (Exception ex) { StickyWorkspace.ShowError("无法查看图片，便签内容已保留", ex); }
    }
}
