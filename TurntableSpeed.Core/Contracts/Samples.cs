namespace TurntableSpeed.Core.Contracts;

/// <summary>
/// A single tri-axial sensor reading. <paramref name="T"/> is the sample timestamp in
/// seconds, taken from the capture clock — never from <c>DateTime.Now</c>.
/// </summary>
public readonly record struct Vector3Sample(double T, double X, double Y, double Z)
{
    public double Magnitude => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double Dot(in Vector3Sample other) => X * other.X + Y * other.Y + Z * other.Z;

    public double Dot(double ux, double uy, double uz) => X * ux + Y * uy + Z * uz;

    public bool IsFinite => double.IsFinite(T) && double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public override string ToString() =>
        $"{T:F6}, {X:F6}, {Y:F6}, {Z:F6}";
}

/// <summary>
/// A contiguous block of mono audio. <paramref name="T"/> is the timestamp of the first
/// sample in the block, in seconds.
/// </summary>
public readonly record struct AudioBlock(double T, ReadOnlyMemory<float> Samples, int SampleRate)
{
    public double Duration => SampleRate > 0 ? Samples.Length / (double)SampleRate : 0.0;

    public double EndTime => T + Duration;
}

/// <summary>Which physical sensor a <see cref="Vector3Sample"/> stream comes from.</summary>
public enum SensorKind
{
    Magnetometer,
    Gyroscope,
    Accelerometer,
}
