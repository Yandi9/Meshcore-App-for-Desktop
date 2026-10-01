using Avalonia.Controls;
using Avalonia.Threading;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => PasswordBox.Focus(), DispatcherPriority.Background);
    }

    private void Cancel_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => (DataContext as LoginViewModel)?.Close?.Invoke();
}
