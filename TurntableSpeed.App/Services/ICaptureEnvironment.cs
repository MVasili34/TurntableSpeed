using TurntableSpeed.App.Localization;
using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.App.Services;

/// <summary>
/// Everything the app needs from the device, behind one interface so the shared code never
/// mentions a platform type. Spec §2: capture sits behind interfaces in the core, and the
/// implementations live in the platform project.
/// </summary>
public interface ICaptureEnvironment
{
    ISensorSuite Sensors { get; }

    IPermissionBroker Permissions { get; }

    /// <summary>Short description of the device and the capture path, for the diagnostics screen.</summary>
    string Description { get; }

    /// <summary>
    /// Open an audio capture. Created per measurement rather than once, because the settings —
    /// and whether the platform honours them — are part of what is being measured.
    /// </summary>
    IAudioCaptureSource CreateAudioCapture(AudioCaptureSettings settings);

    /// <summary>
    /// Write an export to wherever the platform keeps user-visible files, returning the path
    /// shown to the user. §6 asks the diagnostics screen for raw CSV/WAV export.
    /// </summary>
    Task<string> ExportAsync(string fileName, Action<Stream> write, CancellationToken ct = default);
}

/// <summary>
/// Stands in when a platform provides nothing — and on the desktop target used to compile-check
/// the shared code. Reports everything as unavailable rather than throwing, so the UI degrades
/// into an explanation instead of a crash (spec §6, §10).
/// </summary>
public sealed class UnavailableCaptureEnvironment : ICaptureEnvironment
{
    public ISensorSuite Sensors { get; } = new NoSensors();

    public IPermissionBroker Permissions { get; } = new NoPermissions();

    public string Description => AppStrings.CaptureUnavailable;

    public IAudioCaptureSource CreateAudioCapture(AudioCaptureSettings settings) =>
        new NoAudio(settings);

    public Task<string> ExportAsync(string fileName, Action<Stream> write, CancellationToken ct = default) =>
        Task.FromException<string>(new PlatformNotSupportedException(AppStrings.ExportUnavailable));

    private sealed class NoSensors : ISensorSuite
    {
        public IVector3SampleSource? Magnetometer => null;

        public IVector3SampleSource? Gyroscope => null;

        public IVector3SampleSource? Accelerometer => null;
    }

    private sealed class NoPermissions : IPermissionBroker
    {
        public Task<bool> RequestMicrophoneAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> IsMicrophoneGrantedAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NoAudio : IAudioCaptureSource
    {
        public NoAudio(AudioCaptureSettings settings) => Settings = settings;

        public AudioCaptureSettings Settings { get; }

        public AudioCaptureReport Report { get; } = AudioCaptureReport.Unknown;

        public async IAsyncEnumerable<AudioBlock> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
