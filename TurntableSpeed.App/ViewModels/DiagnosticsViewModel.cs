using System.Windows.Input;
using TurntableSpeed.App.Localization;
using TurntableSpeed.App.Services;
using TurntableSpeed.Core.Contracts;
using static TurntableSpeed.App.ViewModels.Display;

namespace TurntableSpeed.App.ViewModels;

/// <summary>
/// The diagnostics screen (spec §6): raw sensor values, what the audio capture actually did,
/// the measured gyroscope scale factor, and export of the raw data — which is how the fixture
/// set in §7.4 gets fed.
/// </summary>
public sealed class DiagnosticsViewModel : ObservableBase
{
    private readonly ICaptureEnvironment _environment;
    private readonly SessionRecorder _recorder;
    private readonly SensorModeViewModel _sensorMode;
    private readonly AudioModeViewModel _audioMode;

    public DiagnosticsViewModel(
        ICaptureEnvironment environment,
        SessionRecorder recorder,
        SensorModeViewModel sensorMode,
        AudioModeViewModel audioMode)
    {
        _environment = environment;
        _recorder = recorder;
        _sensorMode = sensorMode;
        _audioMode = audioMode;

        ExportMagnetometerCommand = new Command(async () => await ExportSensorAsync(SensorKind.Magnetometer).ConfigureAwait(false));
        ExportGyroscopeCommand = new Command(async () => await ExportSensorAsync(SensorKind.Gyroscope).ConfigureAwait(false));
        ExportAccelerometerCommand = new Command(async () => await ExportSensorAsync(SensorKind.Accelerometer).ConfigureAwait(false));
        ExportAudioCommand = new Command(async () => await ExportAudioAsync().ConfigureAwait(false));
        RefreshCommand = new Command(Refresh);

        // Never unsubscribed: this view model is a singleton and outlives everything that could
        // do the unsubscribing.
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;

        Refresh();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ExportStatus = _exportStatusText();
        Refresh();

        // Device is computed, not stored, and the platform builds part of it out of localized
        // words ("магнитометр: есть"), so it has to be asked for again.
        Raise(nameof(Device));
    }

    public ICommand ExportMagnetometerCommand { get; }

    public ICommand ExportGyroscopeCommand { get; }

    public ICommand ExportAccelerometerCommand { get; }

    public ICommand ExportAudioCommand { get; }

    public ICommand RefreshCommand { get; }

    public string Device => _environment.Description;

    private string _magnetometer = Dash;
    public string Magnetometer { get => _magnetometer; private set => SetProperty(ref _magnetometer, value); }

    private string _gyroscope = Dash;
    public string Gyroscope { get => _gyroscope; private set => SetProperty(ref _gyroscope, value); }

    private string _accelerometer = Dash;
    public string Accelerometer { get => _accelerometer; private set => SetProperty(ref _accelerometer, value); }

    private string _audioCapture = Dash;
    public string AudioCapture { get => _audioCapture; private set => SetProperty(ref _audioCapture, value); }

    private string _audioQuality = Dash;
    public string AudioQuality { get => _audioQuality; private set => SetProperty(ref _audioQuality, value); }

    private string _scaleFactor = Dash;
    public string GyroScaleFactor { get => _scaleFactor; private set => SetProperty(ref _scaleFactor, value); }

    private string _recorded = Dash;
    public string Recorded { get => _recorded; private set => SetProperty(ref _recorded, value); }

    private string _exportStatus = string.Empty;
    public string ExportStatus { get => _exportStatus; private set => SetProperty(ref _exportStatus, value); }

    /// <summary>The last export result, kept as the recipe that produced it — a path stays a
    /// path, but the sentence around it follows the language.</summary>
    private Func<string> _exportStatusText = () => string.Empty;

    private void SetExportStatus(Func<string> text)
    {
        _exportStatusText = text;
        ExportStatus = text();
    }

    public void Refresh()
    {
        Magnetometer = Describe(SensorKind.Magnetometer, AppStrings.UnitMicrotesla);
        Gyroscope = Describe(SensorKind.Gyroscope, AppStrings.UnitRadiansPerSecond);
        Accelerometer = Describe(SensorKind.Accelerometer, AppStrings.UnitMetresPerSecondSquared);

        GyroScaleFactor = _sensorMode.GyroScaleFactor;

        AudioCapture = Tr.Format(AppStrings.DiagAudioCapture,
            Number(_recorder.AudioSampleRate), Number(_recorder.AudioSeconds, "F1"));
        AudioQuality = _audioMode.InputQuality;

        Recorded = Tr.Format(AppStrings.DiagRecorded,
            Number(_recorder.Count(SensorKind.Magnetometer)),
            Number(_recorder.Count(SensorKind.Gyroscope)),
            Number(_recorder.Count(SensorKind.Accelerometer)));
    }

    private string Describe(SensorKind kind, string unit)
    {
        if (!_recorder.TryGetLast(kind, out var sample))
        {
            return AppStrings.NoData;
        }

        return Tr.Format(
            AppStrings.DiagSampleLine,
            Number(sample.X, "F4"),
            Number(sample.Y, "F4"),
            Number(sample.Z, "F4"),
            unit,
            Number(sample.Magnitude, "F4"));
    }

    private async Task ExportSensorAsync(SensorKind kind)
    {
        if (_recorder.Count(kind) == 0)
        {
            SetExportStatus(() => AppStrings.DiagExportNothing);
            return;
        }

        var name = kind.ToString().ToLowerInvariant();

        try
        {
            var path = await _environment.ExportAsync(
                $"turntable-{name}.csv",
                stream => _recorder.WriteCsv(kind, stream, $"{name}; {_environment.Description}"))
                .ConfigureAwait(false);

            SetExportStatus(() => Tr.Format(AppStrings.DiagExportSaved, path));
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            SetExportStatus(() => Tr.Format(AppStrings.DiagExportFailed, message));
        }
    }

    private async Task ExportAudioAsync()
    {
        if (_recorder.AudioSampleCount == 0)
        {
            SetExportStatus(() => AppStrings.DiagExportNoAudio);
            return;
        }

        try
        {
            var path = await _environment.ExportAsync(
                "turntable-audio.wav",
                stream => _recorder.WriteWav(stream))
                .ConfigureAwait(false);

            SetExportStatus(() => Tr.Format(AppStrings.DiagExportSaved, path));
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            SetExportStatus(() => Tr.Format(AppStrings.DiagExportFailed, message));
        }
    }
}
