using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;

namespace Sticky.App.Controls;

/// <summary>Our embedded SVG symbol sheet uses only paths in the shared SVG/WPF geometry vocabulary.</summary>
internal static class HistoryActionIcons
{
    private static readonly Lazy<IReadOnlyDictionary<string, Geometry>> Shapes = new(Load);

    private static IReadOnlyDictionary<string, Geometry> Load()
    {
        var uri = new Uri("pack://application:,,,/Sticky;component/Assets/HistoryActions.svg");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        XNamespace svg = "http://www.w3.org/2000/svg";
        return XDocument.Load(stream).Descendants(svg + "symbol").ToDictionary(symbol => symbol.Attribute("id")!.Value, symbol =>
        {
            var shape = Geometry.Parse(string.Join(" ", symbol.Elements(svg + "path").Select(path => path.Attribute("d")!.Value)));
            shape.Freeze();
            return shape;
        }, StringComparer.Ordinal);
    }

    public static FrameworkElement Create(string kind)
    {
        var key = kind switch
        {
            "baseline" => "baseline", "noteCreated" => "create",
            "noteDeleted" or "noteRemoved" or "externalRemoved" => "delete",
            "noteRestored" or "historyRestored" => "restore", "noteMoved" => "move", "groupsChanged" => "group", _ => "edit"
        };
        var path = new Path { Data = Shapes.Value[key], Width = 24, Height = 24, StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        return new Viewbox { Width = 18, Height = 18, Child = path, HorizontalAlignment = HorizontalAlignment.Center };
    }
}
