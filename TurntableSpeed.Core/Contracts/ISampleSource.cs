namespace TurntableSpeed.Core.Contracts;

/// <summary>
/// A stream of samples. Implementations live outside the core: on device they wrap the
/// platform sensor/audio APIs, in tests they replay a fixture.
/// </summary>
public interface ISampleSource<T>
{
    IAsyncEnumerable<T> ReadAsync(CancellationToken ct);
}

/// <summary>A tri-axial sensor stream plus the metadata the UI needs to explain itself.</summary>
public interface IVector3SampleSource : ISampleSource<Vector3Sample>
{
    SensorKind Kind { get; }

    /// <summary>Nominal delivery rate in Hz; 0 when unknown.</summary>
    double NominalRateHz { get; }

    /// <summary>False when the device has no such sensor. The UI must degrade, not crash.</summary>
    bool IsAvailable { get; }
}

/// <summary>Requested audio capture configuration.</summary>
/// <param name="SampleRate">44100 or 48000.</param>
/// <param name="BitsPerSample">16 or 32.</param>
/// <param name="PreferUnprocessed">
/// Ask the platform for an unprocessed input. Noise suppression, AGC and echo cancellation
/// destroy exactly the transients the click-periodicity estimator lives on.
/// </param>
/// <param name="BlockSize">Frames per delivered <see cref="AudioBlock"/>.</param>
public sealed record AudioCaptureSettings(
    int SampleRate = 48000,
    int BitsPerSample = 16,
    bool PreferUnprocessed = true,
    int BlockSize = 4096)
{
    public static AudioCaptureSettings Default { get; } = new();
}

/// <summary>What the platform actually gave us, as opposed to what we asked for.</summary>
public sealed record AudioCaptureReport(
    int SampleRate,
    bool UnprocessedSourceRequested,
    bool UnprocessedSourceGranted,
    bool NoiseSuppressorDisabled,
    bool AutomaticGainControlDisabled,
    bool AcousticEchoCancelerDisabled,
    string Details)
{
    /// <summary>True when every processing block we know about is confirmed off.</summary>
    public bool IsClean =>
        UnprocessedSourceGranted &&
        NoiseSuppressorDisabled &&
        AutomaticGainControlDisabled &&
        AcousticEchoCancelerDisabled;

    public static AudioCaptureReport Unknown { get; } =
        new(0, false, false, false, false, false, "capture not started");
}

/// <summary>An audio stream together with the truth about how it was captured.</summary>
public interface IAudioCaptureSource : ISampleSource<AudioBlock>
{
    AudioCaptureSettings Settings { get; }

    /// <summary>Valid once <see cref="ISampleSource{T}.ReadAsync"/> has produced a block.</summary>
    AudioCaptureReport Report { get; }
}

/// <summary>The three sensors the app cares about. Any of them may be null on a given device.</summary>
public interface ISensorSuite
{
    IVector3SampleSource? Magnetometer { get; }

    IVector3SampleSource? Gyroscope { get; }

    IVector3SampleSource? Accelerometer { get; }
}

/// <summary>Runtime permission gate, implemented by the platform project.</summary>
public interface IPermissionBroker
{
    Task<bool> RequestMicrophoneAsync(CancellationToken ct = default);

    Task<bool> IsMicrophoneGrantedAsync(CancellationToken ct = default);
}
