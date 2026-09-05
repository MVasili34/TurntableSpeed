using System.ComponentModel;
using TurntableSpeed.App.Drawing;
using TurntableSpeed.App.Localization;
using TurntableSpeed.App.ViewModels;

namespace TurntableSpeed.App.Views;

public partial class SensorModePage : ContentPage
{
    private readonly SensorModeViewModel _viewModel;
    private readonly PlotDrawable _rate = new();
    private readonly PlotDrawable _wow = new();

    public SensorModePage(SensorModeViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = viewModel;

        RateChart.Drawable = _rate;
        WowChart.Drawable = _wow;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.PropertyChanged += OnViewModelChanged;
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
        Redraw();
    }

    protected override void OnDisappearing()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnDisappearing();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A GraphicsView does not observe its drawable, so a changed plot has to be pushed in
        // and the view told to repaint.
        if (e.PropertyName is nameof(SensorModeViewModel.RatePlot) or nameof(SensorModeViewModel.WowPlot))
        {
            Redraw();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        // The captions are set here rather than at construction: a chart that is still empty
        // when the language changes has nothing but its caption on screen.
        _rate.EmptyText = AppStrings.SensorPlotRateEmpty;
        _wow.EmptyText = AppStrings.SensorPlotWowEmpty;

        _rate.Model = _viewModel.RatePlot;
        _wow.Model = _viewModel.WowPlot;
        RateChart.Invalidate();
        WowChart.Invalidate();
    }
}
