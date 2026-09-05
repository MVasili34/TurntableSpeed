using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Estimators;

[Flags]
public enum SensorWarning
{
    None = 0,

    /// <summary>No magnetometer on this device — the reference instrument is missing.</summary>
    NoMagnetometer = 1 << 0,

    NoGyroscope = 1 << 1,

    /// <summary>Zero calibration was never performed, so the gyroscope bias is unknown.</summary>
    GyroZeroNotCalibrated = 1 << 2,

    /// <summary>The phone is not lying flat enough for the accelerometer checks to mean anything.</summary>
    PhoneNotFlat = 1 << 3,

    /// <summary>Magnetometer and gyroscope disagree beyond the threshold — result not trustworthy.</summary>
    MethodsDisagree = 1 << 4,

    /// <summary>Elevated phase residual or outlier rate: something magnetic is nearby.</summary>
    MagneticDisturbance = 1 << 5,

    /// <summary>Not enough full revolutions yet for the magnetometer fit to be meaningful.</summary>
    NotEnoughRevolutions = 1 << 6,

    /// <summary>
    /// Always set in sensor mode. A 150–250 g phone is comparable to the mass of a record, and
    /// on a budget belt drive the extra bearing load can slow the platter by itself. This mode
    /// measures the speed <em>with the phone on the platter</em>, which is not the speed during
    /// playback — hence the acoustic mode, and hence comparing the two.
    /// </summary>
    LoadAffectsMeasurement = 1 << 7,
}

/// <summary>Everything the sensor-mode screen needs, in one immutable snapshot.</summary>
public sealed record SensorFusionResult(
    SpeedEstimate? Primary,
    SpeedEstimate? Magnetometer,
    SpeedEstimate? Gyroscope,
    double GyroScaleFactor,
    bool GyroScaleCalibrated,
    double DisagreementPercent,
    bool IsReliable,
    SensorWarning Warnings,
    double TiltDegrees,
    double? EstimatedRadiusMeters,
    double InstantaneousRadPerSecond,
    WowFlutterResult WowFlutter,
    double? SpinUpSeconds)
{
    public bool HasResult => Primary is not null;
}

public sealed class SensorFusionEstimatorOptions
{
    /// <summary>Disagreement above which the combined result is flagged unreliable, percent.</summary>
    public double DisagreementThresholdPercent { get; set; } = 2.0;

    /// <summary>Magnetometer relative precision required before its speed is used to calibrate the gyroscope.</summary>
    public double CalibrationPrecisionThreshold { get; set; } = 0.002;

    /// <summary>Revolutions the magnetometer must have covered before it may calibrate the gyroscope.</summary>
    public double CalibrationMinimumRevolutions { get; set; } = 5.0;

    /// <summary>Phase residual above which a magnetic disturbance is reported, degrees.</summary>
    public double MagneticDisturbanceResidualDegrees { get; set; } = 8.0;
}

/// <summary>
/// Combines the two sensor estimators. The magnetometer is the reference; the gyroscope's
/// scale factor is derived from it on the fly and shown in diagnostics. The two are never
/// silently averaged — when they disagree, that fact is the output.
/// </summary>
public sealed class SensorFusionEstimator
{
    private readonly SensorFusionEstimatorOptions _options;

    public SensorFusionEstimator(
        SensorFusionEstimatorOptions? options = null,
        MagnetometerSpeedEstimatorOptions? magnetometerOptions = null,
        GyroscopeSpeedEstimatorOptions? gyroscopeOptions = null,
        OrientationMonitorOptions? orientationOptions = null)
    {
        _options = options ?? new SensorFusionEstimatorOptions();
        Magnetometer = new MagnetometerSpeedEstimator(magnetometerOptions);
        Gyroscope = new GyroscopeSpeedEstimator(gyroscopeOptions);
        Orientation = new OrientationMonitor(orientationOptions);
    }

    public MagnetometerSpeedEstimator Magnetometer { get; }

    public GyroscopeSpeedEstimator Gyroscope { get; }

    public OrientationMonitor Orientation { get; }

    /// <summary>Set to false when the device reports no magnetometer, so the UI can explain itself.</summary>
    public bool MagnetometerAvailable { get; set; } = true;

    public bool GyroscopeAvailable { get; set; } = true;

    /// <summary>Set once <see cref="GyroZeroCalibrator"/> has produced a usable bias.</summary>
    public bool GyroZeroCalibrated { get; private set; }

    public SpeedEstimate? Current => Result.Primary;

    public SensorFusionResult Result { get; private set; } = Empty;

    private static SensorFusionResult Empty { get; } = new(
        null, null, null, 1.0, false, double.NaN, false,
        SensorWarning.LoadAffectsMeasurement, double.NaN, null, double.NaN,
        WowFlutterResult.Empty, null);

    public void ApplyGyroBias(GyroBias bias)
    {
        Gyroscope.Bias = bias;
        GyroZeroCalibrated = bias.Count > 0;
    }

    public void PushMagnetometer(Vector3Sample sample)
    {
        Magnetometer.Push(sample);
        Recompute();
    }

    public void PushGyroscope(Vector3Sample sample)
    {
        Gyroscope.Push(sample);
        Recompute();
    }

    public void PushAccelerometer(Vector3Sample sample)
    {
        Orientation.Push(sample);
        Recompute();
    }

    /// <summary>Force both estimators to re-fit and refresh the combined result.</summary>
    public void Flush()
    {
        Magnetometer.Flush();
        Gyroscope.Flush();
        Recompute();
    }

    public void Reset()
    {
        Magnetometer.Reset();
        Gyroscope.Reset();
        Orientation.Reset();
        Gyroscope.ClearScaleCalibration();
        GyroZeroCalibrated = false;
        Result = Empty;
    }

    private void Recompute()
    {
        var magnetometer = Magnetometer.Current;
        var gyroscope = Gyroscope.Current;

        CalibrateGyroScaleFactor(magnetometer);

        var disagreement = ComputeDisagreement(magnetometer, gyroscope);
        var warnings = CollectWarnings(magnetometer, gyroscope, disagreement);
        var reliable = double.IsFinite(disagreement) &&
                       Math.Abs(disagreement) <= _options.DisagreementThresholdPercent;

        var primary = ChoosePrimary(magnetometer, gyroscope, disagreement, reliable);

        Result = new SensorFusionResult(
            primary,
            magnetometer,
            gyroscope,
            Gyroscope.ScaleFactor,
            Gyroscope.IsScaleCalibrated,
            disagreement,
            reliable,
            warnings,
            Orientation.TiltDegrees,
            Orientation.EstimateRadiusMeters(Math.Abs(Magnetometer.SignedRadiansPerSecond)),
            Gyroscope.InstantaneousRadPerSecond,
            Gyroscope.WowFlutter,
            Gyroscope.SpinUpSeconds);
    }

    /// <summary>
    /// k = ω_mag / ω_gyro, computed from the gyroscope's <em>raw</em> rate so the calibration
    /// cannot feed on itself.
    /// </summary>
    private void CalibrateGyroScaleFactor(SpeedEstimate? magnetometer)
    {
        if (magnetometer is null || !(magnetometer.RevolutionsPerMinute > 0.0))
        {
            return;
        }

        if (magnetometer.StandardError / magnetometer.RevolutionsPerMinute > _options.CalibrationPrecisionThreshold)
        {
            return;
        }

        if (!magnetometer.Diagnostics.TryGetValue("revolutions", out var revolutions) ||
            revolutions < _options.CalibrationMinimumRevolutions)
        {
            return;
        }

        var gyroscope = Gyroscope.Current;
        if (gyroscope is null ||
            !gyroscope.Diagnostics.TryGetValue("omegaRawRadPerSecond", out var rawOmega) ||
            !(rawOmega > 0.0))
        {
            return;
        }

        var magOmega = Math.Abs(Magnetometer.SignedRadiansPerSecond);
        if (!(magOmega > 0.0))
        {
            return;
        }

        var scaleFactor = magOmega / rawOmega;

        // A "calibration" outside ±10% is not a scale-factor error, it is a broken measurement.
        if (scaleFactor > 0.9 && scaleFactor < 1.1)
        {
            Gyroscope.ApplyScaleCalibration(scaleFactor);
        }
    }

    private static double ComputeDisagreement(SpeedEstimate? magnetometer, SpeedEstimate? gyroscope)
    {
        if (magnetometer is null || gyroscope is null || !(magnetometer.RevolutionsPerMinute > 0.0))
        {
            return double.NaN;
        }

        // Compare the raw gyroscope reading: comparing the calibrated one would be circular.
        if (!gyroscope.Diagnostics.TryGetValue("omegaRawRadPerSecond", out var rawOmega) || !(rawOmega > 0.0))
        {
            return double.NaN;
        }

        var gyroRpm = SpeedMath.RpmFromRadiansPerSecond(rawOmega);
        return (gyroRpm - magnetometer.RevolutionsPerMinute) / magnetometer.RevolutionsPerMinute * 100.0;
    }

    private SensorWarning CollectWarnings(
        SpeedEstimate? magnetometer,
        SpeedEstimate? gyroscope,
        double disagreement)
    {
        // Always on: this mode measures the platter with a phone sitting on it.
        var warnings = SensorWarning.LoadAffectsMeasurement;

        if (!MagnetometerAvailable)
        {
            warnings |= SensorWarning.NoMagnetometer;
        }

        if (!GyroscopeAvailable)
        {
            warnings |= SensorWarning.NoGyroscope;
        }

        if (GyroscopeAvailable && !GyroZeroCalibrated)
        {
            warnings |= SensorWarning.GyroZeroNotCalibrated;
        }

        if (Orientation.HasData && !Orientation.IsFlat)
        {
            warnings |= SensorWarning.PhoneNotFlat;
        }

        if (magnetometer is not null && gyroscope is not null &&
            double.IsFinite(disagreement) &&
            Math.Abs(disagreement) > _options.DisagreementThresholdPercent)
        {
            warnings |= SensorWarning.MethodsDisagree;
        }

        if (magnetometer is null)
        {
            if (MagnetometerAvailable)
            {
                warnings |= SensorWarning.NotEnoughRevolutions;
            }
        }
        else
        {
            if (magnetometer.Diagnostics.TryGetValue("revolutions", out var revolutions) && revolutions < 3.0)
            {
                warnings |= SensorWarning.NotEnoughRevolutions;
            }

            if (magnetometer.Diagnostics.TryGetValue("phaseResidualDeg", out var residual) &&
                residual > _options.MagneticDisturbanceResidualDegrees)
            {
                warnings |= SensorWarning.MagneticDisturbance;
            }
        }

        return warnings;
    }

    /// <summary>
    /// The magnetometer wins whenever it has an answer. The gyroscope stands in only when
    /// there is no magnetometer at all, and then its scale-factor uncertainty is already
    /// baked into its standard error.
    /// </summary>
    private SpeedEstimate? ChoosePrimary(
        SpeedEstimate? magnetometer,
        SpeedEstimate? gyroscope,
        double disagreement,
        bool reliable)
    {
        var chosen = magnetometer ?? gyroscope;
        if (chosen is null)
        {
            return null;
        }

        var diagnostics = new Dictionary<string, double>(chosen.Diagnostics)
        {
            ["source"] = magnetometer is not null ? 0.0 : 1.0,
            ["gyroScaleFactor"] = Gyroscope.ScaleFactor,
            ["gyroScaleCalibrated"] = Gyroscope.IsScaleCalibrated ? 1.0 : 0.0,
        };

        if (double.IsFinite(disagreement))
        {
            diagnostics["disagreementPercent"] = disagreement;
        }

        if (double.IsFinite(Orientation.TiltDegrees))
        {
            diagnostics["tiltDegrees"] = Orientation.TiltDegrees;
        }

        // Disagreement is information, not something to average away: it lowers confidence and
        // is reported, but the magnetometer reading itself is left untouched.
        var confidence = chosen.Confidence;
        if (magnetometer is not null && gyroscope is not null && !reliable)
        {
            confidence *= 0.4;
        }

        return chosen with { Confidence = confidence, Diagnostics = diagnostics };
    }
}
