using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.Core.Estimators;

public sealed class OrientationMonitorOptions
{
    /// <summary>Time constant of the gravity low-pass, seconds.</summary>
    public double GravityTimeConstant { get; set; } = 0.7;

    /// <summary>Beyond this tilt the phone is not lying flat enough, degrees.</summary>
    public double FlatToleranceDegrees { get; set; } = 5.0;

    /// <summary>Nominal gravity, m/s². Used only for the plausibility check.</summary>
    public double StandardGravity { get; set; } = 9.80665;
}

/// <summary>
/// Watches the accelerometer for two things the spec asks for: that the phone is actually
/// lying flat, and — as a coarse sanity check only — the centripetal acceleration, from which
/// the placement radius can be guessed. The radius never enters the speed estimate: it is
/// unknown, and a = ω²·r has two unknowns.
/// </summary>
public sealed class OrientationMonitor
{
    private readonly OrientationMonitorOptions _options;
    private double _gx, _gy, _gz;
    private bool _initialized;
    private double _lastTimestamp = double.NegativeInfinity;

    public OrientationMonitor(OrientationMonitorOptions? options = null)
    {
        _options = options ?? new OrientationMonitorOptions();
        GravityReference = _options.StandardGravity;
    }

    public bool HasData => _initialized;

    public long SampleCount { get; private set; }

    /// <summary>Low-passed acceleration vector: gravity plus the (constant) centripetal term.</summary>
    public (double X, double Y, double Z) LowPassed => (_gx, _gy, _gz);

    /// <summary>Magnitude of the low-passed vector, m/s².</summary>
    public double Magnitude => Math.Sqrt(_gx * _gx + _gy * _gy + _gz * _gz);

    /// <summary>
    /// Gravity magnitude used as the reference for <see cref="TiltDegrees"/>. Defaults to
    /// standard gravity; call <see cref="CaptureGravityReference"/> during the still period the
    /// zero calibration already asks for to replace it with this device's own reading, which
    /// removes the accelerometer's factory scale error from the tilt.
    /// </summary>
    public double GravityReference { get; private set; }

    /// <summary>
    /// Tilt of the phone away from horizontal, degrees, from the vertical component alone:
    /// a_z = g·cos θ.
    /// <para>
    /// Deliberately not the angle to the low-passed vector. A phone lying perfectly flat but
    /// off-centre also reads a centripetal term a = ω²·r in its own plane — 5.3 m/s² at 78 rpm
    /// eight centimetres out, which would look like a 28° tilt and raise a "phone not flat"
    /// warning on a correctly placed phone. The centripetal term lies in the platter plane and
    /// so leaves a_z untouched, which is why the vertical component is the honest measure.
    /// </para>
    /// </summary>
    public double TiltDegrees
    {
        get
        {
            if (!_initialized || !(GravityReference > 0.0))
            {
                return double.NaN;
            }

            var cos = Math.Clamp(_gz / GravityReference, -1.0, 1.0);
            return Math.Acos(cos) * 180.0 / Math.PI;
        }
    }

    /// <summary>
    /// Angle between the phone's +Z axis and the low-passed acceleration, degrees. Includes the
    /// centripetal term, so it exceeds <see cref="TiltDegrees"/> on a spinning platter; the
    /// difference between the two is itself a diagnostic of off-centre placement.
    /// </summary>
    public double ApparentTiltDegrees
    {
        get
        {
            var magnitude = Magnitude;
            if (!_initialized || magnitude <= 0.0)
            {
                return double.NaN;
            }

            return Math.Acos(Math.Clamp(_gz / magnitude, -1.0, 1.0)) * 180.0 / Math.PI;
        }
    }

    /// <summary>
    /// Adopt the current low-passed magnitude as this device's gravity. Valid only while the
    /// platter is stopped — there is no centripetal term to contaminate it then — which is
    /// exactly when the gyroscope zero calibration is being taken.
    /// </summary>
    public bool CaptureGravityReference()
    {
        var magnitude = Magnitude;
        if (!_initialized || !double.IsFinite(magnitude) ||
            Math.Abs(magnitude - _options.StandardGravity) > 1.5)
        {
            return false;
        }

        GravityReference = magnitude;
        return true;
    }

    public bool IsFlat
    {
        get
        {
            var tilt = TiltDegrees;
            return double.IsFinite(tilt) && tilt <= _options.FlatToleranceDegrees;
        }
    }

    /// <summary>
    /// In-plane component of the low-passed acceleration, m/s². With the phone flat this is
    /// the centripetal acceleration; with the phone tilted it is contaminated by gravity,
    /// which is precisely why <see cref="IsFlat"/> must be checked first.
    /// </summary>
    public double HorizontalAcceleration => Math.Sqrt(_gx * _gx + _gy * _gy);

    /// <summary>
    /// The vertical reading is consistent with the phone resting on something. Below the band
    /// the sensor is faulty or the phone is falling; above it the phone is being handled. The
    /// centripetal term does not enter, so this stays valid while the platter turns.
    /// </summary>
    public bool IsGravityPlausible =>
        _initialized && Math.Abs(_gz - GravityReference) < 1.5;

    public void Push(Vector3Sample sample)
    {
        if (!sample.IsFinite || sample.T <= _lastTimestamp)
        {
            return;
        }

        var previous = _lastTimestamp;
        _lastTimestamp = sample.T;
        SampleCount++;

        if (!_initialized)
        {
            (_gx, _gy, _gz) = (sample.X, sample.Y, sample.Z);
            _initialized = true;
            return;
        }

        var dt = sample.T - previous;
        var alpha = dt > 0.0 ? 1.0 - Math.Exp(-dt / _options.GravityTimeConstant) : 1.0;
        _gx += alpha * (sample.X - _gx);
        _gy += alpha * (sample.Y - _gy);
        _gz += alpha * (sample.Z - _gz);
    }

    /// <summary>
    /// Placement radius implied by a = ω²·r, in metres. Coarse: valid only when the phone is
    /// flat, and always to be shown as a sanity indicator rather than a measurement.
    /// </summary>
    public double? EstimateRadiusMeters(double radiansPerSecond)
    {
        if (!_initialized || !IsFlat || !(radiansPerSecond > 0.0) || !double.IsFinite(radiansPerSecond))
        {
            return null;
        }

        var radius = HorizontalAcceleration / (radiansPerSecond * radiansPerSecond);
        return double.IsFinite(radius) && radius >= 0.0 && radius < 0.5 ? radius : null;
    }

    public void Reset()
    {
        _initialized = false;
        _gx = _gy = _gz = 0.0;
        SampleCount = 0;
        _lastTimestamp = double.NegativeInfinity;
    }
}
