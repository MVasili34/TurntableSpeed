using TurntableSpeed.App.ViewModels;

namespace TurntableSpeed.App.Views;

public partial class DiagnosticsPage : ContentPage
{
    private readonly DiagnosticsViewModel _viewModel;
    private IDispatcherTimer? _timer;

    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _viewModel.Refresh();

        // Raw sensor values are only interesting live. Twice a second is enough to read.
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => _viewModel.Refresh();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _timer?.Stop();
        _timer = null;
        base.OnDisappearing();
    }
}
