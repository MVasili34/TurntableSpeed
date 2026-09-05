using System.Globalization;
using System.Text;
using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Io;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Golden;

/// <summary>
/// Writes the synthetic entries of <c>TurntableSpeed.Fixtures</c> and records what the
/// estimators produced for them.
/// <para>
/// Not a test — a tool, run deliberately when a fixture is added or a golden value is
/// intentionally revised. Running it re-records the golden column, which is exactly what a
/// regression test must never do by itself, so it is kept out of the test run.
/// </para>
/// <para>
/// Real recordings from real turntables are the point of the fixture set (spec §7.4); these
/// synthetic ones only prove the golden mechanism works and give it something to guard until
/// real material is added alongside them.
/// </para>
/// </summary>
public static class FixtureBuilder
{
    public const int AudioSampleRate = 44100;

    public static void Build(string directory)
    {
        Directory.CreateDirectory(directory);

        var rows = new List<GoldenFixture>
        {
            BuildClickFixture(directory, "synthetic-clicks-33.wav", 100.0 / 3.0, seconds: 16.0, seed: 101),
            BuildClickFixture(directory, "synthetic-clicks-45.wav", 45.0, seconds: 16.0, seed: 202),
            BuildClickFixture(directory, "synthetic-clicks-33-slow.wav", 100.0 / 3.0 * 0.982, seconds: 18.0, seed: 303),
            BuildMagnetometerFixture(directory, "synthetic-magnetometer-33.csv", 100.0 / 3.0, seconds: 60.0, seed: 404),
            BuildMagnetometerFixture(directory, "synthetic-magnetometer-45-dirty.csv", 45.0, seconds: 60.0, seed: 505,
                hardIron: (28.0, -15.0, 9.0), softIronRatio: 0.72, outlierProbability: 0.01),
        };

        File.WriteAllText(Path.Combine(directory, GoldenManifest.FileName), GoldenManifest.Write(rows), Encoding.UTF8);
    }

    private static GoldenFixture BuildClickFixture(
        string directory,
        string fileName,
        double rpm,
        double seconds,
        int seed)
    {
        var programme = new TonalSignalGenerator
        {
            Frequencies = TonalSignalGenerator.AMajorTriad,
            DurationSeconds = seconds,
            SampleRate = AudioSampleRate,
            Amplitude = 0.12,
            Seed = seed,
        }.Generate();

        var signal = AudioSynth.NormalizePeak(new ClickTrainGenerator
        {
            PeriodSeconds = 60.0 / rpm,
            DurationSeconds = seconds,
            SampleRate = AudioSampleRate,
            Programme = programme,
            Seed = seed,
        }.Generate());

        WavIo.WriteFile(Path.Combine(directory, fileName), signal, AudioSampleRate);

        var estimator = new ClickPeriodicityEstimator();
        ReplayAudioSource.FromWavFile(Path.Combine(directory, fileName)).PushAll(estimator.Push);
        estimator.Flush();

        return new GoldenFixture(
            fileName,
            GoldenKind.AudioClick,
            rpm,
            Tolerances.ClickRpmPercent,
            estimator.Current?.RevolutionsPerMinute ?? double.NaN,
            $"synthetic click train under an A major triad, {seconds:F0} s");
    }

    private static GoldenFixture BuildMagnetometerFixture(
        string directory,
        string fileName,
        double rpm,
        double seconds,
        int seed,
        (double X, double Y, double Z) hardIron = default,
        double softIronRatio = 1.0,
        double outlierProbability = 0.0)
    {
        var samples = new MagnetometerSignalGenerator
        {
            Rpm = rpm,
            DurationSeconds = seconds,
            SampleRateHz = 50.0,
            HardIron = hardIron,
            SoftIronAxisRatio = softIronRatio,
            SoftIronAngle = 0.4,
            OutlierProbability = outlierProbability,
            Seed = seed,
        }.Generate();

        var path = Path.Combine(directory, fileName);
        SensorCsvIo.WriteFile(path, samples,
            $"synthetic magnetometer, {rpm:F4} rpm, hard iron ({hardIron.X:F1}, {hardIron.Y:F1}, {hardIron.Z:F1}) uT");

        var estimator = new MagnetometerSpeedEstimator();
        ReplayVector3Source.FromCsvFile(path, SensorKind.Magnetometer).PushAll(estimator.Push);
        estimator.Flush();

        return new GoldenFixture(
            fileName,
            GoldenKind.Magnetometer,
            rpm,
            Tolerances.MagnetometerRpmPercent60s,
            estimator.Current?.RevolutionsPerMinute ?? double.NaN,
            $"synthetic magnetometer, {seconds:F0} s" +
            (outlierProbability > 0.0 ? ", with hard/soft iron and magnetic outliers" : string.Empty));
    }
}

public enum GoldenKind
{
    AudioClick,
    Magnetometer,
}

/// <summary>
/// One fixture and what is expected of it.
/// </summary>
/// <param name="ExpectedRpm">
/// The physical truth. For a synthetic fixture it is known exactly; for a real recording it is
/// whatever independent measurement the fixture was documented with.
/// </param>
/// <param name="TolerancePercent">Accuracy the spec demands of this method.</param>
/// <param name="GoldenRpm">
/// What the estimator produced when the fixture was recorded. Held to a far tighter tolerance
/// than <paramref name="ExpectedRpm"/>: its job is not to check accuracy but to notice a change
/// in behaviour, so that an algorithm edit cannot quietly move the answer on real material.
/// </param>
public sealed record GoldenFixture(
    string File,
    GoldenKind Kind,
    double ExpectedRpm,
    double TolerancePercent,
    double GoldenRpm,
    string Notes);

public static class GoldenManifest
{
    public const string FileName = "manifest.csv";

    /// <summary>
    /// How far the estimator may drift from its recorded golden value before the change counts
    /// as a regression rather than noise. Tight on purpose: the pipeline is deterministic, so
    /// on unchanged input an unchanged algorithm reproduces the number exactly.
    /// </summary>
    public const double DriftTolerancePercent = 0.01;

    public static string Write(IEnumerable<GoldenFixture> fixtures)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# TurntableSpeed golden fixtures. See README.md.");
        builder.AppendLine("# expectedRpm is the physical truth; goldenRpm is what the estimator produced");
        builder.AppendLine("# when the fixture was recorded, and guards against silent behaviour changes.");
        builder.AppendLine("file,kind,expectedRpm,tolerancePercent,goldenRpm,notes");

        foreach (var fixture in fixtures)
        {
            builder.Append(fixture.File).Append(',')
                .Append(fixture.Kind).Append(',')
                .Append(fixture.ExpectedRpm.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(fixture.TolerancePercent.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(fixture.GoldenRpm.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(fixture.Notes.Replace(',', ';'));
        }

        return builder.ToString();
    }

    public static List<GoldenFixture> Read(string path)
    {
        var fixtures = new List<GoldenFixture>();

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed.StartsWith("file,", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = trimmed.Split(',');
            if (fields.Length < 5)
            {
                throw new InvalidDataException($"Malformed manifest row: {line}");
            }

            fixtures.Add(new GoldenFixture(
                fields[0].Trim(),
                Enum.Parse<GoldenKind>(fields[1].Trim(), ignoreCase: true),
                double.Parse(fields[2], CultureInfo.InvariantCulture),
                double.Parse(fields[3], CultureInfo.InvariantCulture),
                double.Parse(fields[4], CultureInfo.InvariantCulture),
                fields.Length > 5 ? fields[5].Trim() : string.Empty));
        }

        return fixtures;
    }
}
