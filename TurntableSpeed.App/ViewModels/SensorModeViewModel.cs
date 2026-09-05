using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using TurntableSpeed.App.Drawing;
using TurntableSpeed.App.Services;
using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.App.Localization;
using TurntableSpeed.App.Presentation;
using static TurntableSpeed.App.ViewModels.Display;

namespace TurntableSpeed.App.ViewModels;

public enum ZeroCalibrationState
{
    NotStarted,
    Collecting,
    Succeeded,
    Failed,
}

/// <summary>
/// Mode A — the phone lying on the platter (spec §3, screen contents in §6).
/// </summary>
public sealed class SensorModeViewModel : ObservableBase, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(120);

    private readonly ICaptureEnvironment _environment;
    private readonly SessionRecorder _recorder;
    private readonly IDispatcher _dispatcher;

    /// <summary>
    /// The estimators are not thread-safe and three sensor streams arrive on their own threads,
    /// so every push is serialised here. The work per sample is a few arithmetic operations;
    /// the periodic re-fit is what costs, and it is already throttled by sample count.
    /// </summary>
    private readonly object _gate = new();

    private readonly SensorFusionEstimator _fusion = new();
    private readonly TimeWindow<TimedValue> _rateTrace = new(v => v.T, windowSeconds: 30.0, maxCount: 2000);
    private readonly Stopwatch _sinceRefresh = Stopwatch.StartNew();

    private GyroZeroCalibrator? _calibrator;
    private CancellationTokenSource? _cts;
    private Task? _pumps;
    private bool _refreshQueued;

    public SensorModeViewModel(ICaptureEnvironment environment, SessionRecorder recorder, IDispatcher dispatcher)
    {
        _environment = environment;
        _recorder = recorder;
        _dispatcher = dispatcher;

        StartCommand = new Command(async () => await StartAsync().ConfigureAwait(false), () => !IsRunning);
        StopCommand = new Command(async () => await StopAsync().ConfigureAwait(false), () => IsRunning);
        CalibrateZeroCommand = new Command(BeginZeroCalibration, () => _environment.Sensors.Gyroscope is not null);
        ResetCommand = new Command(ResetSession);

        _fusion.MagnetometerAvailable = _environment.Sensors.Magnetometer?.IsAvailable ?? false;
        _fusion.GyroscopeAvailable = _environment.Sensors.Gyroscope?.IsAvailable ?? false;

        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;

        Refresh();
    }

    /// <summary>
    /// Bindings on <c>{loc:Translate}</c> repaint themselves; text this view model has already
    /// composed does not. Everything derived from the current result is simply recomputed, and
    /// the status line — which may have been written minutes ago — is re-rendered from the
    /// closure that produced it.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ZeroStatus = _zeroStatusText();
        Refresh();
    }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand CalibrateZeroCommand { get; }

    public ICommand ResetCommand { get; }

    public ObservableCollection<Notice> Notices { get; } = [];

    // ---- Headline reading ----

    private SpeedDisplay _speed = SpeedDisplay.Empty;
    public SpeedDisplay Speed { get => _speed; private set => SetProperty(ref _speed, value); }

    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                ((Command)StartCommand).ChangeCanExecute();
                ((Command)StopCommand).ChangeCanExecute();
            }
        }
    }

    // ---- Mode A extras, per §6 ----

    private string _instantaneous = Dash;
    public string InstantaneousRpm { get => _instantaneous; private set => SetProperty(ref _instantaneous, value); }

    private PlotModel _ratePlot = PlotModel.Empty;
    public PlotModel RatePlot { get => _ratePlot; private set => SetProperty(ref _ratePlot, value); }

    private PlotModel _wowPlot = PlotModel.Empty;
    public PlotModel WowPlot { get => _wowPlot; private set => SetProperty(ref _wowPlot, value); }

    private string _wowSummary = Dash;
    public string WowSummary { get => _wowSummary; private set => SetProperty(ref _wowSummary, value); }

    private string _disagreement = Dash;
    public string Disagreement { get => _disagreement; private set => SetProperty(ref _disagreement, value); }

    private string _orientation = Dash;
    public string Orientation { get => _orientation; private set => SetProperty(ref _orientation, value); }

    private string _spinUp = Dash;
    public string SpinUp { get => _spinUp; private set => SetProperty(ref _spinUp, value); }

    private string _scaleFactor = Dash;
    public string GyroScaleFactor { get => _scaleFactor; private set => SetProperty(ref _scaleFactor, value); }

    // ---- Zero calibration (§3.3: the app has to ask for this, not hope for it) ----

    private ZeroCalibrationState _zeroState = ZeroCalibrationState.NotStarted;
    public ZeroCalibrationState ZeroState { get => _zeroState; private set => SetProperty(ref _zeroState, value); }

    private double _zeroProgress;
    public double ZeroProgress { get => _zeroProgress; private set => SetProperty(ref _zeroProgress, value); }

    private string _zeroStatus = AppStrings.SensorZeroNotMeasured;
    public string ZeroStatus { get => _zeroStatus; private set => SetProperty(ref _zeroStatus, value); }

    /// <summary>
    /// How the current status line is produced, not what it currently says.
    /// <para>
    /// A closure rather than a resource key plus arguments: the numbers are captured already
    /// formatted, the compiler still checks the resource name, and re-rendering in the other
    /// language is one call.
    /// </para>
    /// </summary>
    private Func<string> _zeroStatusText = () => AppStrings.SensorZeroNotMeasured;

    private void SetZeroStatus(Func<string> text)
    {
        _zeroStatusText = text;
        ZeroStatus = text();
    }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var sensors = _environment.Sensors;
        if (sensors.Magnetometer is null && sensors.Gyroscope is null)
        {
            SetZeroStatus(() => AppStrings.SensorNoInertialSensors);
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;

        var token = _cts.Token;
        var tasks = new List<Task>(3);

        if (sensors.Magnetometer is { IsAvailable: true } magnetometer)
        {
            tasks.Add(PumpAsync(magnetometer, SensorKind.Magnetometer, token));
        }

        if (sensors.Gyroscope is { IsAvailable: true } gyroscope)
        {
            tasks.Add(PumpAsync(gyroscope, SensorKind.Gyroscope, token));
        }

        if (sensors.Accelerometer is { IsAvailable: true } accelerometer)
        {
            tasks.Add(PumpAsync(accelerometer, SensorKind.Accelerometer, token));
        }

        _pumps = Task.WhenAll(tasks);

        try
        {
            await _pumps.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is not a failure.
        }
        finally
        {
            _dispatcher.Dispatch(() => IsRunning = false);
        }
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_pumps is not null)
            {
                await _pumps.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
        _cts = null;
        _pumps = null;

        lock (_gate)
        {
            _fusion.Flush();
        }

        _dispatcher.Dispatch(Refresh);
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            _fusion.Reset();
            _rateTrace.Clear();
            _calibrator = null;
        }

        _recorder.Clear();
        ZeroState = ZeroCalibrationState.NotStarted;
        ZeroProgress = 0.0;
        SetZeroStatus(() => AppStrings.SensorZeroNotMeasured);
        Refresh();
    }

    /// <summary>
    /// Ask for a still platter and start collecting. Spec §3.3 wants 2–3 seconds of it, and
    /// wants the app to insist rather than quietly proceed with an unknown offset.
    /// </summary>
    public void BeginZeroCalibration()
    {
        lock (_gate)
        {
            _calibrator = new GyroZeroCalibrator();
        }

        ZeroState = ZeroCalibrationState.Collecting;
        ZeroProgress = 0.0;
        SetZeroStatus(() => AppStrings.SensorZeroHoldStill);
    }

    private async Task PumpAsync(IVector3SampleSource source, SensorKind kind, CancellationToken ct)
    {
        await foreach (var sample in source.ReadAsync(ct).ConfigureAwait(false))
        {
            _recorder.Add(kind, sample);

            lock (_gate)
            {
                Consume(kind, sample);
            }

            MaybeRefresh();
        }
    }

    /// <summary>Called under <see cref="_gate"/>.</summary>
    private void Consume(SensorKind kind, in Vector3Sample sample)
    {
        switch (kind)
        {
            case SensorKind.Magnetometer:
                _fusion.PushMagnetometer(sample);
                break;

            case SensorKind.Gyroscope:
                if (_calibrator is not null)
                {
                    _calibrator.Push(sample);
                    return;   // during calibration the platter is stopped; nothing to measure
                }

                _fusion.PushGyroscope(sample);
                break;

            default:
                _fusion.PushAccelerometer(sample);
                break;
        }

        if (double.IsFinite(_fusion.Gyroscope.InstantaneousRadPerSecond))
        {
            _rateTrace.Add(new TimedValue(
                sample.T,
                SpeedMath.RpmFromRadiansPerSecond(Math.Abs(_fusion.Gyroscope.InstantaneousRadPerSecond))));
        }
    }

    /// <summary>
    /// Coalesce UI updates. Sensors arrive at up to 200 Hz; repainting that often would burn
    /// battery to show the user nothing they can perceive.
    /// </summary>
    private void MaybeRefresh()
    {
        if (_refreshQueued || _sinceRefresh.Elapsed < RefreshInterval)
        {
            return;
        }

        _refreshQueued = true;
        _sinceRefresh.Restart();

        _dispatcher.Dispatch(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    private void Refresh()
    {
        SensorFusionResult result;
        GyroZeroCalibrator? calibrator;
        TimedValue[] trace;

        lock (_gate)
        {
            result = _fusion.Result;
            calibrator = _calibrator;
            trace = _rateTrace.ToArray();
        }

        UpdateZeroCalibration(calibrator);

        Speed = SpeedDisplay.From(result.Primary);

        InstantaneousRpm = double.IsFinite(result.InstantaneousRadPerSecond)
            ? Tr.Format(AppStrings.RpmValue,
                Number(SpeedMath.RpmFromRadiansPerSecond(Math.Abs(result.InstantaneousRadPerSecond)), "F3"))
            : Dash;

        var nominal = result.Primary?.Nominal is { } speed ? NominalSpeeds.Rpm(speed) : (double?)null;
        RatePlot = PlotModel.FromTrace(trace, AppStrings.UnitRpm, nominal);

        var wow = result.WowFlutter;
        WowPlot = PlotModel.FromSpectrum(wow.Spectrum, 0.1, 20.0, "%");
        WowSummary = double.IsFinite(wow.RmsPercent)
            ? Tr.Format(AppStrings.SensorWowSummary,
                Number(wow.RmsPercent, "F3"), Number(wow.PeakPercent, "F3"), Number(wow.DominantFrequency, "F3"))
            : Dash;

        Disagreement = double.IsFinite(result.DisagreementPercent)
            ? Tr.Format(AppStrings.SensorDisagreement, Number(result.DisagreementPercent, "+0.00;−0.00"))
            : Dash;

        Orientation = double.IsFinite(result.TiltDegrees)
            ? Tr.Format(AppStrings.SensorTilt, Number(result.TiltDegrees, "F1")) +
              (result.EstimatedRadiusMeters is { } radius
                  ? Tr.Format(AppStrings.SensorRadius, Number(radius * 100.0, "F0"))
                  : string.Empty)
            : Dash;

        SpinUp = result.SpinUpSeconds is { } spinUp
            ? Tr.Format(AppStrings.SensorSpinUpSeconds, Number(spinUp, "F1"))
            : AppStrings.SensorSpinUpNotObserved;

        GyroScaleFactor = result.GyroScaleCalibrated
            ? Tr.Format(AppStrings.SensorGyroScaleMeasured, Number(result.GyroScaleFactor, "F4"))
            : AppStrings.SensorGyroScaleNotMeasured;

        UpdateNotices(UserMessages.For(result.Warnings));
    }

    private void UpdateZeroCalibration(GyroZeroCalibrator? calibrator)
    {
        if (calibrator is null)
        {
            return;
        }

        ZeroProgress = calibrator.Progress;

        if (!calibrator.HasEnoughData)
        {
            var progress = Percent(calibrator.Progress);
            SetZeroStatus(() => Tr.Format(AppStrings.SensorZeroMeasuring, progress));
            return;
        }

        var still = calibrator.IsStill(out var reason);
        var bias = calibrator.Compute();

        lock (_gate)
        {
            _calibrator = null;
        }

        if (!still || bias is null)
        {
            ZeroState = ZeroCalibrationState.Failed;

            // The calibrator reports its reason in English, for the log rather than the screen.
            // What is kept here is the decision, so the sentence can be produced in either
            // language later.
            var stillTurning = reason.Contains("turning", StringComparison.OrdinalIgnoreCase);
            SetZeroStatus(() => stillTurning
                ? AppStrings.SensorZeroStillTurning
                : AppStrings.SensorZeroPhoneMoved);
            return;
        }

        lock (_gate)
        {
            _fusion.ApplyGyroBias(bias);

            // The platter is stopped, so there is no centripetal term: the only moment this
            // device's own gravity reading can be trusted as a reference (§3.4).
            _fusion.Orientation.CaptureGravityReference();
        }

        ZeroState = ZeroCalibrationState.Succeeded;
        ZeroProgress = 1.0;

        var magnitude = Number(bias.Magnitude, "F4");
        var sigma = Number(bias.MaxSigma, "F4");
        SetZeroStatus(() => Tr.Format(AppStrings.SensorZeroMeasured, magnitude, sigma));
    }

    private void UpdateNotices(IReadOnlyList<Notice> notices)
    {
        Notices.Clear();
        foreach (var notice in notices)
        {
            Notices.Add(notice);
        }
    }

    public void Dispose()
    {
        LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
