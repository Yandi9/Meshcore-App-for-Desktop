using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MC1.Windows.ViewModels;

namespace MC1.Windows.Views;

public partial class CliView : UserControl
{
    private CliViewModel? _vm;

    public CliView()
    {
        InitializeComponent();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.LinesAppended -= ScrollToEnd;
        _vm = DataContext as CliViewModel;
        if (_vm is not null) _vm.LinesAppended += ScrollToEnd;
        ScrollToEnd();
        Dispatcher.UIThread.Post(() => InputBox.Focus(), DispatcherPriority.Background);
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => OutputScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                _vm.SubmitCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                _vm.HistoryPrevious();
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Down:
                _vm.HistoryNext();
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Tab:
                _vm.Complete();
                InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
        }
    }
}
