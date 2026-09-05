using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.Core.Dsp;

/// <summary>Eigen-decomposition of a symmetric 3×3 matrix, eigenvalues in descending order.</summary>
public readonly record struct Eigen3(
    double Value0,
    double Value1,
    double Value2,
    (double X, double Y, double Z) Vector0,
    (double X, double Y, double Z) Vector1,
    (double X, double Y, double Z) Vector2)
{
    public double this[int index] => index switch
    {
        0 => Value0,
        1 => Value1,
        2 => Value2,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public (double X, double Y, double Z) Vector(int index) => index switch
    {
        0 => Vector0,
        1 => Vector1,
        2 => Vector2,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

/// <summary>Cyclic Jacobi eigensolver for symmetric 3×3 matrices, plus PCA over sample sets.</summary>
public static class SymmetricEigen3
{
    /// <param name="m">Row-major 3×3, assumed symmetric; only the upper triangle is read.</param>
    public static Eigen3 Decompose(double[,] m, int sweeps = 24)
    {
        var a = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                a[i, j] = i <= j ? m[i, j] : m[j, i];
            }
        }

        var v = new double[3, 3];
        v[0, 0] = v[1, 1] = v[2, 2] = 1.0;

        for (var sweep = 0; sweep < sweeps; sweep++)
        {
            var off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
            if (off < 1e-18)
            {
                break;
            }

            for (var p = 0; p < 2; p++)
            {
                for (var q = p + 1; q < 3; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-300)
                    {
                        continue;
                    }

                    var theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                    if (theta == 0.0)
                    {
                        t = 1.0;
                    }

                    var cos = 1.0 / Math.Sqrt(t * t + 1.0);
                    var sin = t * cos;

                    Rotate(a, v, p, q, cos, sin);
                }
            }
        }

        var values = new[] { a[0, 0], a[1, 1], a[2, 2] };
        var order = new[] { 0, 1, 2 };
        Array.Sort(order, (i, j) => values[j].CompareTo(values[i]));

        return new Eigen3(
            values[order[0]],
            values[order[1]],
            values[order[2]],
            Column(v, order[0]),
            Column(v, order[1]),
            Column(v, order[2]));

        static (double X, double Y, double Z) Column(double[,] v, int c) => (v[0, c], v[1, c], v[2, c]);
    }

    private static void Rotate(double[,] a, double[,] v, int p, int q, double cos, double sin)
    {
        var app = a[p, p];
        var aqq = a[q, q];
        var apq = a[p, q];

        a[p, p] = cos * cos * app - 2.0 * sin * cos * apq + sin * sin * aqq;
        a[q, q] = sin * sin * app + 2.0 * sin * cos * apq + cos * cos * aqq;
        a[p, q] = 0.0;
        a[q, p] = 0.0;

        var r = 3 - p - q;
        var apr = p < r ? a[p, r] : a[r, p];
        var aqr = q < r ? a[q, r] : a[r, q];
        var newApr = cos * apr - sin * aqr;
        var newAqr = sin * apr + cos * aqr;

        if (p < r)
        {
            a[p, r] = newApr;
            a[r, p] = newApr;
        }
        else
        {
            a[r, p] = newApr;
            a[p, r] = newApr;
        }

        if (q < r)
        {
            a[q, r] = newAqr;
            a[r, q] = newAqr;
        }
        else
        {
            a[r, q] = newAqr;
            a[q, r] = newAqr;
        }

        for (var i = 0; i < 3; i++)
        {
            var vip = v[i, p];
            var viq = v[i, q];
            v[i, p] = cos * vip - sin * viq;
            v[i, q] = sin * vip + cos * viq;
        }
    }
}

/// <summary>Mean vector and principal axes of a set of tri-axial samples.</summary>
/// <param name="Mean">Centroid — for the magnetometer this is a first guess at the hard-iron offset.</param>
/// <param name="Axes">Eigen-decomposition of the covariance, eigenvalues descending.</param>
public sealed record PrincipalAxesResult(
    (double X, double Y, double Z) Mean,
    Eigen3 Axes,
    int Count)
{
    /// <summary>
    /// The rotation axis: the direction with the least variance, i.e. the normal of the plane
    /// the field vector sweeps out.
    /// </summary>
    public (double X, double Y, double Z) Normal => Axes.Vector2;

    /// <summary>In-plane basis vector 1.</summary>
    public (double X, double Y, double Z) PlaneU => Axes.Vector0;

    /// <summary>In-plane basis vector 2.</summary>
    public (double X, double Y, double Z) PlaneV => Axes.Vector1;

    /// <summary>
    /// How flat the point cloud is: smallest eigenvalue over the largest. Near zero means the
    /// samples really do lie in a plane, which is the precondition for the phase method.
    /// </summary>
    public double Flatness =>
        Axes.Value0 > 0.0 ? Math.Max(0.0, Axes.Value2) / Axes.Value0 : double.NaN;

    /// <summary>Project a sample onto the fitted plane, relative to <see cref="Mean"/>.</summary>
    public (double U, double V) Project(in Vector3Sample sample)
    {
        var dx = sample.X - Mean.X;
        var dy = sample.Y - Mean.Y;
        var dz = sample.Z - Mean.Z;
        var u = dx * PlaneU.X + dy * PlaneU.Y + dz * PlaneU.Z;
        var v = dx * PlaneV.X + dy * PlaneV.Y + dz * PlaneV.Z;
        return (u, v);
    }
}

public static class PrincipalAxes
{
    /// <summary>Covariance PCA over tri-axial samples.</summary>
    public static PrincipalAxesResult? Compute(IReadOnlyList<Vector3Sample> samples)
    {
        var n = samples.Count;
        if (n < 3)
        {
            return null;
        }

        double mx = 0, my = 0, mz = 0;
        foreach (var s in samples)
        {
            mx += s.X;
            my += s.Y;
            mz += s.Z;
        }

        mx /= n;
        my /= n;
        mz /= n;

        var covariance = new double[3, 3];
        foreach (var s in samples)
        {
            var dx = s.X - mx;
            var dy = s.Y - my;
            var dz = s.Z - mz;
            covariance[0, 0] += dx * dx;
            covariance[0, 1] += dx * dy;
            covariance[0, 2] += dx * dz;
            covariance[1, 1] += dy * dy;
            covariance[1, 2] += dy * dz;
            covariance[2, 2] += dz * dz;
        }

        var scale = 1.0 / (n - 1);
        for (var i = 0; i < 3; i++)
        {
            for (var j = i; j < 3; j++)
            {
                covariance[i, j] *= scale;
                covariance[j, i] = covariance[i, j];
            }
        }

        return new PrincipalAxesResult((mx, my, mz), SymmetricEigen3.Decompose(covariance), n);
    }

    /// <summary>
    /// Dominant direction of a set of vectors that are nominally parallel — the gyroscope's
    /// rotation axis. Unlike PCA this keeps the sign, because direction of rotation matters.
    /// </summary>
    public static (double X, double Y, double Z, double Norm) MeanDirection(IReadOnlyList<Vector3Sample> samples)
    {
        double sx = 0, sy = 0, sz = 0;
        foreach (var s in samples)
        {
            sx += s.X;
            sy += s.Y;
            sz += s.Z;
        }

        var n = Math.Max(1, samples.Count);
        sx /= n;
        sy /= n;
        sz /= n;

        var norm = Math.Sqrt(sx * sx + sy * sy + sz * sz);
        if (norm <= 0.0 || !double.IsFinite(norm))
        {
            return (0.0, 0.0, 1.0, 0.0);
        }

        return (sx / norm, sy / norm, sz / norm, norm);
    }
}
