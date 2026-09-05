using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using TurntableSpeed.App.Drawing;
using TurntableSpeed.App.Services;
using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.App.Localization;
using TurntableSpeed.App.Presentation;
using static TurntableSpeed.App.ViewModels.Display;

namespace TurntableSpeed.App.ViewModels;

/// <summary>
/// Mode B — the microphone (spec §4, screen contents in §6).
/// </summary>
public sealed class AudioModeViewModel : ObservableBase, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(150);

    private static readonly NominalSpeed[] NominalChoices =
    {
        NominalSpeed.Rpm33, NominalSpeed.Rpm45, NominalSpeed.Rpm78,
    };

    private readonly ICaptureEnvironment _environment;
    private readonly SessionRecorder _recorder;
    private readonly IDispatcher _dispatcher;
    private readonly Lock _gate = new();
    private readonly AudioFusionEstimator _fusion = new();
    private readonly Stopwatch _sinceRefresh = Stopwatch.StartNew();

    private CancellationTokenSource? _cts;
    private Task? _pump;
    private bool _refreshQueued;

    public AudioModeViewModel(ICaptureEnvironment environment, SessionRecorder recorder, IDispatcher dispatcher)
    {
        _environment = environment;
        _recorder = recorder;
        _dispatcher = dispatcher;

        // The picker starts on 33⅓, and the estimator has to be told so: the setter below is what
        // normally passes the choice on, and it does not run for the initial selection.
        _fusion.ManualNominal = NominalChoices[_selectedNominal];

        StartCommand = new Command(async () => await StartAsync().ConfigureAwait(false), () => !IsRunning);
        StopCommand = new Command(async () => await StopAsync().ConfigureAwait(false), () => IsRunning);
        ResetCommand = new Command(ResetSession);

        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;

        Refresh();
    }

    /// <summary>
    /// The status line can be minutes old, so it is re-rendered from the closure that wrote it;
    /// everything else on the screen is derived from the current result and is simply recomputed.
    /// The nominal picker needs nothing — "33⅓ / 45 / 78" are numerals, not words.
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Status = _statusText();
        Refresh();
    }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand ResetCommand { get; }

    public ObservableCollection<Notice> Notices { get; } = new();

    /// <summary>
    /// The nominal the pitch reading is applied to. Always one of the three — there is no "auto"
    /// setting, because the only thing that could have filled it in is the click estimator, and
    /// the case where that estimator finds nothing is exactly the case where the pitch grid has
    /// to carry the mode. Pinning 33⅓ by default costs a tap on the rare 45 and gives a reading
    /// from the first seconds on everything else.
    /// </summary>
    public IReadOnlyList<string> NominalOptions { get; } = new[] {"33⅓", "45", "78"};

    private int _selectedNominal;

    public int SelectedNominalIndex
    {
        get => _selectedNominal;
        set
        {
            if (value < 0 || value >= NominalChoices.Length || !SetProperty(ref _selectedNominal, value))
            {
                return;
            }

            lock (_gate)
            {
                _fusion.ManualNominal = NominalChoices[value];
            }
        }
    }

    private SpeedDisplay _speed = SpeedDisplay.Empty;

    public SpeedDisplay Speed
    {
        get => _speed;
        private set => SetProperty(ref _speed, value);
    }

    private bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                ((Command) StartCommand).ChangeCanExecute();
                ((Command) StopCommand).ChangeCanExecute();
            }
        }
    }

    private PlotModel _autocorrelation = PlotModel.Empty;

    public PlotModel Autocorrelation
    {
        get => _autocorrelation;
        private set => SetProperty(ref _autocorrelation, value);
    }

    private PlotModel _spectrum = PlotModel.Empty;

    public PlotModel Spectrum
    {
        get => _spectrum;
        private set => SetProperty(ref _spectrum, value);
    }

    private string _inputLevel = Dash;

    public string InputLevel
    {
        get => _inputLevel;
        private set => SetProperty(ref _inputLevel, value);
    }

    /// <summary>0..1 for the level bar: −60 dBFS at the bottom, full scale at the top.</summary>
    private double _inputLevelFraction;

    public double InputLevelFraction
    {
        get => _inputLevelFraction;
        private set => SetProperty(ref _inputLevelFraction, value);
    }

    private string _inputQuality = Dash;

    public string InputQuality
    {
        get => _inputQuality;
        private set => SetProperty(ref _inputQuality, value);
    }

    private string _pitchDeviation = Dash;

    public string PitchDeviation
    {
        get => _pitchDeviation;
        private set => SetProperty(ref _pitchDeviation, value);
    }

    private string _clickReading = Dash;

    public string ClickReading
    {
        get => _clickReading;
        private set => SetProperty(ref _clickReading, value);
    }

    private string _disagreement = Dash;

    public string Disagreement
    {
        get => _disagreement;
        private set => SetProperty(ref _disagreement, value);
    }

    private string _wowStatus = Dash;

    public string WowStatus
    {
        get => _wowStatus;
        private set => SetProperty(ref _wowStatus, value);
    }

    private double _wowProgress;

    public double WowProgress
    {
        get => _wowProgress;
        private set => SetProperty(ref _wowProgress, value);
    }

    private string _status = AppStrings.AudioStatusReady;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>How the current status was produced, so it can be produced again in the other
    /// language. See <see cref="OnLanguageChanged"/>.</summary>
    private Func<string> _statusText = () => AppStrings.AudioStatusReady;

    private void SetStatus(Func<string> text)
    {
        _statusText = text;
        Status = text();
    }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        SetStatus(() => AppStrings.AudioStatusRequestingMicrophone);

        if (!await _environment.Permissions.RequestMicrophoneAsync().ConfigureAwait(false))
        {
            SetStatus(() => AppStrings.AudioStatusMicrophoneDenied);
            return;
        }

        var settings = AudioCaptureSettings.Default;
        IAudioCaptureSource source;

        try
        {
            source = _environment.CreateAudioCapture(settings);
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            SetStatus(() => Tr.Format(AppStrings.AudioStatusMicrophoneFailed, message));
            return;
        }

        _cts = new CancellationTokenSource();
        IsRunning = true;
        SetStatus(() => AppStrings.AudioStatusRunning);

        _pump = PumpAsync(source, _cts.Token);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            _dispatcher.Dispatch(() => SetStatus(() => Tr.Format(AppStrings.AudioStatusCaptureFailed, message)));
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
            if (_pump is not null)
            {
                await _pump.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        cts.Dispose();
        _cts = null;
        _pump = null;

        lock (_gate)
        {
            _fusion.Flush();
        }

        _dispatcher.Dispatch(() =>
        {
            SetStatus(() => AppStrings.AudioStatusStopped);
            Refresh();
        });
    }

    public void ResetSession()
    {
        lock (_gate)
        {
            _fusion.Reset();
        }

        _recorder.Clear();
        SetStatus(() => AppStrings.AudioStatusReady);
        Refresh();
    }

    private async Task PumpAsync(IAudioCaptureSource source, CancellationToken ct)
    {
        var reported = false;

        await foreach (var block in source.ReadAsync(ct).ConfigureAwait(false))
        {
            _recorder.Add(block);

            lock (_gate)
            {
                if (!reported)
                {
                    // Valid only once capture has actually produced a block: before that the
                    // platform has not committed to anything (§4.1).
                    _fusion.SetCaptureReport(source.Report);
                    reported = true;
                }

                _fusion.Push(block);
            }

            MaybeRefresh();
        }
    }

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
        AudioFusionResult result;
        Core.Dsp.Spectrum lastSpectrum;

        lock (_gate)
        {
            result = _fusion.Result;
            lastSpectrum = _fusion.Pitch.LastSpectrum;
        }

        Speed = SpeedDisplay.From(result.Primary);
        Autocorrelation = PlotModel.FromAutocorrelation(result.Autocorrelation);
        Spectrum = PlotModel.FromSpectrum(lastSpectrum, 50.0, 5000.0, AppStrings.UnitAmplitude);

        var quality = result.Quality;
        InputLevel = double.IsFinite(quality.RmsDbfs)
            ? Tr.Format(AppStrings.AudioInputLevel, Number(quality.RmsDbfs, "F1"), Number(quality.PeakDbfs, "F1"))
            : Dash;
        InputLevelFraction = double.IsFinite(quality.RmsDbfs)
            ? Math.Clamp((quality.RmsDbfs + 60.0) / 60.0, 0.0, 1.0)
            : 0.0;

        InputQuality = double.IsFinite(quality.NoiseFloorDbfs)
            ? Tr.Format(AppStrings.AudioInputQuality,
                Number(quality.NoiseFloorDbfs, "F1"), Number(quality.DynamicRangeDb, "F1"))
            : Dash;

        ClickReading = result.Click is { } click
            ? Tr.Format(AppStrings.AudioClickReading,
                Number(click.RevolutionsPerMinute, "F3"), Number(click.Margin95, "F3"))
            : Dash;

        PitchDeviation = double.IsFinite(result.DeviationCents)
            ? Tr.Format(AppStrings.AudioPitchCents, Number(result.DeviationCents, "+0.0;−0.0"))
            : Dash;

        Disagreement = double.IsFinite(result.DisagreementPercent)
            ? Tr.Format(AppStrings.AudioDisagreement, Number(result.DisagreementPercent, "+0.00;−0.00"))
            : Dash;

        WowProgress = result.WowProgress;
        WowStatus = result.WowSpectrum is { } wow
            ? Tr.Format(AppStrings.AudioWowPeak,
                Number(wow.RevolutionsPerMinute / 60.0, "F4"), Number(wow.RevolutionsPerMinute, "F3"))
            : Tr.Format(AppStrings.AudioWowProgress, Percent(result.WowProgress));

        var notices = new List<Notice>();
        notices.AddRange(UserMessages.For(result.Warnings));
        notices.AddRange(UserMessages.For(quality.Warnings));

        Notices.Clear();
        foreach (var notice in Deduplicate(notices))
        {
            Notices.Add(notice);
        }
    }

    /// <summary>
    /// The fusion warnings and the raw input diagnostics overlap on purpose — one is the summary
    /// the other the detail — so the same thing must not be said twice on screen.
    /// </summary>
    private static IEnumerable<Notice> Deduplicate(IEnumerable<Notice> notices)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notice in notices)
        {
            if (seen.Add(notice.Title))
            {
                yield return notice;
            }
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
