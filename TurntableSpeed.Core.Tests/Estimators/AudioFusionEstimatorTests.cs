using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Io;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// Spec §4.5: combine the acoustic methods by their intervals, and show disagreement instead of
/// averaging it away.
/// </summary>
public class AudioFusionEstimatorTests
{
    private static float[] RecordWithClicksAndMusic(
        double periodSeconds,
        double seconds,
        double detuneCents = 0.0,
        double clickAmplitude = 0.5)
    {
        var programme = new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DetuneCents = detuneCents,
            DurationSeconds = seconds,
            Amplitude = 0.25,
        }.Generate();

        return AudioSynth.NormalizePeak(new ClickTrainGenerator
        {
            PeriodSeconds = periodSeconds,
            DurationSeconds = seconds,
            ClickAmplitude = clickAmplitude,
            Programme = programme,
        }.Generate());
    }

    private static AudioFusionEstimator Run(float[] signal, AudioFusionEstimator? fusion = null)
    {
        fusion ??= new AudioFusionEstimator();
        new ReplayAudioSource(signal, 48000).PushAll(fusion.Push);
        fusion.Flush();
        return fusion;
    }

    [Fact]
    public void CombinesTheAcousticMethodsIntoOneReading()
    {
        var fusion = Run(RecordWithClicksAndMusic(1.8, 40.0));

        var result = fusion.Result;

        Assert.NotNull(result.Click);
        Assert.NotNull(result.Pitch);
        TestAssertions.SpeedIs(100.0 / 3.0, result.Primary, Tolerances.ClickRpmPercent, "fused acoustic speed");
        Assert.True(result.IsReliable,
            $"warnings {result.Warnings}, confidence {result.Primary!.Confidence:F3}, " +
            $"disagreement {result.DisagreementPercent:F3}%, input {result.Quality.Warnings}, " +
            $"usable {result.Quality.IsUsable}");
    }

    [Fact]
    public void CombinedIntervalIsNoWorseThanTheBestSingleMethod()
    {
        var fusion = Run(RecordWithClicksAndMusic(1.8, 40.0));

        var result = fusion.Result;

        Assert.True(result.Primary!.StandardError <= result.Click!.StandardError + 1e-12,
            $"combining must not widen the interval: {result.Primary.StandardError:F5} against " +
            $"{result.Click.StandardError:F5}");
    }

    [Fact]
    public void ClickEstimatorSuppliesTheNominalThePitchEstimatorNeeds()
    {
        var fusion = Run(RecordWithClicksAndMusic(60.0 / 45.0, 40.0));

        Assert.Equal(NominalSpeed.Rpm45, fusion.Pitch.AssumedNominal);
        Assert.NotNull(fusion.Result.Pitch);
        Assert.False(fusion.Result.Warnings.HasFlag(AudioWarning.NoNominalYet));
    }

    [Fact]
    public void UserSuppliedNominalOverridesTheClickEstimator()
    {
        var fusion = new AudioFusionEstimator { ManualNominal = NominalSpeed.Rpm45 };
        Run(RecordWithClicksAndMusic(1.8, 20.0), fusion);

        Assert.Equal(NominalSpeed.Rpm45, fusion.Pitch.AssumedNominal);
    }

    [Fact]
    public void PitchReadingsAreAlwaysMarkedAsMixingTheRecordsTuning()
    {
        var fusion = Run(RecordWithClicksAndMusic(1.8, 30.0));

        Assert.NotNull(fusion.Result.Pitch);
        Assert.True(fusion.Result.Warnings.HasFlag(AudioWarning.PitchMixesRecordTuning));
    }

    [Fact]
    public void RecordTunedSharpMakesTheMethodsDisagreeVisibly()
    {
        // The platter is dead on 33⅓ but the record was cut 60 cents sharp. The click estimator
        // is right, the pitch estimator is wrong, and the app must show the conflict rather than
        // splitting the difference.
        var fusion = Run(RecordWithClicksAndMusic(1.8, 40.0, detuneCents: 60.0));

        var result = fusion.Result;

        Assert.True(result.Warnings.HasFlag(AudioWarning.MethodsDisagree),
            $"disagreement was {result.DisagreementPercent:F3}%");
        Assert.False(result.IsReliable);

        // The click estimator's answer survives untouched.
        TestAssertions.SpeedIs(100.0 / 3.0, result.Primary, Tolerances.ClickRpmPercent,
            "speed is still the click estimator's, not a compromise");
    }

    [Fact]
    public void DisagreementCostsConfidence()
    {
        var agreeing = Run(RecordWithClicksAndMusic(1.8, 40.0));
        var arguing = Run(RecordWithClicksAndMusic(1.8, 40.0, detuneCents: 60.0));

        Assert.True(arguing.Result.Primary!.Confidence < agreeing.Result.Primary!.Confidence,
            "a contradicted reading must not look as trustworthy as a corroborated one");
    }

    [Fact]
    public void SpotlessRecordWithNoClicksIsReportedRatherThanGuessedAt()
    {
        // Pure music, no surface noise at all: the primary acoustic method has nothing to work
        // with, and the app should say so.
        var music = new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DurationSeconds = 40.0,
        }.Generate();

        var fusion = Run(music);

        Assert.Null(fusion.Result.Click);
        Assert.True(fusion.Result.Warnings.HasFlag(AudioWarning.NoClickPeriodicity));
    }

    [Fact]
    public void AClickReadingTooWeakToLeadDoesNotBecomeTheAnswer()
    {
        // Leadership belongs to the click method because it measures the platter, not because it
        // cannot be wrong. On a recording with nothing to lock onto it reports an impossible
        // speed at a low confidence, and that must not displace a live pitch reading.
        var fusion = new AudioFusionEstimator(new AudioFusionEstimatorOptions
        {
            MinimumClickConfidence = 0.99,
        })
        {
            ManualNominal = NominalSpeed.Rpm33,
        };

        Run(RecordWithClicksAndMusic(1.8, 40.0), fusion);

        var primary = fusion.Result.Primary;

        Assert.NotNull(fusion.Result.Click);
        Assert.NotNull(primary);
        Assert.Equal(0.0, primary!.Diagnostics["clickCredible"]);
        Assert.Equal(0.0, primary.Diagnostics["usedClick"]);
        Assert.Equal(1.0, primary.Diagnostics["usedPitch"]);
    }

    [Fact]
    public void AClickReadingTooWeakToLeadIsNotTheOnlyAnswerEither()
    {
        // With no nominal pinned there is no pitch reading to fall back on, and a speed nobody
        // believes is worse than no speed: the screen says so instead of showing the number.
        var fusion = new AudioFusionEstimator(new AudioFusionEstimatorOptions
        {
            MinimumClickConfidence = 0.99,
        });

        Run(RecordWithClicksAndMusic(1.8, 40.0), fusion);

        Assert.Null(fusion.Current);
        Assert.False(fusion.Result.IsReliable);
        Assert.True(fusion.Result.Warnings.HasFlag(AudioWarning.NoClickPeriodicity));

        // Still measured, still on screen as the click row and the autocorrelation curve.
        Assert.NotNull(fusion.Result.Click);
    }

    [Fact]
    public void ProcessedInputIsFlaggedThroughToTheAcousticResult()
    {
        var fusion = new AudioFusionEstimator();
        fusion.SetCaptureReport(new AudioCaptureReport(
            48000, true, false, false, false, false, "platform refused the unprocessed source"));

        Run(RecordWithClicksAndMusic(1.8, 20.0), fusion);

        Assert.True(fusion.Result.Warnings.HasFlag(AudioWarning.InputProcessingSuspected));
    }

    [Fact]
    public void AutocorrelationCurveIsCarriedThroughForTheScreen()
    {
        var fusion = Run(RecordWithClicksAndMusic(1.8, 30.0));

        Assert.True(fusion.Result.Autocorrelation.Lags.Length > 100);
        TestAssertions.Close(1.8, fusion.Result.Autocorrelation.PeakLagSeconds, 0.02, "marked peak");
    }

    [Fact]
    public void NothingIsClaimedFromTooLittleAudio()
    {
        var fusion = Run(RecordWithClicksAndMusic(1.8, 3.0));

        Assert.Null(fusion.Current);
        Assert.True(fusion.Result.Warnings.HasFlag(AudioWarning.NotEnoughData));
        Assert.False(fusion.Result.IsReliable);
    }

    [Fact]
    public void ReplayingTheSameRecordingTwiceGivesIdenticalNumbers()
    {
        // Spec §7.5: the pipeline must be deterministic to the bit, or golden tests are worthless.
        var signal = RecordWithClicksAndMusic(1.8, 25.0);

        var first = Run(signal).Current!;
        var second = Run(signal).Current!;

        Assert.Equal(first.RevolutionsPerMinute, second.RevolutionsPerMinute);
        Assert.Equal(first.StandardError, second.StandardError);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Nominal, second.Nominal);
    }

    [Fact]
    public void ResetClearsEverySubEstimator()
    {
        var fusion = Run(RecordWithClicksAndMusic(1.8, 25.0));
        Assert.NotNull(fusion.Current);

        fusion.Reset();

        Assert.Null(fusion.Current);
        Assert.Null(fusion.Click.Current);
        Assert.Null(fusion.Pitch.Current);
        Assert.Equal(AudioInputQuality.Unknown, fusion.Detector.Current);
    }

    [Fact]
    public void WowSpectrumSharesThePitchAnalysisRatherThanRepeatingIt()
    {
        var fusion = new AudioFusionEstimator();

        Assert.Same(fusion.Pitch, fusion.Wow.Pitch);
        Assert.False(fusion.Wow.OwnsPitchEstimator);
    }
}
