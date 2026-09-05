using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>
/// Spec §7.3: feed a parabola with a known vertex. Sub-sample peak location is what turns a
/// 5 ms envelope frame into a 0.1% speed measurement, so its correctness is not cosmetic.
/// </summary>
public class PeakInterpolationTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(-0.25)]
    [InlineData(0.499)]
    [InlineData(-0.499)]
    public void ParabolicRecoversAKnownVertex(double vertex)
    {
        const double height = 7.5;
        const double curvature = 3.0;

        double Sample(double x) => height - curvature * (x - vertex) * (x - vertex);

        var peak = PeakInterpolation.Parabolic(Sample(-1.0), Sample(0.0), Sample(1.0));

        TestAssertions.Close(vertex, peak.Offset, Tolerances.PeakVertex, "vertex offset");
        TestAssertions.Close(height, peak.Value, Tolerances.PeakVertex, "vertex height");
        TestAssertions.Close(curvature, peak.Curvature, Tolerances.PeakVertex, "curvature");
    }

    [Fact]
    public void FlatSamplesDoNotProduceAnOffset()
    {
        var peak = PeakInterpolation.Parabolic(1.0, 1.0, 1.0);

        TestAssertions.Close(0.0, peak.Offset, Tolerances.PeakVertex, "offset of a flat triple");
    }

    [Fact]
    public void RefineMaximumReturnsAFractionalIndex()
    {
        const double vertex = 12.3;
        var values = new double[32];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = 5.0 - 0.5 * (i - vertex) * (i - vertex);
        }

        var (index, peak) = PeakInterpolation.RefineMaximum(values, 0, values.Length - 1);

        TestAssertions.Close(vertex, index, 1e-9, "refined maximum index");
        TestAssertions.Close(5.0, peak.Value, 1e-9, "refined maximum value");
    }

    [Fact]
    public void RefineMaximumStaysInsideTheRequestedRange()
    {
        var values = new double[64];
        values[5] = 10.0;   // the global maximum, outside the search range
        values[40] = 3.0;   // the maximum inside it

        var (index, _) = PeakInterpolation.RefineMaximum(values, 30, 50);

        Assert.InRange(index, 30, 50);
        TestAssertions.Close(40.0, index, 1.0, "in-range maximum");
    }

    [Fact]
    public void RefineMaximumOnEmptyInputReturnsNaN()
    {
        var (index, _) = PeakInterpolation.RefineMaximum([], 0, 10);

        Assert.True(double.IsNaN(index));
    }

    [Fact]
    public void LocalMaximaFindsEveryStrictPeak()
    {
        double[] values = { 0, 1, 0, 2, 1, 5, 4, 4, 9, 0 };

        var maxima = PeakInterpolation.LocalMaxima(values);

        Assert.Equal(new[] { 1, 3, 5, 8 }, maxima);
    }

    [Fact]
    public void LocalMaximaIgnoresTheEndpoints()
    {
        double[] values = { 9, 0, 0, 0, 9 };

        Assert.Empty(PeakInterpolation.LocalMaxima(values));
    }
}
