using TurntableSpeed.App.Localization;
using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Io;

namespace TurntableSpeed.App.Services;

/// <summary>
/// Keeps the raw samples of the current measurement so the diagnostics screen can export them
/// (§6). Bounded: a long session must not grow memory without limit, so the oldest data is
/// dropped once the cap is reached and the export says how much it covers.
/// </summary>
public sealed class SessionRecorder
{
    private readonly Lock _gate = new();
    private readonly List<Vector3Sample> _magnetometer = [];
    private readonly List<Vector3Sample> _gyroscope = [];
    private readonly List<Vector3Sample> _accelerometer = [];
    private readonly List<float> _audio = [];

    /// <summary>Roughly ten minutes of a 100 Hz sensor stream.</summary>
    public int MaxSensorSamples { get; set; } = 60_000;

    /// <summary>Roughly two minutes at 48 kHz. Audio is by far the bulkiest thing kept.</summary>
    public int MaxAudioSamples { get; set; } = 48_000 * 120;

    public int AudioSampleRate { get; private set; }

    public int MagnetometerCount { get { lock (_gate) { return _magnetometer.Count; } } }

    public int GyroscopeCount { get { lock (_gate) { return _gyroscope.Count; } } }

    public int AccelerometerCount { get { lock (_gate) { return _accelerometer.Count; } } }

    public int AudioSampleCount { get { lock (_gate) { return _audio.Count; } } }

    public double AudioSeconds =>
        AudioSampleRate > 0 ? AudioSampleCount / (double)AudioSampleRate : 0.0;

    public void Add(SensorKind kind, in Vector3Sample sample)
    {
        lock (_gate)
        {
            var target = kind switch
            {
                SensorKind.Magnetometer => _magnetometer,
                SensorKind.Gyroscope => _gyroscope,
                _ => _accelerometer,
            };

            target.Add(sample);
            Trim(target, MaxSensorSamples);
        }
    }

    public void Add(in AudioBlock block)
    {
        lock (_gate)
        {
            AudioSampleRate = block.SampleRate;
            _audio.AddRange(block.Samples.ToArray());
            Trim(_audio, MaxAudioSamples);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _magnetometer.Clear();
            _gyroscope.Clear();
            _accelerometer.Clear();
            _audio.Clear();
            AudioSampleRate = 0;
        }
    }

    public bool HasSensorData(SensorKind kind) => Count(kind) > 0;

    /// <summary>Most recent reading of a sensor, for the diagnostics screen's raw values.</summary>
    public bool TryGetLast(SensorKind kind, out Vector3Sample sample)
    {
        lock (_gate)
        {
            var source = kind switch
            {
                SensorKind.Magnetometer => _magnetometer,
                SensorKind.Gyroscope => _gyroscope,
                _ => _accelerometer,
            };

            if (source.Count == 0)
            {
                sample = default;
                return false;
            }

            sample = source[^1];
            return true;
        }
    }

    public int Count(SensorKind kind)
    {
        lock (_gate)
        {
            return kind switch
            {
                SensorKind.Magnetometer => _magnetometer.Count,
                SensorKind.Gyroscope => _gyroscope.Count,
                _ => _accelerometer.Count,
            };
        }
    }

    /// <summary>Write one sensor stream as CSV, in the format the fixtures and replay use.</summary>
    public void WriteCsv(SensorKind kind, Stream stream, string comment)
    {
        Vector3Sample[] snapshot;
        lock (_gate)
        {
            snapshot = kind switch
            {
                SensorKind.Magnetometer => _magnetometer.ToArray(),
                SensorKind.Gyroscope => _gyroscope.ToArray(),
                _ => _accelerometer.ToArray(),
            };
        }

        using var writer = new StreamWriter(stream, leaveOpen: true);
        SensorCsvIo.Write(writer, snapshot, comment);
    }

    /// <summary>Write the captured audio as a 16-bit mono WAV.</summary>
    public void WriteWav(Stream stream)
    {
        float[] snapshot;
        int rate;
        lock (_gate)
        {
            snapshot = _audio.ToArray();
            rate = AudioSampleRate;
        }

        if (rate <= 0)
        {
            // Surfaced to the user through the export status line, so it is localized.
            throw new InvalidOperationException(AppStrings.ErrorNoAudioRecorded);
        }

        WavIo.Write(stream, snapshot, rate);
    }

    private static void Trim<T>(List<T> items, int max)
    {
        if (items.Count <= max)
        {
            return;
        }

        items.RemoveRange(0, items.Count - max);
    }
}
