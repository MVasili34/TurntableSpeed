using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// Spec §4.2, the primary acoustic method. The target is ±0.3% through the air, and a
/// confident 33-or-45 answer within 10–15 seconds.
/// </summary>
public class ClickPeriodicityEstimatorTests
{
    private static ClickPeriodicityEstimator Run(
        ClickTrainGenerator generator,
        ClickPeriodicityEstimator? estimator = null)
    {
        estimator ??= new ClickPeriodicityEstimator();
        var signal = AudioSynth.NormalizePeak(generator.Generate());
        new TurntableSpeed.Core.Io.ReplayAudioSource(signal, generator.SampleRate).PushAll(estimator.Push);
        estimator.Flush();
        return estimator;
    }

    [Theory]
    [InlineData(1.8, 100.0 / 3.0)]
    [InlineData(1.3333333333333333, 45.0)]
    [InlineData(0.7692307692307693, 78.0)]
    public void RecoversEachNominalSpeed(double period, double rpm)
    {
        var estimator = Run(new ClickTrainGenerator { PeriodSeconds = period, DurationSeconds = 30.0 });

        TestAssertions.SpeedIs(rpm, estimator.Current, Tolerances.ClickRpmPercent, $"{rpm} rpm from clicks");
        Assert.NotNull(estimator.Current!.Nominal);
        TestAssertions.Close(rpm, NominalSpeeds.Rpm(estimator.Current.Nominal!.Value), 0.01, "classified nominal");
    }

    [Fact]
    public void MeasuresAPlatterRunningSlow()
    {
        const double truth = 100.0 / 3.0 * 0.985;
        var estimator = Run(new ClickTrainGenerator
        {
            PeriodSeconds = 60.0 / truth,
            DurationSeconds = 30.0,
        });

        TestAssertions.SpeedIs(truth, estimator.Current, Tolerances.ClickRpmPercent, "1.5% slow platter");
        Assert.Equal(NominalSpeed.Rpm33, estimator.Current!.Nominal);
        TestAssertions.Close(-1.5, estimator.Current.DeviationPercent, Tolerances.ClickRpmPercent, "deviation");
    }

    [Fact]
    public void AnswersWithinFifteenSeconds()
    {
        // Spec §4.2: "a confident 33-or-45 answer must be reached in 10–15 s".
        var estimator = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 15.0 });

        TestAssertions.SpeedIs(100.0 / 3.0, estimator.Current, Tolerances.ClickRpmPercent, "speed after 15 s");
        Assert.Equal(NominalSpeed.Rpm33, estimator.Current!.Nominal);
        Assert.True(estimator.Current.Confidence > 0.5,
            $"15 s should be enough to be confident, got {estimator.Current.Confidence:F3}");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SeveralClicksPerRevolutionDoNotBecomeASubMultiple(int clicksPerRevolution)
    {
        // The failure this guards against: reporting 30 rpm for a 45 record because the raw
        // autocorrelation maximum landed on one and a half revolutions.
        var estimator = Run(new ClickTrainGenerator
        {
            PeriodSeconds = 60.0 / 45.0,
            DurationSeconds = 40.0,
            ClicksPerRevolution = clicksPerRevolution,
        });

        TestAssertions.SpeedIs(45.0, estimator.Current, Tolerances.ClickRpmPercent,
            $"45 rpm with {clicksPerRevolution} clicks per revolution");
        Assert.Equal(NominalSpeed.Rpm45, estimator.Current!.Nominal);
    }

    [Fact]
    public void TwoClicksPerRevolutionAtThirtyThreeIsAlsoResolved()
    {
        var estimator = Run(new ClickTrainGenerator
        {
            PeriodSeconds = 1.8,
            DurationSeconds = 40.0,
            ClicksPerRevolution = 2,
        });

        TestAssertions.SpeedIs(100.0 / 3.0, estimator.Current, Tolerances.ClickRpmPercent,
            "33⅓ with two clicks per revolution");
    }

    [Fact]
    public void WorksUnderneathMusic()
    {
        var programme = new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DurationSeconds = 30.0,
            Amplitude = 0.3,
        }.Generate();

        var estimator = Run(new ClickTrainGenerator
        {
            PeriodSeconds = 1.8,
            DurationSeconds = 30.0,
            Programme = programme,
            ProgrammeGain = 1.0,
        });

        TestAssertions.SpeedIs(100.0 / 3.0, estimator.Current, Tolerances.ClickRpmPercent, "speed under music");
    }

    [Fact]
    public void QuietClicksInLoudNoiseStillProduceAnHonestInterval()
    {
        var estimator = Run(new ClickTrainGenerator
        {
            PeriodSeconds = 1.8,
            DurationSeconds = 40.0,
            ClickAmplitude = 0.15,
            PinkNoiseAmplitude = 0.05,
            WhiteNoiseAmplitude = 0.02,
        });

        Assert.NotNull(estimator.Current);
        TestAssertions.SpeedIs(100.0 / 3.0, estimator.Current, Tolerances.ClickRpmPercent, "faint clicks in noise");
    }

    [Fact]
    public void ClicksBuriedInNoiseProduceSilenceRatherThanAConfidentGuess()
    {
        // A peak barely above the autocorrelation background is not a detection. Reporting a
        // period picked out of the noise — with a tight interval around it — would be worse
        // than admitting the recording has nothing to lock onto.
        var estimator = Run(new ClickTrainGenerator
        {
            PeriodSeconds = 1.8,
            DurationSeconds = 40.0,
            ClickAmplitude = 0.02,
            SecondaryClickAmplitude = 0.02,
            PinkNoiseAmplitude = 0.08,
            WhiteNoiseAmplitude = 0.08,
        });

        Assert.True(estimator.Current is null,
            $"a peak this far into the background is not a detection, got " +
            $"{estimator.Current?.RevolutionsPerMinute:F2} rpm at peak-to-noise " +
            $"{estimator.Autocorrelation.PeakToNoise:F1}");
    }

    [Fact]
    public void APeriodIsNotPublishedUntilSuccessiveFitsAgreeOnIt()
    {
        // The failure this guards against: a reading that jumps between speeds no turntable has
        // as the sliding window changes its mind about which bump is tallest. A period that
        // survives a single fit has shown nothing yet.
        var signal = AudioSynth.NormalizePeak(
            new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 30.0 }.Generate());

        var immediate = new ClickPeriodicityEstimator(
            new ClickPeriodicityEstimatorOptions { ConfirmationsRequired = 1 });
        var confirmed = new ClickPeriodicityEstimator(
            new ClickPeriodicityEstimatorOptions { ConfirmationsRequired = 4 });

        // Stop the moment the unguarded estimator first commits to a period.
        var pushed = 0;
        var block = 4096;
        while (pushed < signal.Length && immediate.Current is null)
        {
            var length = Math.Min(block, signal.Length - pushed);
            var audio = new AudioBlock(pushed / 48000.0, new ReadOnlyMemory<float>(signal, pushed, length), 48000);
            immediate.Push(audio);
            confirmed.Push(audio);
            pushed += length;
        }

        Assert.NotNull(immediate.Current);
        Assert.Null(confirmed.Current);

        // And it is a delay, not a refusal: the same signal still gets measured.
        while (pushed < signal.Length)
        {
            var length = Math.Min(block, signal.Length - pushed);
            confirmed.Push(new AudioBlock(pushed / 48000.0, new ReadOnlyMemory<float>(signal, pushed, length), 48000));
            pushed += length;
        }

        confirmed.Flush();
        TestAssertions.SpeedIs(100.0 / 3.0, confirmed.Current, Tolerances.ClickRpmPercent,
            "confirmed period once the capture has run");
    }

    [Fact]
    public void ClickJitterWidensTheIntervalRatherThanBeingIgnored()
    {
        var steady = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 30.0 });
        var jittery = Run(new ClickTrainGenerator
        {
            PeriodSeconds = 1.8,
            DurationSeconds = 30.0,
            JitterSeconds = 0.004,
        });

        Assert.True(jittery.Current!.StandardError > steady.Current!.StandardError,
            $"jitter must widen the interval: {jittery.Current.StandardError:F4} against " +
            $"{steady.Current.StandardError:F4}");
        TestAssertions.IntervalCovers(100.0 / 3.0, jittery.Current, "jittery clicks");
    }

    [Fact]
    public void PrecisionImprovesWithLongerListening()
    {
        var short15 = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 15.0 });
        var long40 = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 40.0 });

        Assert.True(long40.Current!.StandardError < short15.Current!.StandardError,
            $"40 s must beat 15 s: {long40.Current.StandardError:F4} against {short15.Current.StandardError:F4}");
    }

    [Fact]
    public void SaysNothingBeforeItHasHeardEnough()
    {
        var estimator = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 5.0 });

        Assert.Null(estimator.Current);
    }

    [Fact]
    public void ExposesTheAutocorrelationCurveWithItsPeakMarked()
    {
        // Spec §6 asks for this on the acoustic screen, and it is the most honest thing the
        // mode can show: either there is a spike once per revolution or there is not.
        var estimator = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 30.0 });

        var view = estimator.Autocorrelation;

        Assert.True(view.Lags.Length > 100, "the curve should be drawable");
        Assert.Equal(view.Lags.Length, view.Values.Length);
        TestAssertions.Close(0.6, view.Lags[0], 0.02, "curve starts at the shortest lag searched");
        TestAssertions.Close(2.2, view.Lags[^1], 0.02, "curve ends at the longest lag searched");
        TestAssertions.Close(1.8, view.PeakLagSeconds, 0.02, "marked peak");
        Assert.True(view.PeakToNoise > 4.0, $"peak-to-noise was only {view.PeakToNoise:F1}");
    }

    [Fact]
    public void PeriodAndDiagnosticsAgreeWithTheReportedSpeed()
    {
        var estimator = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 30.0 });

        var estimate = estimator.Current!;

        TestAssertions.Close(estimator.PeriodSeconds, estimate.Diagnostics["periodSeconds"], 1e-12,
            "period in diagnostics");
        TestAssertions.Close(60.0 / estimator.PeriodSeconds, estimate.RevolutionsPerMinute, 1e-9,
            "speed derived from the period");
        Assert.True(estimate.Diagnostics["revolutions"] > 15.0, "revolutions covered");
    }

    [Fact]
    public void SilenceProducesNothingRatherThanANumber()
    {
        var estimator = new ClickPeriodicityEstimator();
        var silence = new float[48000 * 20];

        new TurntableSpeed.Core.Io.ReplayAudioSource(silence, 48000).PushAll(estimator.Push);
        estimator.Flush();

        Assert.Null(estimator.Current);
    }

    [Fact]
    public void ResetReturnsTheEstimatorToItsInitialState()
    {
        var estimator = Run(new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 20.0 });
        Assert.NotNull(estimator.Current);

        estimator.Reset();

        Assert.Null(estimator.Current);
        Assert.True(double.IsNaN(estimator.PeriodSeconds));
        Assert.Empty(estimator.Autocorrelation.Lags);
        Assert.Equal(0, estimator.EnvelopeSampleCount);
    }

    [Fact]
    public void RaggedBlockSizesGiveTheSameAnswerAsRegularOnes()
    {
        var signal = new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 25.0 }.Generate();

        var regular = new ClickPeriodicityEstimator();
        new TurntableSpeed.Core.Io.ReplayAudioSource(signal, 48000, 4096).PushAll(regular.Push);
        regular.Flush();

        var ragged = new ClickPeriodicityEstimator();
        var offset = 0;
        var sizes = new[] { 1024, 4096, 512, 8192, 333 };
        var next = 0;
        while (offset < signal.Length)
        {
            var size = Math.Min(sizes[next++ % sizes.Length], signal.Length - offset);
            ragged.Push(new AudioBlock(offset / 48000.0, new ReadOnlyMemory<float>(signal, offset, size), 48000));
            offset += size;
        }

        ragged.Flush();

        TestAssertions.WithinPercent(regular.Current!.RevolutionsPerMinute, ragged.Current!.RevolutionsPerMinute,
            0.05, "block size must not change the answer");
    }
}
