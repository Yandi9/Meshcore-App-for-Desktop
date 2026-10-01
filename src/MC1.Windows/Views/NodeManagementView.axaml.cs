using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class NodeManagementView : UserControl
{
    public NodeManagementView()
    {
        InitializeComponent();
        CliBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is NodeManagementViewModel vm)
            {
                vm.SendCliCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is NodeManagementViewModel vm)
            vm.CliLines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => CliScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
