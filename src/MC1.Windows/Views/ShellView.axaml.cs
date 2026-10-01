using Avalonia.Controls;
using Avalonia.Input;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class ShellView : UserControl
{
    public ShellView() => InitializeComponent();

    private void Toast_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ToastItem t } && DataContext is MainWindowViewModel vm)
        {
            t.Activate();
            vm.Toasts.Remove(t);
        }
    }
}
