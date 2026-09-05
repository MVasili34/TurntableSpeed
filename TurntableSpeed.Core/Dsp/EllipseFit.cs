using MathNet.Numerics.LinearAlgebra;

namespace TurntableSpeed.Core.Dsp;

/// <summary>Conic coefficients of A·x² + B·x·y + C·y² + D·x + E·y + F = 0.</summary>
public readonly record struct ConicCoefficients(double A, double B, double C, double D, double E, double F)
{
    /// <summary>Negative for an ellipse.</summary>
    public double Discriminant => B * B - 4.0 * A * C;

    public double Evaluate(double x, double y) =>
        A * x * x + B * x * y + C * y * y + D * x + E * y + F;
}

/// <summary>
/// A fitted ellipse in geometric form. For the magnetometer this is the whole hard-iron and
/// soft-iron model: <see cref="CenterX"/>/<see cref="CenterY"/> is the hard-iron offset and
/// the axis ratio is the soft-iron distortion.
/// </summary>
public sealed record EllipseFitResult(
    double CenterX,
    double CenterY,
    double SemiMajor,
    double SemiMinor,
    double Angle,
    double ResidualRms,
    int PointCount)
{
    /// <summary>1.0 for a perfect circle; grows with soft-iron distortion.</summary>
    public double AxisRatio => SemiMinor > 0.0 ? SemiMajor / SemiMinor : double.PositiveInfinity;

    public double Eccentricity =>
        SemiMajor > 0.0 ? Math.Sqrt(Math.Max(0.0, 1.0 - SemiMinor * SemiMinor / (SemiMajor * SemiMajor))) : double.NaN;

    /// <summary>
    /// Map a measured point onto the unit circle: remove the offset, de-rotate, rescale the
    /// axes. Phase taken from the result advances uniformly for a uniformly rotating body,
    /// which is exactly what the linear phase regression assumes.
    /// </summary>
    public (double X, double Y) Normalize(double x, double y)
    {
        var dx = x - CenterX;
        var dy = y - CenterY;
        var cos = Math.Cos(Angle);
        var sin = Math.Sin(Angle);

        // Into the ellipse frame.
        var u = dx * cos + dy * sin;
        var v = -dx * sin + dy * cos;

        if (SemiMajor > 0.0)
        {
            u /= SemiMajor;
        }

        if (SemiMinor > 0.0)
        {
            v /= SemiMinor;
        }

        // Back to the original frame, now circular.
        return (u * cos - v * sin, u * sin + v * cos);
    }

    /// <summary>Phase of a point after soft-iron correction, in (−π, π].</summary>
    public double Phase(double x, double y)
    {
        var (nx, ny) = Normalize(x, y);
        return Math.Atan2(ny, nx);
    }
}

/// <summary>
/// Direct least-squares ellipse fitting, Halíř–Flusser formulation of Fitzgibbon's method.
/// Ellipse-specific by construction, so a partial arc cannot degenerate into a hyperbola.
/// </summary>
public static class EllipseFit
{
    public const int MinimumPoints = 6;

    public static EllipseFitResult? Fit(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys)
    {
        var n = Math.Min(xs.Length, ys.Length);
        if (n < MinimumPoints)
        {
            return null;
        }

        // Normalise for conditioning: x² terms of raw magnetometer values in µT overflow the
        // dynamic range of the scatter matrices badly.
        double meanX = 0.0, meanY = 0.0;
        for (var i = 0; i < n; i++)
        {
            meanX += xs[i];
            meanY += ys[i];
        }

        meanX /= n;
        meanY /= n;

        var scale = 0.0;
        for (var i = 0; i < n; i++)
        {
            var dx = xs[i] - meanX;
            var dy = ys[i] - meanY;
            scale += dx * dx + dy * dy;
        }

        scale = Math.Sqrt(scale / n);
        if (!double.IsFinite(scale) || scale <= 0.0)
        {
            return null;
        }

        var d1 = Matrix<double>.Build.Dense(n, 3);
        var d2 = Matrix<double>.Build.Dense(n, 3);
        for (var i = 0; i < n; i++)
        {
            var x = (xs[i] - meanX) / scale;
            var y = (ys[i] - meanY) / scale;
            d1[i, 0] = x * x;
            d1[i, 1] = x * y;
            d1[i, 2] = y * y;
            d2[i, 0] = x;
            d2[i, 1] = y;
            d2[i, 2] = 1.0;
        }

        var s1 = d1.TransposeThisAndMultiply(d1);
        var s2 = d1.TransposeThisAndMultiply(d2);
        var s3 = d2.TransposeThisAndMultiply(d2);

        Matrix<double> s3Inverse;
        try
        {
            if (Math.Abs(s3.Determinant()) < 1e-14)
            {
                return null;
            }

            s3Inverse = s3.Inverse();
        }
        catch (Exception)
        {
            return null;
        }

        var t = -s3Inverse * s2.Transpose();
        var m = s1 + s2 * t;

        // Pre-multiply by C1⁻¹ for the ellipse constraint 4AC − B² = 1.
        var constrained = Matrix<double>.Build.Dense(3, 3);
        for (var col = 0; col < 3; col++)
        {
            constrained[0, col] = 0.5 * m[2, col];
            constrained[1, col] = -m[1, col];
            constrained[2, col] = 0.5 * m[0, col];
        }

        Vector<double>? solution = null;
        try
        {
            var evd = constrained.Evd();
            for (var i = 0; i < 3; i++)
            {
                if (Math.Abs(evd.EigenValues[i].Imaginary) > 1e-9)
                {
                    continue;
                }

                var v = evd.EigenVectors.Column(i);
                if (4.0 * v[0] * v[2] - v[1] * v[1] > 0.0)
                {
                    solution = v;
                    break;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        if (solution is null)
        {
            return null;
        }

        var a2 = t * solution;
        var conic = new ConicCoefficients(solution[0], solution[1], solution[2], a2[0], a2[1], a2[2]);

        var geometry = FromConic(conic);
        if (geometry is null)
        {
            return null;
        }

        var (cx, cy, semiMajor, semiMinor, angle) = geometry.Value;

        // Undo the normalisation.
        cx = cx * scale + meanX;
        cy = cy * scale + meanY;
        semiMajor *= scale;
        semiMinor *= scale;

        var result = new EllipseFitResult(cx, cy, semiMajor, semiMinor, angle, 0.0, n);

        var residualSum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var (nx, ny) = result.Normalize(xs[i], ys[i]);
            var radius = Math.Sqrt(nx * nx + ny * ny);
            var residual = radius - 1.0;
            residualSum += residual * residual;
        }

        return result with { ResidualRms = Math.Sqrt(residualSum / n) };
    }

    public static EllipseFitResult? Fit(IReadOnlyList<(double X, double Y)> points)
    {
        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].X;
            ys[i] = points[i].Y;
        }

        return Fit(xs, ys);
    }

    /// <summary>Convert conic coefficients to centre, semi-axes and rotation.</summary>
    public static (double CenterX, double CenterY, double SemiMajor, double SemiMinor, double Angle)?
        FromConic(in ConicCoefficients conic)
    {
        var (a, b, c, d, e, f) = (conic.A, conic.B, conic.C, conic.D, conic.E, conic.F);
        var discriminant = b * b - 4.0 * a * c;
        if (!(discriminant < 0.0))
        {
            return null;
        }

        var cx = (2.0 * c * d - b * e) / discriminant;
        var cy = (2.0 * a * e - b * d) / discriminant;

        // Constant term after translating the conic to its centre.
        var fc = f + 0.5 * (d * cx + e * cy);

        // Eigen-decomposition of the quadratic form [[a, b/2], [b/2, c]].
        var half = 0.5 * b;
        var trace = a + c;
        var det = a * c - half * half;
        var root = Math.Sqrt(Math.Max(0.0, trace * trace - 4.0 * det));
        var lambda1 = 0.5 * (trace + root);
        var lambda2 = 0.5 * (trace - root);

        if (lambda1 == 0.0 || lambda2 == 0.0)
        {
            return null;
        }

        var axis1Squared = -fc / lambda1;
        var axis2Squared = -fc / lambda2;
        if (axis1Squared <= 0.0 || axis2Squared <= 0.0)
        {
            return null;
        }

        var axis1 = Math.Sqrt(axis1Squared);
        var axis2 = Math.Sqrt(axis2Squared);

        // Eigenvector of lambda1 (the larger eigenvalue → the shorter axis).
        double vx, vy;
        if (Math.Abs(half) > 1e-300)
        {
            vx = half;
            vy = lambda1 - a;
        }
        else
        {
            // No cross term: the form is diag(a, c), so eigenvalue a belongs to (1, 0) and c to
            // (0, 1). lambda1 is the larger of the two — pick the axis it actually belongs to.
            (vx, vy) = a <= c ? (0.0, 1.0) : (1.0, 0.0);
        }

        var norm = Math.Sqrt(vx * vx + vy * vy);
        if (norm <= 0.0)
        {
            return null;
        }

        vx /= norm;
        vy /= norm;

        // Angle of the major axis: axis2 belongs to lambda2, perpendicular to (vx, vy).
        double semiMajor, semiMinor, angle;
        if (axis2 >= axis1)
        {
            semiMajor = axis2;
            semiMinor = axis1;
            angle = Math.Atan2(vx, -vy);
        }
        else
        {
            semiMajor = axis1;
            semiMinor = axis2;
            angle = Math.Atan2(vy, vx);
        }

        return (cx, cy, semiMajor, semiMinor, PhaseMath.WrapToPi(angle));
    }
}
