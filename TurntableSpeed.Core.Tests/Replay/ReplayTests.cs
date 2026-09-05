using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Io;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Replay;

/// <summary>
/// Spec §7.5: replay reads a fixture and delivers samples with their original timestamps but
/// without the original delays, so a two-minute recording runs through the pipeline in
/// milliseconds — and does so identically every time.
/// </summary>
public class ReplayTests
{
    [Fact]
    public void WavRoundTripsThroughSixteenBitPcm()
    {
        var original = new float[10_000];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (float)(0.8 * Math.Sin(2.0 * Math.PI * 440.0 * i / 48000.0));
        }

        using var stream = new MemoryStream();
        WavIo.Write(stream, original, 48000);
        stream.Position = 0;
        var read = WavIo.Read(stream);

        Assert.Equal(48000, read.SampleRate);
        Assert.Equal(1, read.SourceChannels);
        Assert.Equal(16, read.SourceBitsPerSample);
        Assert.Equal(original.Length, read.Samples.Length);

        for (var i = 0; i < original.Length; i++)
        {
            // One quantisation step of 16-bit audio.
            TestAssertions.Close(original[i], read.Samples[i], 1.0 / 32767.0, $"sample {i}");
        }
    }

    [Fact]
    public void WavRoundTripsExactlyThroughThirtyTwoBitFloat()
    {
        var rng = new Random(83);
        var original = new float[5_000];
        for (var i = 0; i < original.Length; i++)
        {
            original[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }

        using var stream = new MemoryStream();
        WavIo.Write(stream, original, 44100, bitsPerSample: 32);
        stream.Position = 0;
        var read = WavIo.Read(stream);

        Assert.Equal(44100, read.SampleRate);
        Assert.Equal(original, read.Samples);
    }

    [Fact]
    public void WavDurationMatchesTheSampleCount()
    {
        using var stream = new MemoryStream();
        WavIo.Write(stream, new float[48000], 48000);
        stream.Position = 0;

        TestAssertions.Close(1.0, WavIo.Read(stream).Duration, 1e-12, "duration of one second of audio");
    }

    [Fact]
    public void MalformedWavIsRejectedRatherThanMisread()
    {
        using var stream = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 });

        Assert.Throws<InvalidDataException>(() => WavIo.Read(stream));
    }

    [Fact]
    public void SensorCsvRoundTripsExactly()
    {
        var samples = new MagnetometerSignalGenerator { DurationSeconds = 5.0 }.Generate();

        var csv = SensorCsvIo.WriteString(samples, "magnetometer, synthetic");
        var read = SensorCsvIo.ReadString(csv);

        Assert.Equal(samples.Length, read.Count);
        for (var i = 0; i < samples.Length; i++)
        {
            // Round-trip formatting: a fixture that does not read back bit-for-bit would make a
            // golden test lie about a regression.
            Assert.Equal(samples[i], read[i]);
        }
    }

    [Fact]
    public void SensorCsvSkipsCommentsAndBlankLines()
    {
        const string csv = """
                           # captured on a Technics SL-1200
                           # 33 rpm, phone at 7 cm

                           t,x,y,z
                           0,1.5,-2.5,45

                           0.02,1.6,-2.4,45.1
                           """;

        var samples = SensorCsvIo.ReadString(csv);

        Assert.Equal(2, samples.Count);
        Assert.Equal(new Vector3Sample(0.0, 1.5, -2.5, 45.0), samples[0]);
        Assert.Equal(new Vector3Sample(0.02, 1.6, -2.4, 45.1), samples[1]);
    }

    [Fact]
    public void MalformedCsvRowThrowsRatherThanBeingSilentlyDropped()
    {
        const string csv = "t,x,y,z\n0,1,2,3\n0.02,4,5\n";

        Assert.Throws<InvalidDataException>(() => SensorCsvIo.ReadString(csv));
    }

    [Fact]
    public void ReplayDeliversTheOriginalTimestamps()
    {
        var samples = new MagnetometerSignalGenerator { DurationSeconds = 2.0, SampleRateHz = 50.0 }.Generate();
        var source = new ReplayVector3Source(samples, SensorKind.Magnetometer);

        var delivered = new List<Vector3Sample>();
        source.PushAll(delivered.Add);

        Assert.Equal(samples.Length, delivered.Count);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], delivered[i]);
        }

        TestAssertions.Close(50.0, source.NominalRateHz, 0.1, "recovered nominal rate");
    }

    [Fact]
    public async Task ReplayIsEnumerableAsynchronouslyWithoutRealDelays()
    {
        var samples = new MagnetometerSignalGenerator { DurationSeconds = 60.0, SampleRateHz = 50.0 }.Generate();
        var source = new ReplayVector3Source(samples, SensorKind.Magnetometer);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        await foreach (var _ in source.ReadAsync(CancellationToken.None))
        {
            count++;
        }

        started.Stop();

        Assert.Equal(samples.Length, count);
        Assert.True(started.ElapsedMilliseconds < 2000,
            $"a minute of data must replay in milliseconds, took {started.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AudioReplayDerivesBlockTimestampsFromTheSampleIndex()
    {
        var signal = new float[48000 * 3];
        var source = new ReplayAudioSource(signal, 48000, blockSize: 4096);

        var blocks = source.Blocks().ToList();

        Assert.Equal(36, blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            TestAssertions.Close(i * 4096 / 48000.0, blocks[i].T, 1e-12, $"timestamp of block {i}");
        }

        // Contiguous: every block starts where the previous one ended.
        for (var i = 1; i < blocks.Count; i++)
        {
            TestAssertions.Close(blocks[i - 1].EndTime, blocks[i].T, 1e-12, $"continuity at block {i}");
        }
    }

    [Fact]
    public void AudioReplayCoversEverySampleExactlyOnce()
    {
        var signal = new float[10_000];
        for (var i = 0; i < signal.Length; i++)
        {
            signal[i] = i;
        }

        var seen = new List<float>();
        new ReplayAudioSource(signal, 48000, blockSize: 333).PushAll(b => seen.AddRange(b.Samples.ToArray()));

        Assert.Equal(signal, seen.ToArray());
    }

    [Fact]
    public void ReplayReportsThatNoPlatformProcessingIsInThePath()
    {
        var source = new ReplayAudioSource(new float[1000], 48000);

        Assert.True(source.Report.IsClean);
        Assert.Contains("replayed", source.Report.Details);
    }

    [Fact]
    public void SensorPipelineIsBitForBitDeterministicAcrossRuns()
    {
        var samples = new MagnetometerSignalGenerator { DurationSeconds = 60.0 }.Generate();
        var source = new ReplayVector3Source(samples, SensorKind.Magnetometer);

        static string Fingerprint(ReplayVector3Source source)
        {
            var estimator = new MagnetometerSpeedEstimator();
            source.PushAll(estimator.Push);
            estimator.Flush();

            var estimate = estimator.Current!;
            return string.Join('|',
                estimate.RevolutionsPerMinute.ToString("R"),
                estimate.StandardError.ToString("R"),
                estimate.Confidence.ToString("R"),
                estimate.DeviationPercent.ToString("R"),
                estimator.SignedRadiansPerSecond.ToString("R"));
        }

        Assert.Equal(Fingerprint(source), Fingerprint(source));
    }

    [Fact]
    public void AudioPipelineIsBitForBitDeterministicAcrossRuns()
    {
        var signal = new ClickTrainGenerator { PeriodSeconds = 1.8, DurationSeconds = 25.0 }.Generate();
        var source = new ReplayAudioSource(signal, 48000);

        static string Fingerprint(ReplayAudioSource source)
        {
            var estimator = new ClickPeriodicityEstimator();
            source.PushAll(estimator.Push);
            estimator.Flush();

            var estimate = estimator.Current!;
            return string.Join('|',
                estimate.RevolutionsPerMinute.ToString("R"),
                estimate.StandardError.ToString("R"),
                estimate.Confidence.ToString("R"),
                estimator.PeriodSeconds.ToString("R"),
                estimator.Autocorrelation.PeakValue.ToString("R"));
        }

        Assert.Equal(Fingerprint(source), Fingerprint(source));
    }

    [Fact]
    public void ReplayingThroughFilesGivesTheSameAnswerAsReplayingInMemory()
    {
        var samples = new MagnetometerSignalGenerator { DurationSeconds = 30.0 }.Generate();

        var direct = new MagnetometerSpeedEstimator();
        foreach (var sample in samples)
        {
            direct.Push(sample);
        }

        direct.Flush();

        var path = Path.Combine(Path.GetTempPath(), $"turntable-replay-{Guid.NewGuid():N}.csv");
        try
        {
            SensorCsvIo.WriteFile(path, samples);

            var viaFile = new MagnetometerSpeedEstimator();
            ReplayVector3Source.FromCsvFile(path, SensorKind.Magnetometer).PushAll(viaFile.Push);
            viaFile.Flush();

            Assert.Equal(direct.Current!.RevolutionsPerMinute, viaFile.Current!.RevolutionsPerMinute);
            Assert.Equal(direct.Current.StandardError, viaFile.Current.StandardError);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
