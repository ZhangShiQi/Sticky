using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Sticky.App.Controls;

// Uniform cells allow exact scroll ranges without measuring every note in the collection.
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(316.0, FrameworkPropertyMetadataOptions.AffectsMeasure, CellChanged), ValidSize);
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(242.0, FrameworkPropertyMetadataOptions.AffectsMeasure, CellChanged), ValidSize);
    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    private Size _viewport, _extent;
    private double _offset;
    private int _columns = 1;
    private static bool ValidSize(object value) => value is double size && double.IsFinite(size) && size > 0;
    private static void CellChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) { ((VirtualizingWrapPanel)sender)._offset = 0; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new Size();
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Math.Max(ItemWidth, ActualWidth);
        var height = double.IsFinite(availableSize.Height) ? availableSize.Height : Math.Max(ItemHeight, ActualHeight);
        var columns = Math.Max(1, (int)Math.Floor(width / ItemWidth));
        if (columns != _columns) _offset = Math.Floor(Math.Floor(_offset / ItemHeight) * _columns / columns) * ItemHeight;
        _columns = columns; _viewport = new Size(width, height);
        _extent = new Size(width, Math.Ceiling(owner.Items.Count / (double)columns) * ItemHeight);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent.Height - height));
        ScrollOwner?.InvalidateScrollInfo();
        var first = Math.Max(0, ((int)Math.Floor(_offset / ItemHeight) - 1) * columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)Math.Ceiling((_offset + height) / ItemHeight) + 1) * columns - 1);
        // Accessing InternalChildren initializes the items-host generator on the first layout.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1); else generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
        if (last < first) return _viewport;
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
        {
            for (var index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out _);
                if (!children.Contains(child))
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                }
                generator.PrepareItemContainer(child);
                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
        return _viewport;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            var index = ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(child);
            if (index >= 0) child.Arrange(new Rect(index % _columns * ItemWidth, index / _columns * ItemHeight - _offset, ItemWidth, ItemHeight));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        ItemContainerGenerator.RemoveAll();
        RemoveInternalChildRange(0, InternalChildren.Count);
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= ItemsControl.GetItemsOwner(this).Items.Count) return;
        ShowRow(index / _columns); UpdateLayout();
    }
    private void ShowRow(int row)
    {
        var top = row * ItemHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + ItemHeight > _offset + _viewport.Height) SetVerticalOffset(top + ItemHeight - _viewport.Height);
    }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? child = visual;
        while (child is not null && VisualTreeHelper.GetParent(child) != this) child = VisualTreeHelper.GetParent(child);
        if (child is null) return Rect.Empty;
        var index = ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(child);
        if (index < 0) return Rect.Empty;
        ShowRow(index / _columns);
        var visible = new Rect(index % _columns * ItemWidth, index / _columns * ItemHeight - _offset, ItemWidth, ItemHeight);
        visible.Intersect(new Rect(_viewport)); return visible;
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        var next = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (Math.Abs(next - _offset) < .01) return;
        _offset = next; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo();
    }
    public void LineUp() => SetVerticalOffset(_offset - 32);
    public void LineDown() => SetVerticalOffset(_offset + 32);
    public void MouseWheelUp() => SetVerticalOffset(_offset - 96);
    public void MouseWheelDown() => SetVerticalOffset(_offset + 96);
    public void PageUp() => SetVerticalOffset(_offset - ViewportHeight);
    public void PageDown() => SetVerticalOffset(_offset + ViewportHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
