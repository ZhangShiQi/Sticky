using System.Windows;
using System.Windows.Controls;

namespace Sticky.App;

internal static class VaultPasswordPrompt
{
    public static string? Show(Window owner, string title, bool confirm = false, bool recovery = false)
    {
        var dialog = new Window { Owner = owner, Title = title, Width = 430, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = recovery ? "输入单独保存的恢复密钥" : confirm
            ? "设置至少 12 个字符的独立长口令。主密码不保存在本机。" : "输入主密码，解锁加密保险箱。", TextWrapping = TextWrapping.Wrap });
        var input = new PasswordBox { MaxLength = 1024, Margin = new Thickness(0, 14, 0, 12), Padding = new Thickness(8) };
        panel.Children.Add(input);
        var repeat = new PasswordBox { MaxLength = 1024, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(8) };
        if (confirm) { panel.Children.Add(new TextBlock { Text = "再次输入主密码" }); panel.Children.Add(repeat); }
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true });
        var accept = new Button { Content = confirm ? "设置主密码" : "解锁", IsDefault = true };
        accept.Click += (_, _) =>
        {
            if (input.Password.Length == 0) return;
            if (confirm && (input.Password.Length < 12 || input.Password != repeat.Password))
            { error.Text = "主密码至少 12 个字符，且两次输入必须一致。"; return; }
            dialog.DialogResult = true;
        };
        buttons.Children.Add(accept); panel.Children.Add(buttons); dialog.Content = panel;
        dialog.Loaded += (_, _) => input.Focus();
        var result = dialog.ShowDialog() == true ? input.Password : null;
        input.Clear(); repeat.Clear(); return result;
    }
}
