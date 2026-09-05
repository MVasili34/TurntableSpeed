using System.ComponentModel;
using TurntableSpeed.App.Drawing;
using TurntableSpeed.App.Localization;
using TurntableSpeed.App.ViewModels;

namespace TurntableSpeed.App.Views;

public partial class AudioModePage : ContentPage
{
    private readonly AudioModeViewModel _viewModel;
    private readonly PlotDrawable _acf = new();
    private readonly PlotDrawable _spectrum = new();

    public AudioModePage(AudioModeViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = viewModel;

        AcfChart.Drawable = _acf;
        SpectrumChart.Drawable = _spectrum;
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
        if (e.PropertyName is nameof(AudioModeViewModel.Autocorrelation) or nameof(AudioModeViewModel.Spectrum))
        {
            Redraw();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        // See SensorModePage: the empty-state caption is often the only text on the chart, so it
        // is refreshed on every repaint rather than fixed at construction.
        _acf.EmptyText = AppStrings.AudioPlotAcfEmpty;
        _spectrum.EmptyText = AppStrings.AudioPlotSpectrumEmpty;

        _acf.Model = _viewModel.Autocorrelation;
        _spectrum.Model = _viewModel.Spectrum;
        AcfChart.Invalidate();
        SpectrumChart.Invalidate();
    }
}
