using System.Runtime.CompilerServices;
using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.Core.Io;

/// <summary>
/// Replays a recorded sensor stream. Samples come back with their original timestamps but
/// <em>without</em> the original delays, so a two-minute recording runs through the whole
/// pipeline in milliseconds. Enumerating twice yields exactly the same sequence, which is what
/// makes the determinism tests possible.
/// </summary>
public sealed class ReplayVector3Source : IVector3SampleSource
{
    private readonly IReadOnlyList<Vector3Sample> _samples;

    public ReplayVector3Source(IReadOnlyList<Vector3Sample> samples, SensorKind kind)
    {
        _samples = samples ?? throw new ArgumentNullException(nameof(samples));
        Kind = kind;
    }

    public SensorKind Kind { get; }

    public int Count => _samples.Count;

    /// <summary>Median delivery rate of the recording, Hz; 0 when there is not enough data.</summary>
    public double NominalRateHz
    {
        get
        {
            if (_samples.Count < 2)
            {
                return 0.0;
            }

            var span = _samples[^1].T - _samples[0].T;
            return span > 0.0 ? (_samples.Count - 1) / span : 0.0;
        }
    }

    public bool IsAvailable => _samples.Count > 0;

    /// <summary>Load a recording written by <see cref="SensorCsvIo"/>.</summary>
    public static ReplayVector3Source FromCsvFile(string path, SensorKind kind) =>
        new(SensorCsvIo.ReadFile(path), kind);

    public async IAsyncEnumerable<Vector3Sample> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var sample in _samples)
        {
            ct.ThrowIfCancellationRequested();
            yield return sample;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Synchronous drain — what most tests actually want.</summary>
    public void PushAll(Action<Vector3Sample> push)
    {
        foreach (var sample in _samples)
        {
            push(sample);
        }
    }
}

/// <summary>
/// Replays recorded audio as <see cref="AudioBlock"/>s. Block timestamps are derived from the
/// sample index and the sample rate — never from a clock — so replaying the same file always
/// produces the same block boundaries and the same estimator output.
/// </summary>
public sealed class ReplayAudioSource : IAudioCaptureSource
{
    private readonly ReadOnlyMemory<float> _samples;

    public ReplayAudioSource(
        ReadOnlyMemory<float> samples,
        int sampleRate,
        int blockSize = 4096,
        AudioCaptureReport? report = null,
        double startTime = 0.0)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        if (blockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }

        _samples = samples;
        StartTime = startTime;
        Settings = new AudioCaptureSettings(sampleRate, BitsPerSample: 32, PreferUnprocessed: true, blockSize);

        // Nothing in this path applies noise suppression, AGC or echo cancellation. Whether the
        // *recording* was made through them is a property of the file, and the input diagnostics
        // are what answer that.
        Report = report ?? new AudioCaptureReport(
            sampleRate, true, true, true, true, true,
            "replayed from a file; no platform processing in this path");
    }

    public AudioCaptureSettings Settings { get; }

    public AudioCaptureReport Report { get; }

    public double StartTime { get; }

    public int SampleCount => _samples.Length;

    public double Duration => _samples.Length / (double)Settings.SampleRate;

    public static ReplayAudioSource FromWavFile(string path, int blockSize = 4096)
    {
        var wav = WavIo.ReadFile(path);
        return new ReplayAudioSource(wav.Samples, wav.SampleRate, blockSize);
    }

    public async IAsyncEnumerable<AudioBlock> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var block in Blocks())
        {
            ct.ThrowIfCancellationRequested();
            yield return block;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>The block sequence, without the async machinery.</summary>
    public IEnumerable<AudioBlock> Blocks()
    {
        var sampleRate = Settings.SampleRate;
        var blockSize = Settings.BlockSize;

        for (var offset = 0; offset < _samples.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, _samples.Length - offset);
            yield return new AudioBlock(
                StartTime + offset / (double)sampleRate,
                _samples.Slice(offset, length),
                sampleRate);
        }
    }

    /// <summary>Synchronous drain — what most tests actually want.</summary>
    public void PushAll(Action<AudioBlock> push)
    {
        foreach (var block in Blocks())
        {
            push(block);
        }
    }
}
