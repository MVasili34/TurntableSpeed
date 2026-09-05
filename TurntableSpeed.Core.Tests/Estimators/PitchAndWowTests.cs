using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Io;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// Spec §4.3. Fast, and useful for an instantaneous indication — but it measures the pitch of
/// what was pressed onto the record, which is not the same thing as the speed of the platter.
/// </summary>
public class PitchGridEstimatorTests
{
    private static PitchGridEstimator Run(TonalSignalGenerator generator, NominalSpeed? nominal = null)
    {
        var estimator = new PitchGridEstimator { AssumedNominal = nominal };
        generator.ToSource().PushAll(estimator.Push);
        estimator.Flush();
        return estimator;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(30.0)]
    [InlineData(-25.0)]
    [InlineData(17.2)]
    [InlineData(-45.0)]
    public void RecoversAKnownDetuning(double cents)
    {
        var estimator = Run(new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DetuneCents = cents,
            DurationSeconds = 12.0,
        });

        TestAssertions.CloseModulo(cents, estimator.DeviationCents, 100.0, Tolerances.PitchCents,
            $"detuning of {cents} cents");
        Assert.True(estimator.Resultant > 0.8, $"a clean chord should be concentrated, R was {estimator.Resultant:F3}");
    }

    [Fact]
    public void DetuningByResamplingAgreesWithDetuningByFrequency()
    {
        // Two different ways of being 1% fast: write the frequencies higher, or play the record
        // faster. They must give the same reading, because they are the same thing.
        var written = Run(new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DetuneCents = 1200.0 * Math.Log(1.01, 2.0),
            DurationSeconds = 12.0,
        });

        var played = Run(new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            SpeedFactor = 1.01,
            DurationSeconds = 12.0,
        });

        TestAssertions.CloseModulo(written.DeviationCents, played.DeviationCents, 100.0, Tolerances.PitchCents,
            "resampled versus written detuning");
        TestAssertions.CloseModulo(17.2, played.DeviationCents, 100.0, Tolerances.PitchCents,
            "1% fast is 17.2 cents");
    }

    [Fact]
    public void ProducesNoSpeedWithoutANominalToApplyTheDeviationTo()
    {
        // Pitch alone cannot tell 33⅓ from 45: it measures a ratio, not a speed.
        var estimator = Run(new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DetuneCents = 20.0,
            DurationSeconds = 12.0,
        });

        Assert.Null(estimator.Current);
        Assert.True(double.IsFinite(estimator.DeviationCents), "but the cents indication is still live");
    }

    [Theory]
    [InlineData(NominalSpeed.Rpm33)]
    [InlineData(NominalSpeed.Rpm45)]
    public void AppliesTheDeviationToTheSuppliedNominal(NominalSpeed nominal)
    {
        const double cents = 20.0;
        var estimator = Run(
            new TonalSignalGenerator
            {
                Frequencies = TonalSignalGenerator.AMajorTriad,
                DetuneCents = cents,
                DurationSeconds = 12.0,
            },
            nominal);

        var expected = NominalSpeeds.Rpm(nominal) * (1.0 + SpeedMath.RelativeDeviationFromCents(cents));

        Assert.NotNull(estimator.Current);
        TestAssertions.WithinPercent(expected, estimator.Current!.RevolutionsPerMinute, 0.25,
            $"speed implied by {cents} cents at {nominal}");
    }

    [Fact]
    public void TheRecordsOwnTuningIsCarriedAsAnUncertaintyNotHiddenAway()
    {
        // A band that tuned sharp is indistinguishable from a fast platter. The estimator must
        // not claim a tight interval it cannot justify.
        var estimator = Run(
            new TonalSignalGenerator
            {
                Frequencies = TonalSignalGenerator.AMajorTriad,
                DetuneCents = 0.0,
                DurationSeconds = 12.0,
            },
            NominalSpeed.Rpm33);

        var estimate = estimator.Current!;

        TestAssertions.Close(15.0, estimate.Diagnostics["centsTuningUncertainty"], 1e-9, "declared tuning uncertainty");
        Assert.True(estimate.Diagnostics["centsStatisticalError"] < estimate.Diagnostics["centsStandardError"],
            "the tuning systematic must dominate the statistical error");

        // 15 cents is 0.87%, so the interval is far wider than the click estimator's.
        Assert.True(estimate.StandardErrorPercent > 0.5,
            $"interval was only ±{estimate.StandardErrorPercent:F3}%, which this method cannot justify");
    }

    [Fact]
    public void EveryReadingIsMarkedAsMixingTheRecordsTuning()
    {
        var estimator = Run(
            new TonalSignalGenerator { Frequencies = TonalSignalGenerator.AMajorTriad, DurationSeconds = 12.0 },
            NominalSpeed.Rpm33);

        TestAssertions.Close(1.0, estimator.Current!.Diagnostics["mixesRecordTuning"], 1e-12,
            "the caveat flag must be on every reading");
        Assert.Contains("tuning", PitchGridEstimator.Caveat);
    }

    [Fact]
    public void ConfidenceIsCappedBecauseOfThatSameAmbiguity()
    {
        var estimator = Run(
            new TonalSignalGenerator
            {
                Frequencies = TonalSignalGenerator.AMajorTriad,
                DurationSeconds = 20.0,
                NoiseAmplitude = 0.0,
            },
            NominalSpeed.Rpm33);

        Assert.True(estimator.Current!.Confidence <= 0.5,
            $"however clean the pitch, this method cannot be authoritative: {estimator.Current.Confidence:F3}");
    }

    [Fact]
    public void NoiseWithNoTonalContentProducesNoOpinion()
    {
        var rng = new Random(71);
        var samples = new float[48000 * 10];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        var estimator = new PitchGridEstimator { AssumedNominal = NominalSpeed.Rpm33 };
        new ReplayAudioSource(samples, 48000).PushAll(estimator.Push);
        estimator.Flush();

        Assert.True(estimator.Resultant < 0.5 || estimator.Current!.Confidence < 0.2,
            $"white noise should not look like music: R={estimator.Resultant:F3}");
    }

    [Fact]
    public void ExposesTheLatestSpectrumForDisplay()
    {
        var estimator = Run(new TonalSignalGenerator
        {
            Frequencies = new[] { 440.0 },
            Harmonics = 1,
            DurationSeconds = 5.0,
        });

        var spectrum = estimator.LastSpectrum;

        Assert.True(spectrum.Count > 1000);
        var (frequency, _) = spectrum.PeakInBand(300.0, 600.0);
        TestAssertions.WithinPercent(440.0, frequency, 1.0, "peak of the displayed spectrum");
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var estimator = Run(
            new TonalSignalGenerator { Frequencies = TonalSignalGenerator.AMajorTriad, DurationSeconds = 8.0 },
            NominalSpeed.Rpm33);
        Assert.NotNull(estimator.Current);

        estimator.Reset();

        Assert.Null(estimator.Current);
        Assert.True(double.IsNaN(estimator.DeviationCents));
        Assert.Empty(estimator.Frames);
    }
}

/// <summary>
/// Spec §4.4. Two or three minutes of pitch deviation, de-trended and transformed: eccentricity
/// modulates the pitch exactly once per revolution, so the spectrum carries a line at the
/// rotation frequency.
/// </summary>
public class WowSpectrumEstimatorTests
{
    private static WowSpectrumEstimator Run(double periodSeconds, double depthCents, double seconds)
    {
        var estimator = new WowSpectrumEstimator();

        new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DurationSeconds = seconds,
            VibratoDepthCents = depthCents,
            VibratoFrequencyHz = 1.0 / periodSeconds,
        }.ToSource().PushAll(estimator.Push);

        estimator.Flush();
        return estimator;
    }

    [Theory]
    [InlineData(1.8, 100.0 / 3.0)]
    [InlineData(1.3333333333333333, 45.0)]
    public void FindsTheRotationFrequencyInThePitchModulation(double period, double rpm)
    {
        var estimator = Run(period, depthCents: 12.0, seconds: 100.0);

        var result = estimator.Result;

        Assert.NotNull(result.Speed);
        TestAssertions.WithinPercent(1.0 / period, result.PeakFrequencyHz, Tolerances.WowFrequencyPercent,
            "rotation frequency from the wow spectrum");
        TestAssertions.SpeedIs(rpm, result.Speed, Tolerances.WowFrequencyPercent, "speed from the wow spectrum");
    }

    [Fact]
    public void ReportsTheModulationDepthInPercent()
    {
        const double depthCents = 12.0;
        var estimator = Run(1.8, depthCents, seconds: 100.0);

        // 12 cents is 0.694% of speed.
        var expectedPercent = SpeedMath.PercentFromCents(depthCents);

        TestAssertions.WithinPercent(expectedPercent, estimator.Result.WowFlutter.PeakPercent,
            Tolerances.WowDepthRelative * 100.0, "wow depth");
    }

    [Fact]
    public void SaysNothingUntilItHasEnoughMinutes()
    {
        var estimator = Run(1.8, depthCents: 12.0, seconds: 30.0);

        Assert.Null(estimator.Result.Speed);
        Assert.True(estimator.Progress < 0.5, "and reports how far along it is");
    }

    [Fact]
    public void ProgressTracksTheCaptureLength()
    {
        var estimator = Run(1.8, depthCents: 10.0, seconds: 100.0);

        TestAssertions.Close(100.0 / 180.0, estimator.Progress, 0.05, "progress toward the recommended length");
        Assert.True(estimator.Result.AnalyzedSeconds > 90.0);
    }

    [Fact]
    public void ResolutionSharpensWithCaptureLength()
    {
        var shorter = Run(1.8, 12.0, seconds: 70.0);
        var longer = Run(1.8, 12.0, seconds: 140.0);

        Assert.True(longer.Result.ResolutionHz < shorter.Result.ResolutionHz,
            $"longer capture must resolve better: {longer.Result.ResolutionHz:F5} against " +
            $"{shorter.Result.ResolutionHz:F5} Hz");
        Assert.True(longer.Result.Speed!.StandardError < shorter.Result.Speed!.StandardError);
    }

    [Fact]
    public void ConstantTuningOffsetIsRemovedByDetrendingAndDoesNotDisturbTheSpectrum()
    {
        var estimator = new WowSpectrumEstimator();

        new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DurationSeconds = 100.0,
            DetuneCents = 35.0,          // the record is simply tuned sharp
            VibratoDepthCents = 12.0,
            VibratoFrequencyHz = 1.0 / 1.8,
        }.ToSource().PushAll(estimator.Push);

        estimator.Flush();

        TestAssertions.WithinPercent(1.0 / 1.8, estimator.Result.PeakFrequencyHz, Tolerances.WowFrequencyPercent,
            "rotation frequency is unaffected by the record's tuning");
    }

    [Fact]
    public void CanBeFedACentsSeriesDirectly()
    {
        var estimator = new WowSpectrumEstimator();

        for (var i = 0; i < 20 * 120; i++)
        {
            var t = i / 20.0;
            estimator.PushCents(t, 12.0 * Math.Sin(2.0 * Math.PI * t / 1.8));
        }

        estimator.Flush();

        TestAssertions.WithinPercent(1.0 / 1.8, estimator.Result.PeakFrequencyHz, 0.2,
            "rotation frequency from a directly supplied series");
    }

    [Fact]
    public void ResetClearsTheResult()
    {
        var estimator = Run(1.8, 12.0, seconds: 100.0);
        Assert.NotNull(estimator.Result.Speed);

        estimator.Reset();

        Assert.Null(estimator.Result.Speed);
        TestAssertions.Close(0.0, estimator.Progress, 1e-12, "progress after reset");
    }
}
