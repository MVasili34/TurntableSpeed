using TurntableSpeed.Core.Contracts;
using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Io;

namespace TurntableSpeed.Core.Tests.Golden;

/// <summary>
/// Spec §7.4: replay the fixtures and check them against documented expectations, so that an
/// algorithm change cannot quietly make the result worse on recorded material.
/// </summary>
public class GoldenTests
{
    private static string FixturesDirectory => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string ManifestPath => Path.Combine(FixturesDirectory, GoldenManifest.FileName);

    public static TheoryData<string> FixtureNames
    {
        get
        {
            var data = new TheoryData<string>();
            if (File.Exists(ManifestPath))
            {
                foreach (var fixture in GoldenManifest.Read(ManifestPath))
                {
                    data.Add(fixture.File);
                }
            }
            else
            {
                data.Add(string.Empty);
            }

            return data;
        }
    }

    private static double Measure(GoldenFixture fixture)
    {
        var path = Path.Combine(FixturesDirectory, fixture.File);

        switch (fixture.Kind)
        {
            case GoldenKind.AudioClick:
            {
                var estimator = new ClickPeriodicityEstimator();
                ReplayAudioSource.FromWavFile(path).PushAll(estimator.Push);
                estimator.Flush();
                return estimator.Current?.RevolutionsPerMinute ?? double.NaN;
            }

            case GoldenKind.Magnetometer:
            {
                var estimator = new MagnetometerSpeedEstimator();
                ReplayVector3Source.FromCsvFile(path, SensorKind.Magnetometer).PushAll(estimator.Push);
                estimator.Flush();
                return estimator.Current?.RevolutionsPerMinute ?? double.NaN;
            }

            default:
                throw new InvalidOperationException($"Unknown fixture kind {fixture.Kind}.");
        }
    }

    [Fact]
    public void ManifestExistsAndListsFixtures()
    {
        Assert.True(File.Exists(ManifestPath),
            $"No fixture manifest at {ManifestPath}. Golden tests silently passing because there is " +
            $"nothing to check is the failure mode this assertion exists to prevent.");

        Assert.NotEmpty(GoldenManifest.Read(ManifestPath));
    }

    [Fact]
    public void EveryFixtureInTheManifestIsPresentOnDisk()
    {
        foreach (var fixture in GoldenManifest.Read(ManifestPath))
        {
            Assert.True(File.Exists(Path.Combine(FixturesDirectory, fixture.File)),
                $"Manifest lists {fixture.File} but the file is missing.");
        }
    }

    [Fact]
    public void NoFixtureOnDiskIsLeftUnchecked()
    {
        var listed = GoldenManifest.Read(ManifestPath)
            .Select(f => f.File)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(FixturesDirectory))
        {
            var name = Path.GetFileName(path);
            if (name.Equals(GoldenManifest.FileName, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.True(listed.Contains(name),
                $"{name} sits in the fixtures directory but no manifest row refers to it, so nothing tests it.");
        }
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void FixtureIsMeasuredToTheAccuracyTheSpecificationDemands(string fileName)
    {
        Assert.False(string.IsNullOrEmpty(fileName), "No fixtures found — see ManifestExistsAndListsFixtures.");

        var fixture = GoldenManifest.Read(ManifestPath).Single(f => f.File == fileName);
        var measured = Measure(fixture);

        TestAssertions.WithinPercent(fixture.ExpectedRpm, measured, fixture.TolerancePercent,
            $"{fixture.File} ({fixture.Notes})");
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void FixtureStillProducesItsRecordedGoldenValue(string fileName)
    {
        Assert.False(string.IsNullOrEmpty(fileName), "No fixtures found — see ManifestExistsAndListsFixtures.");

        var fixture = GoldenManifest.Read(ManifestPath).Single(f => f.File == fileName);
        var measured = Measure(fixture);

        TestAssertions.WithinPercent(fixture.GoldenRpm, measured, GoldenManifest.DriftTolerancePercent,
            $"{fixture.File} has drifted from its recorded behaviour — if the change is intended, " +
            $"re-record the manifest deliberately rather than widening this tolerance");
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void ReplayingAFixtureTwiceGivesTheSameNumber(string fileName)
    {
        Assert.False(string.IsNullOrEmpty(fileName), "No fixtures found — see ManifestExistsAndListsFixtures.");

        var fixture = GoldenManifest.Read(ManifestPath).Single(f => f.File == fileName);

        Assert.Equal(Measure(fixture), Measure(fixture));
    }
}
