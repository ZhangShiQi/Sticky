using System.Windows;
using System.Windows.Controls;

namespace Sticky.App;

public static class TextPrompt
{
    public static string? Show(Window owner, string title, string label, string value = "", int maxLength = 50, bool allowEmpty = false)
    {
        var dialog = new Window { Title = title, Owner = owner, Icon = owner.Icon, Width = 370, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 12) });
        var input = new TextBox { Text = value, MaxLength = maxLength, Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var accept = new Button { Content = "保存", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        accept.Click += (_, _) => { if (allowEmpty || input.Text.Trim().Length > 0) dialog.DialogResult = true; };
        actions.Children.Add(cancel); actions.Children.Add(accept); panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
