using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Sticky.Core;

namespace Sticky.App.Controls;

internal static class TagPills
{
    public static Border Create(NoteTag tag, Action? activate = null, Action? remove = null, bool compact = false)
    {
        var label = new TextBlock
        {
            Text = tag.Name, FontSize = compact ? 11 : 12, MaxWidth = compact ? 96 : 190,
            Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        var pill = new Border
        {
            Background = Fill(tag), CornerRadius = new CornerRadius(13), Margin = compact ? new Thickness(2, 0, 2, 0) : new Thickness(0, 0, 6, 5),
            Padding = compact ? new Thickness(6, 0, 6, 0) : new Thickness(9, 0, remove is null ? 9 : 3, 0),
            Height = compact ? 22 : 27, ToolTip = tag.Name, VerticalAlignment = VerticalAlignment.Center
        };
        var contents = new StackPanel { Orientation = Orientation.Horizontal };
        if (activate is null) contents.Children.Add(label);
        else contents.Children.Add(Button(label, compact ? "编辑标签 " + tag.Name : "添加标签 " + tag.Name, activate));
        if (remove is not null) contents.Children.Add(Button("×", "移除标签 " + tag.Name, remove, new Thickness(6, 0, 3, 0)));
        pill.Child = contents;
        return pill;
    }

    public static Button Button(object content, string accessibleName, Action click, Thickness padding = default)
    {
        var button = new Button
        {
            Content = content, Background = Brushes.Transparent, Foreground = Brushes.Black,
            Margin = new Thickness(0), Padding = padding, MinWidth = 0, FontSize = 13
        };
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) => click();
        return button;
    }

    public static SolidColorBrush Fill(NoteTag tag)
    {
        double hue = BitConverter.ToUInt32(tag.Id.ToByteArray()) % 360 / 30d;
        byte Channel(int offset)
        {
            var sector = (hue + offset) % 12;
            var wave = Math.Max(-1, Math.Min(Math.Min(sector - 3, 9 - sector), 1));
            return (byte)Math.Round((.84 - .115 * wave) * 255);
        }
        var brush = new SolidColorBrush(Color.FromRgb(Channel(0), Channel(8), Channel(4)));
        brush.Freeze();
        return brush;
    }

    public static Border Swatch(NoteTag tag) => new() { Width = 11, Height = 11, CornerRadius = new CornerRadius(5), Background = Fill(tag) };
}
