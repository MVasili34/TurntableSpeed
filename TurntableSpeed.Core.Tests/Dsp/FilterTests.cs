using TurntableSpeed.Core.Dsp;

namespace TurntableSpeed.Core.Tests.Dsp;

/// <summary>
/// Spec §7.3: check the filters' frequency response by feeding them a set of sines. The
/// analytic <see cref="BiquadCascade.MagnitudeResponse"/> is checked against the measured one,
/// so a mistake in either is caught.
/// </summary>
public class FilterTests
{
    private const double SampleRate = 48000.0;

    /// <summary>Amplitude of a settled sine at the output, measured by quadrature correlation.</summary>
    private static double MeasureGain(BiquadCascade filter, double frequency, double sampleRate = SampleRate)
    {
        filter.Reset();

        var settle = (int)(sampleRate * 0.2);
        var measure = (int)(sampleRate * 0.5);
        var omega = 2.0 * Math.PI * frequency / sampleRate;

        for (var i = 0; i < settle; i++)
        {
            filter.Process(Math.Sin(omega * i));
        }

        double real = 0.0, imaginary = 0.0;
        for (var i = 0; i < measure; i++)
        {
            var n = settle + i;
            var y = filter.Process(Math.Sin(omega * n));
            real += y * Math.Cos(omega * n);
            imaginary += y * Math.Sin(omega * n);
        }

        return 2.0 * Math.Sqrt(real * real + imaginary * imaginary) / measure;
    }

    private static double Db(double gain) => 20.0 * Math.Log10(Math.Max(gain, 1e-300));

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void LowPassIsMinusThreeDecibelsAtItsCutoff(int order)
    {
        const double cutoff = 1000.0;
        var filter = Butterworth.LowPass(cutoff, SampleRate, order);

        var measured = Db(MeasureGain(filter, cutoff));

        TestAssertions.Close(-3.0103, measured, Tolerances.FilterResponseDb, $"order-{order} low-pass at cutoff");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void HighPassIsMinusThreeDecibelsAtItsCutoff(int order)
    {
        const double cutoff = 5000.0;
        var filter = Butterworth.HighPass(cutoff, SampleRate, order);

        var measured = Db(MeasureGain(filter, cutoff));

        TestAssertions.Close(-3.0103, measured, Tolerances.FilterResponseDb, $"order-{order} high-pass at cutoff");
    }

    [Fact]
    public void LowPassPassbandIsFlat()
    {
        var filter = Butterworth.LowPass(1000.0, SampleRate, 4);

        foreach (var frequency in new[] { 20.0, 50.0, 100.0, 200.0 })
        {
            TestAssertions.Close(0.0, Db(MeasureGain(filter, frequency)), Tolerances.FilterResponseDb,
                $"low-pass passband at {frequency} Hz");
        }
    }

    [Fact]
    public void LowPassRollsOffAtSixDecibelsPerOctavePerOrder()
    {
        const int order = 4;

        // Low frequencies relative to Nyquist on purpose: the bilinear transform warps the
        // frequency axis, so a digital Butterworth is genuinely steeper than its analogue
        // prototype near Nyquist. Down here that warping is under half a percent.
        var filter = Butterworth.LowPass(200.0, SampleRate, order);

        var atFour = Db(MeasureGain(filter, 800.0));
        var atEight = Db(MeasureGain(filter, 1600.0));

        TestAssertions.Close(-6.0 * order, atEight - atFour, 0.5, "stopband slope per octave");
    }

    [Fact]
    public void DigitalRollOffSteepensTowardNyquistAsTheBilinearTransformDemands()
    {
        const int order = 4;
        var filter = Butterworth.LowPass(1000.0, SampleRate, order);

        // |H| = 1 / sqrt(1 + (tan(w/2)/tan(w₀/2))^(2N)) — the exact bilinear response. At
        // 8 kHz this is about −27 dB per octave rather than the analogue −24.
        static double Predicted(double frequency, double cutoff, int n, double sampleRate)
        {
            var ratio = Math.Tan(Math.PI * frequency / sampleRate) / Math.Tan(Math.PI * cutoff / sampleRate);
            return -10.0 * Math.Log10(1.0 + Math.Pow(ratio, 2.0 * n));
        }

        foreach (var frequency in new[] { 2000.0, 4000.0, 8000.0 })
        {
            TestAssertions.Close(
                Predicted(frequency, 1000.0, order, SampleRate),
                Db(MeasureGain(filter, frequency)),
                Tolerances.FilterResponseDb,
                $"bilinear response at {frequency} Hz");
        }
    }

    [Fact]
    public void HighPassRejectsTheProgrammeBandTheClickEstimatorDiscards()
    {
        // The click estimator's actual configuration: 5 kHz, 4th order.
        var filter = Butterworth.HighPass(5000.0, SampleRate, 4);

        Assert.True(Db(MeasureGain(filter, 220.0)) < -60.0, "a bass note must be crushed");
        Assert.True(Db(MeasureGain(filter, 1000.0)) < -40.0, "midrange must be well attenuated");
        TestAssertions.Close(0.0, Db(MeasureGain(filter, 15000.0)), Tolerances.FilterResponseDb, "click band");
    }

    [Fact]
    public void BandPassPassesTheMiddleAndRejectsBothSides()
    {
        var filter = Butterworth.BandPass(500.0, 2000.0, SampleRate, 4);

        TestAssertions.Close(0.0, Db(MeasureGain(filter, 1000.0)), 0.6, "band-pass centre");
        Assert.True(Db(MeasureGain(filter, 100.0)) < -30.0, "below the band");
        Assert.True(Db(MeasureGain(filter, 10000.0)) < -30.0, "above the band");
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(1000.0)]
    [InlineData(3000.0)]
    [InlineData(10000.0)]
    public void AnalyticResponseMatchesTheMeasuredOne(double frequency)
    {
        var filter = Butterworth.LowPass(2000.0, SampleRate, 4);

        var analytic = Db(filter.MagnitudeResponse(frequency, SampleRate));
        var measured = Db(MeasureGain(filter, frequency));

        TestAssertions.Close(analytic, measured, Tolerances.FilterResponseDb,
            $"analytic vs measured response at {frequency} Hz");
    }

    [Fact]
    public void ButterworthSectionQMatchesTheKnownValues()
    {
        // A 4th-order Butterworth is two sections with Q = 0.54120 and 1.30656.
        TestAssertions.Close(0.5411961, Butterworth.SectionQ(4, 0), 1e-6, "order-4 section 0 Q");
        TestAssertions.Close(1.3065630, Butterworth.SectionQ(4, 1), 1e-6, "order-4 section 1 Q");

        // A 2nd-order one is a single section with Q = 1/√2.
        TestAssertions.Close(Math.Sqrt(0.5), Butterworth.SectionQ(2, 0), 1e-9, "order-2 Q");
    }

    [Fact]
    public void OddOrdersAreRejectedRatherThanSilentlyRounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Butterworth.LowPass(1000.0, SampleRate, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Butterworth.LowPass(1000.0, SampleRate, 0));
    }

    [Fact]
    public void CutoffOutsideTheNyquistBandIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Butterworth.LowPass(30000.0, SampleRate, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Butterworth.LowPass(0.0, SampleRate, 4));
    }

    [Fact]
    public void ResetClearsTheFilterState()
    {
        var filter = Butterworth.LowPass(1000.0, SampleRate, 4);

        for (var i = 0; i < 1000; i++)
        {
            filter.Process(1.0);
        }

        var settled = filter.Process(0.0);
        filter.Reset();
        var afterReset = filter.Process(0.0);

        Assert.True(Math.Abs(settled) > Math.Abs(afterReset));
        TestAssertions.Close(0.0, afterReset, 1e-12, "output after reset");
    }

    [Fact]
    public void BlockProcessingMatchesSampleBySample()
    {
        var rng = new Random(21);
        var input = new float[5000];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        var reference = Butterworth.HighPass(5000.0, SampleRate, 4);
        var expected = new double[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            expected[i] = reference.Process(input[i]);
        }

        // The same filter fed in ragged blocks must not ring at the boundaries.
        var streaming = Butterworth.HighPass(5000.0, SampleRate, 4);
        var actual = new double[input.Length];
        var offset = 0;
        var blockSizes = new[] { 1, 7, 64, 999, 4096 };
        var next = 0;
        while (offset < input.Length)
        {
            var size = Math.Min(blockSizes[next++ % blockSizes.Length], input.Length - offset);
            streaming.Process(input.AsSpan(offset, size), actual.AsSpan(offset, size));
            offset += size;
        }

        for (var i = 0; i < input.Length; i++)
        {
            TestAssertions.Close(expected[i], actual[i], 1e-12, $"block-boundary continuity at {i}");
        }
    }
}
