using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Sticky.App;
using Sticky.Core;

internal static partial class Program
{
    private static void NoteOverlayScrollBar()
    {
        var workspace = new StickyWorkspace(Path.Combine(_root, "note-overlay-scrollbar"));
        var lines = Enumerable.Range(1, 80).Select(i => $"第 {i} 行，检查浮动滚动条与正文换行是否各自独立。").ToArray();
        var rich = new Note { Blocks = lines.Select(line => new NoteBlock { Inlines = [new NoteInline { Text = line }] }).ToList() };
        var markdown = new Note();
        markdown.SetMarkdown(string.Join("\n\n", lines));
        try
        {
            foreach (var note in new[] { rich, markdown })
            {
                workspace.Store.Save(note);
                workspace.Notes.Add(note);
                workspace.OpenNote(note);
                var window = workspace.Windows[note.Id];
                var body = (FrameworkElement)window.FindName(note.IsMarkdown ? "MarkdownEditor" : "Editor");
                var scrollBar = (ScrollBar)window.FindName("BodyScrollBar");
                Pump(50);
                var viewer = FindVisual<ScrollViewer>(body).First();
                var widthAt100 = viewer.ViewportWidth;
                Check(viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden && scrollBar.Visibility == Visibility.Hidden,
                    "The native scrollbar reserved space or the overlay appeared before scrolling.");

                SendWheel(viewer);
                Pump(40);
                Check(viewer.VerticalOffset > 0 && scrollBar.IsVisible && scrollBar.Value == viewer.VerticalOffset,
                    "Mouse wheel did not scroll the body and reveal the overlay at the current offset.");
                Check(viewer.ViewportWidth == widthAt100, "Revealing the overlay changed the text viewport width.");
                window.UpdateLayout();
                var track = (Track)scrollBar.Template.FindName("PART_Track", scrollBar);
                var thumb = track.Thumb;
                var indicator = (Border)thumb.Template.FindName("Indicator", thumb);
                var indicatorWidth = indicator.TransformToAncestor(window).TransformBounds(new Rect(indicator.RenderSize)).Width;
                Check(indicatorWidth == 4 && thumb.ActualHeight > 0, "The floating thumb was missing or wider than four pixels.");

                var beforeDrag = viewer.VerticalOffset;
                thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                thumb.RaiseEvent(new DragDeltaEventArgs(0, 24) { RoutedEvent = Thumb.DragDeltaEvent });
                thumb.RaiseEvent(new DragCompletedEventArgs(0, 24, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Pump(40);
                Check(viewer.VerticalOffset > beforeDrag, "Dragging the overlay did not move the document.");
                var beforeWheelOverThumb = viewer.VerticalOffset;
                SendWheel(thumb);
                Pump(40);
                Check(viewer.VerticalOffset > beforeWheelOverThumb, "Scrolling over the thumb did not reach the document.");

                window.EnterEditMode();
                ZoomWheel(body, 120 * 40);
                Pump(50);
                SendWheel(viewer);
                Pump(40);
                var widthAt400 = viewer.ViewportWidth;
                Check(ZoomPercent(window) == 400 && widthAt400 < widthAt100 / 3 && scrollBar.IsVisible,
                    "The overlay or editor stopped working at 400% while editing.");
                Check(indicator.TransformToAncestor(window).TransformBounds(new Rect(indicator.RenderSize)).Width == indicatorWidth,
                    "The floating thumb width followed the document zoom.");

                Pump(1150);
                Check(scrollBar.Visibility == Visibility.Hidden && !scrollBar.IsHitTestVisible && viewer.ViewportWidth == widthAt400,
                    $"The idle overlay stayed visible, blocked interaction, or changed wrapping ({scrollBar.Visibility}, opacity {scrollBar.Opacity}, viewport {viewer.ViewportWidth} vs {widthAt400}).");
                window.Close();
            }

            var shortNote = new Note { Blocks = [new NoteBlock { Inlines = [new NoteInline { Text = "短便签" }] }] };
            workspace.Store.Save(shortNote);
            workspace.OpenNote(shortNote);
            var shortWindow = workspace.Windows[shortNote.Id];
            Pump(40);
            SendWheel(FindVisual<ScrollViewer>((FrameworkElement)shortWindow.FindName("Editor")).First());
            Pump(40);
            Check(((ScrollBar)shortWindow.FindName("BodyScrollBar")).Visibility == Visibility.Hidden,
                "A note with no overflow displayed a scroll thumb.");
        }
        finally { workspace.PrepareExit(); }

        static void SendWheel(UIElement target) => target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
    }
}
