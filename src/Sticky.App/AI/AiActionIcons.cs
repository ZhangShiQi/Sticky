using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using System.Globalization;
using Sticky.Core;

namespace Sticky.App.AI;

public sealed record AiIconChoice(string Key, string Label);
public sealed class AiIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => AiActionIcons.Create(value as string ?? "sparkles");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public static class AiActionIcons
{
    public static IReadOnlyList<AiIconChoice> Choices { get; } = [new("sparkles", "星光"), new("edit", "编辑"), new("translate", "翻译"), new("summary", "摘要"), new("list", "列表"), new("idea", "灵感"), new("formal", "公文"), new("code", "代码")];
    private static readonly Lazy<Dictionary<string, Geometry>> Shapes = new(() =>
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Sticky;component/Assets/AiActions.svg"))!.Stream;
        XNamespace svg = "http://www.w3.org/2000/svg";
        return XDocument.Load(stream).Descendants(svg + "symbol").ToDictionary(s => s.Attribute("id")!.Value, s =>
        { var data = Geometry.Parse(s.Element(svg + "path")!.Attribute("d")!.Value); data.Freeze(); return data; });
    });
    public static FrameworkElement Create(string key)
    {
        if (!AiCatalogRules.IconKeys.Contains(key)) key = "sparkles";
        var path = new System.Windows.Shapes.Path { Data = Shapes.Value[key], Width = 24, Height = 24, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1) });
        return new Viewbox { Width = 18, Height = 18, Child = path };
    }
}
