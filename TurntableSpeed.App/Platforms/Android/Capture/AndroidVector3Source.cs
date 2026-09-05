using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Android.Hardware;
using TurntableSpeed.Core.Contracts;

// Namespace deliberately without an "Android" segment: TurntableSpeed.App.Platforms.Android.*
// would shadow the root Android.* namespace inside these very files.
namespace TurntableSpeed.App.Capture;

/// <summary>
/// One tri-axial Android sensor as a <see cref="IVector3SampleSource"/>.
/// <para>
/// Timestamps come from <c>SensorEvent.Timestamp</c>, the sensor hub's own clock in
/// nanoseconds — not from the system clock. That is the whole point: the core measures a
/// frequency against this time base, so it has to be the steadiest one available.
/// </para>
/// </summary>
public sealed class AndroidVector3Source : Java.Lang.Object, ISensorEventListener, IVector3SampleSource
{
    private readonly SensorManager _manager;
    private readonly Sensor _sensor;
    private readonly int _samplingPeriodMicroseconds;

    private Channel<Vector3Sample>? _channel;

    public AndroidVector3Source(SensorManager manager, Sensor sensor, SensorKind kind, double requestedRateHz)
    {
        _manager = manager;
        _sensor = sensor;
        Kind = kind;
        NominalRateHz = requestedRateHz;

        _samplingPeriodMicroseconds = requestedRateHz > 0.0
            ? (int)Math.Round(1_000_000.0 / requestedRateHz)
            : 0;
    }

    public SensorKind Kind { get; }

    public double NominalRateHz { get; }

    public bool IsAvailable => true;

    public async IAsyncEnumerable<Vector3Sample> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Bounded and dropping: if the consumer stalls, losing the oldest samples is better than
        // growing without limit. The estimators tolerate gaps — they work off timestamps.
        var channel = Channel.CreateBounded<Vector3Sample>(new BoundedChannelOptions(8192)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        _channel = channel;

        // The int overload is exposed through the SensorDelay enum; a cast passes a real
        // microsecond period rather than one of the four coarse presets.
        _manager.RegisterListener(this, _sensor, (SensorDelay)_samplingPeriodMicroseconds);

        try
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return sample;
            }
        }
        finally
        {
            _manager.UnregisterListener(this, _sensor);
            channel.Writer.TryComplete();
            _channel = null;
        }
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (e?.Values is null || e.Values.Count < 3)
        {
            return;
        }

        var sample = new Vector3Sample(
            e.Timestamp / 1_000_000_000.0,
            e.Values[0],
            e.Values[1],
            e.Values[2]);

        _channel?.Writer.TryWrite(sample);
    }

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy)
    {
        // Accuracy is reported but not acted on: the magnetometer estimator judges the data by
        // how well the phase fits a straight line, which is a more direct question than the
        // platform's own three-level guess.
    }
}
