using Android.Content;
using Android.Hardware;
using TurntableSpeed.App.Localization;
using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.App.Capture;

/// <summary>
/// The three sensors §3 needs. Any of them may be absent, which is a supported outcome rather
/// than a failure: the UI degrades and says what is missing.
/// </summary>
public sealed class AndroidSensorSuite : ISensorSuite
{
    /// <summary>
    /// 100 Hz requested for all three. The magnetometer usually caps out around 50–100 Hz and
    /// the request is a hint, not a promise — the estimators read the achieved rate back out of
    /// the timestamps.
    /// </summary>
    public const double RequestedRateHz = 100.0;

    public AndroidSensorSuite(Context context)
    {
        var manager = (SensorManager?)context.GetSystemService(Context.SensorService);
        if (manager is null)
        {
            return;
        }

        Manager = manager;

        // Uncalibrated in preference to calibrated: Android's own hard-iron correction is
        // re-estimated as you move and can step discontinuously, which puts a jump straight
        // into a phase ramp. The core fits its own hard/soft-iron model anyway (§3.2), so the
        // rawer stream is the better input.
        Magnetometer =
            Create(manager, SensorType.MagneticFieldUncalibrated, SensorKind.Magnetometer) ??
            Create(manager, SensorType.MagneticField, SensorKind.Magnetometer);

        Gyroscope =
            Create(manager, SensorType.GyroscopeUncalibrated, SensorKind.Gyroscope) ??
            Create(manager, SensorType.Gyroscope, SensorKind.Gyroscope);

        Accelerometer = Create(manager, SensorType.Accelerometer, SensorKind.Accelerometer);
    }

    public SensorManager? Manager { get; }

    public IVector3SampleSource? Magnetometer { get; }

    public IVector3SampleSource? Gyroscope { get; }

    public IVector3SampleSource? Accelerometer { get; }

    public string Describe()
    {
        if (Manager is null)
        {
            return AppStrings.AndroidSensorsNoAccess;
        }

        var parts = new List<string>(3)
        {
            Present(AppStrings.AndroidSensorMagnetometer, Magnetometer),
            Present(AppStrings.AndroidSensorGyroscope, Gyroscope),
            Present(AppStrings.AndroidSensorAccelerometer, Accelerometer),
        };

        return string.Join(", ", parts);

        static string Present(string format, IVector3SampleSource? sensor) =>
            Tr.Format(format, sensor is null ? AppStrings.AnswerAbsent : AppStrings.AnswerPresent);
    }

    private static AndroidVector3Source? Create(SensorManager manager, SensorType type, SensorKind kind)
    {
        var sensor = manager.GetDefaultSensor(type);
        return sensor is null ? null : new AndroidVector3Source(manager, sensor, kind, RequestedRateHz);
    }
}
