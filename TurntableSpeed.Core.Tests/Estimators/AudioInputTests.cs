using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Io;

namespace TurntableSpeed.Core.Tests.Estimators;

/// <summary>
/// Spec §4.1. Without an unprocessed input the acoustic mode does not work at all, and the
/// platform is free to ignore the request — so the app has to catch it from the signal itself.
/// </summary>
public class AudioProcessingDetectorTests
{
    private const int SampleRate = 48000;

    private static AudioInputQuality Analyze(Func<int, double, double> generator, double seconds = 10.0)
    {
        var n = (int)(seconds * SampleRate);
        var samples = new float[n];
        for (var i = 0; i < n; i++)
        {
            samples[i] = (float)generator(i, i / (double)SampleRate);
        }

        var detector = new AudioProcessingDetector();
        new ReplayAudioSource(samples, SampleRate).PushAll(detector.Push);
        return detector.Current;
    }

    /// <summary>Dynamic material: level redrawn every 20 ms, with a slow swell on top.</summary>
    private static Func<int, double, double> Programme(int seed, bool withSwell = true, double gain = 0.25)
    {
        var rng = new Random(seed);
        var frame = -1;
        var level = 1.0;

        return (i, t) =>
        {
            var current = i / (SampleRate / 50);
            if (current != frame)
            {
                frame = current;
                level = 0.05 + 0.95 * rng.NextDouble();
            }

            var swell = withSwell ? 0.05 + 0.95 * (0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 0.15 * t)) : 1.0;
            return gain * level * swell * Math.Sin(2.0 * Math.PI * 220.0 * t) + 0.0008 * (rng.NextDouble() * 2 - 1);
        };
    }

    [Fact]
    public void CleanCaptureRaisesNothing()
    {
        var quality = Analyze(Programme(1));

        Assert.Equal(AudioInputWarning.None, quality.Warnings);
        Assert.True(quality.IsClean);
        Assert.True(quality.IsUsable);
        Assert.Equal(SampleRate, quality.SampleRate);
    }

    [Fact]
    public void NoiseGateIsDetectedFromTheSilenceItLeaves()
    {
        var programme = Programme(2);
        var quality = Analyze((i, t) =>
        {
            var value = programme(i, t);
            // A gate slamming shut in the quiet passages.
            return Math.Abs(value) < 0.02 ? 0.0 : value;
        });

        Assert.True(quality.Warnings.HasFlag(AudioInputWarning.NoiseGateSuspected),
            $"expected a gate warning, got {quality.Warnings} (silent frames {quality.SilentFrameFraction:P1}, " +
            $"floor {quality.NoiseFloorDbfs:F1} dBFS)");
        Assert.False(quality.IsUsable);
    }

    /// <summary>Normalise every one-second block to a fixed RMS — what an AGC actually does.</summary>
    private static float[] ApplyAutomaticGainControl(float[] samples, double targetRms = 0.1)
    {
        var result = (float[])samples.Clone();
        var block = SampleRate;

        for (var start = 0; start < result.Length; start += block)
        {
            var length = Math.Min(block, result.Length - start);
            var sumSquares = 0.0;
            for (var i = 0; i < length; i++)
            {
                sumSquares += (double)result[start + i] * result[start + i];
            }

            var rms = Math.Sqrt(sumSquares / length);
            if (rms <= 1e-9)
            {
                continue;
            }

            var gain = targetRms / rms;
            for (var i = 0; i < length; i++)
            {
                result[start + i] = (float)(result[start + i] * gain);
            }
        }

        return result;
    }

    private static float[] Render(Func<int, double, double> generator, double seconds)
    {
        var n = (int)(seconds * SampleRate);
        var samples = new float[n];
        for (var i = 0; i < n; i++)
        {
            samples[i] = (float)generator(i, i / (double)SampleRate);
        }

        return samples;
    }

    private static AudioInputQuality Analyze(float[] samples)
    {
        var detector = new AudioProcessingDetector();
        new ReplayAudioSource(samples, SampleRate).PushAll(detector.Push);
        return detector.Current;
    }

    [Fact]
    public void AutomaticGainControlIsDetectedFromTheFlattenedSlowLevel()
    {
        // The material has dynamics at both scales; the AGC removes them only at the slow one,
        // which is exactly the signature the detector looks for.
        var raw = Render(Programme(3), seconds: 12.0);
        var quality = Analyze(ApplyAutomaticGainControl(raw));

        Assert.True(quality.Warnings.HasFlag(AudioInputWarning.AutomaticGainControlSuspected),
            $"expected an AGC warning, got {quality.Warnings} (slow σ {quality.SlowLevelStdDb:F2} dB, " +
            $"frame σ {quality.FrameLevelStdDb:F2} dB)");
    }

    [Fact]
    public void TheSameMaterialWithoutAgcIsNotAccused()
    {
        var quality = Analyze(Render(Programme(3), seconds: 12.0));

        Assert.False(quality.Warnings.HasFlag(AudioInputWarning.AutomaticGainControlSuspected),
            $"a dynamic recording must not be called compressed (slow σ {quality.SlowLevelStdDb:F2} dB, " +
            $"frame σ {quality.FrameLevelStdDb:F2} dB)");
    }

    [Fact]
    public void ClippingIsDetected()
    {
        var quality = Analyze((_, t) => Math.Clamp(2.5 * Math.Sin(2.0 * Math.PI * 220.0 * t), -1.0, 1.0));

        Assert.True(quality.Warnings.HasFlag(AudioInputWarning.Clipping));
        Assert.False(quality.IsUsable);
        Assert.True(quality.ClippedSampleFraction > 0.1);
    }

    [Fact]
    public void SignalTooQuietIsDetected()
    {
        var programme = Programme(4);
        var quality = Analyze((i, t) => programme(i, t) * 0.002);

        Assert.True(quality.Warnings.HasFlag(AudioInputWarning.SignalTooQuiet),
            $"RMS was {quality.RmsDbfs:F1} dBFS");
        Assert.False(quality.IsUsable);
    }

    [Fact]
    public void DcOffsetIsDetected()
    {
        var programme = Programme(5);
        var quality = Analyze((i, t) => programme(i, t) + 0.05);

        Assert.True(quality.Warnings.HasFlag(AudioInputWarning.DcOffset));
        TestAssertions.Close(0.05, quality.DcOffset, 0.005, "measured DC offset");
    }

    [Fact]
    public void PlatformAdmissionIsCarriedThrough()
    {
        var detector = new AudioProcessingDetector();
        var samples = new float[SampleRate * 5];
        var programme = Programme(6);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)programme(i, i / (double)SampleRate);
        }

        detector.SetCaptureReport(new AudioCaptureReport(
            SampleRate,
            UnprocessedSourceRequested: true,
            UnprocessedSourceGranted: false,
            NoiseSuppressorDisabled: true,
            AutomaticGainControlDisabled: true,
            AcousticEchoCancelerDisabled: true,
            "device does not support the unprocessed source"));

        new ReplayAudioSource(samples, SampleRate).PushAll(detector.Push);

        Assert.True(detector.Current.Warnings.HasFlag(AudioInputWarning.ProcessingNotDisabled));

        // But a signal that still looks fine is not condemned outright.
        Assert.True(detector.Current.IsUsable);
    }

    [Fact]
    public void UnexpectedSampleRateIsReported()
    {
        var detector = new AudioProcessingDetector();
        var samples = new float[16000 * 5];
        var programme = Programme(7);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)programme(i, i / 16000.0);
        }

        new ReplayAudioSource(samples, 16000).PushAll(detector.Push);

        Assert.True(detector.Current.Warnings.HasFlag(AudioInputWarning.UnexpectedSampleRate));
    }

    [Fact]
    public void NotEnoughDataIsSaidPlainlyRatherThanGuessed()
    {
        var detector = new AudioProcessingDetector();
        var samples = new float[SampleRate / 2];

        new ReplayAudioSource(samples, SampleRate).PushAll(detector.Push);

        Assert.True(detector.Current.Warnings.HasFlag(AudioInputWarning.NotEnoughData));
        Assert.False(detector.Current.IsUsable);
    }

    [Fact]
    public void LevelFramesAreExposedForTheInputMeter()
    {
        var detector = new AudioProcessingDetector();
        var samples = new float[SampleRate * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(0.1 * Math.Sin(2.0 * Math.PI * 440.0 * i / SampleRate));
        }

        new ReplayAudioSource(samples, SampleRate).PushAll(detector.Push);

        Assert.True(detector.Frames.Count > 100);
        TestAssertions.Close(0.1 / Math.Sqrt(2.0), detector.Frames[50].Rms, 0.005, "frame RMS of a sine");
        TestAssertions.Close(0.1, detector.Frames[50].Peak, 0.005, "frame peak of a sine");
    }

    [Fact]
    public void ResetClearsTheVerdict()
    {
        var detector = new AudioProcessingDetector();
        var samples = new float[SampleRate * 5];
        var programme = Programme(8);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)programme(i, i / (double)SampleRate);
        }

        new ReplayAudioSource(samples, SampleRate).PushAll(detector.Push);
        Assert.NotEqual(AudioInputQuality.Unknown, detector.Current);

        detector.Reset();

        Assert.Equal(AudioInputQuality.Unknown, detector.Current);
        Assert.Empty(detector.Frames);
    }
}
