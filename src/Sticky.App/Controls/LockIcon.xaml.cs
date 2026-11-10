using System.Windows;
using System.Windows.Controls;

namespace Sticky.App.Controls;

public partial class LockIcon : UserControl
{
    public static readonly DependencyProperty IsUnlockedProperty = DependencyProperty.Register(
        nameof(IsUnlocked), typeof(bool), typeof(LockIcon), new PropertyMetadata(false));
    public bool IsUnlocked { get => (bool)GetValue(IsUnlockedProperty); set => SetValue(IsUnlockedProperty, value); }
    public LockIcon() => InitializeComponent();
}
