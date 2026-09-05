using TurntableSpeed.Core.Contracts;

namespace TurntableSpeed.Core.Tests.Estimators;

public class NominalSpeedTests
{
    [Fact]
    public void DomainConstantsMatchTheSpecificationTable()
    {
        // Spec §1. These are the numbers everything else is measured against.
        TestAssertions.Close(0.555556, NominalSpeeds.Rpm33.RevolutionsPerSecond, 1e-6, "33⅓ rev/s");
        TestAssertions.Close(200.0, NominalSpeeds.Rpm33.DegreesPerSecond, 1e-9, "33⅓ °/s");
        TestAssertions.Close(3.490659, NominalSpeeds.Rpm33.RadiansPerSecond, 1e-6, "33⅓ rad/s");
        TestAssertions.Close(1.8, NominalSpeeds.Rpm33.PeriodSeconds, 1e-9, "33⅓ period");

        TestAssertions.Close(0.75, NominalSpeeds.Rpm45.RevolutionsPerSecond, 1e-9, "45 rev/s");
        TestAssertions.Close(270.0, NominalSpeeds.Rpm45.DegreesPerSecond, 1e-9, "45 °/s");
        TestAssertions.Close(4.712389, NominalSpeeds.Rpm45.RadiansPerSecond, 1e-6, "45 rad/s");
        TestAssertions.Close(1.333333, NominalSpeeds.Rpm45.PeriodSeconds, 1e-6, "45 period");

        TestAssertions.Close(1.3, NominalSpeeds.Rpm78.RevolutionsPerSecond, 1e-9, "78 rev/s");
        TestAssertions.Close(468.0, NominalSpeeds.Rpm78.DegreesPerSecond, 1e-9, "78 °/s");
        TestAssertions.Close(8.168141, NominalSpeeds.Rpm78.RadiansPerSecond, 1e-6, "78 rad/s");
        TestAssertions.Close(0.769231, NominalSpeeds.Rpm78.PeriodSeconds, 1e-6, "78 period");
    }

    [Fact]
    public void OnePercentFastIsSeventeenPointTwoCents()
    {
        // Spec §1 states this conversion explicitly.
        TestAssertions.Close(17.2, SpeedMath.CentsFromPercent(1.0), 0.05, "+1% in cents");
        TestAssertions.Close(-17.4, SpeedMath.CentsFromPercent(-1.0), 0.05, "−1% in cents");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-2.5)]
    [InlineData(50.0)]
    public void CentsAndPercentRoundTrip(double percent)
    {
        var cents = SpeedMath.CentsFromPercent(percent);

        TestAssertions.Close(percent, SpeedMath.PercentFromCents(cents), 1e-9, "percent round trip");
    }

    [Theory]
    [InlineData(33.3333, NominalSpeed.Rpm33)]
    [InlineData(33.5, NominalSpeed.Rpm33)]
    [InlineData(32.0, NominalSpeed.Rpm33)]
    [InlineData(45.0, NominalSpeed.Rpm45)]
    [InlineData(44.6, NominalSpeed.Rpm45)]
    [InlineData(46.0, NominalSpeed.Rpm45)]
    [InlineData(78.0, NominalSpeed.Rpm78)]
    [InlineData(77.0, NominalSpeed.Rpm78)]
    public void ClassifiesSpeedsNearANominal(double rpm, NominalSpeed expected)
    {
        var classification = NominalSpeeds.Classify(rpm);

        Assert.Equal(expected, classification.Nominal);
        Assert.True(classification.Confidence > 0.5,
            $"{rpm} rpm is clearly {expected} but confidence was only {classification.Confidence:F3}");
    }

    [Fact]
    public void SpeedHalfwayBetweenThirtyThreeAndFortyFiveIsNeverClaimedConfidently()
    {
        // Spec §7.2 calls this out by name. The midpoint of 33⅓ and 45 is 39.17 rpm, which is
        // 17.5% from one and 13% from the other — outside every tolerance band.
        const double midpoint = (100.0 / 3.0 + 45.0) / 2.0;

        var classification = NominalSpeeds.Classify(midpoint);

        Assert.Null(classification.Nominal);
        TestAssertions.Close(0.0, classification.Confidence, 1e-12, "confidence at the midpoint");
    }

    [Theory]
    [InlineData(37.0)]
    [InlineData(39.0)]
    [InlineData(41.0)]
    [InlineData(55.0)]
    [InlineData(65.0)]
    public void SpeedsBetweenNominalsAreLeftUnclassified(double rpm)
    {
        var classification = NominalSpeeds.Classify(rpm);

        Assert.Null(classification.Nominal);
    }

    [Fact]
    public void ConfidenceFallsAsTheReadingApproachesTheEdgeOfTheBand()
    {
        var dead = NominalSpeeds.Classify(100.0 / 3.0).Confidence;
        var near = NominalSpeeds.Classify(100.0 / 3.0 * 1.02).Confidence;
        var edge = NominalSpeeds.Classify(100.0 / 3.0 * 1.07).Confidence;

        Assert.True(dead > near, $"dead-on {dead:F3} should beat 2% off {near:F3}");
        Assert.True(near > edge, $"2% off {near:F3} should beat 7% off {edge:F3}");
    }

    [Fact]
    public void DeviationIsSignedAndRelativeToTheChosenNominal()
    {
        var fast = NominalSpeeds.Classify(100.0 / 3.0 * 1.015);
        var slow = NominalSpeeds.Classify(45.0 * 0.99);

        TestAssertions.Close(1.5, fast.DeviationPercent, 1e-9, "deviation of a fast platter");
        TestAssertions.Close(-1.0, slow.DeviationPercent, 1e-9, "deviation of a slow platter");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonsenseInputsClassifyToNothing(double rpm)
    {
        Assert.Null(NominalSpeeds.Classify(rpm).Nominal);
    }

    [Fact]
    public void SpeedEstimateCarriesTheClassificationAndTheInterval()
    {
        var estimate = SpeedEstimate.FromRpm(33.5, 0.01, 0.9);

        Assert.Equal(NominalSpeed.Rpm33, estimate.Nominal);
        TestAssertions.Close(0.5, estimate.DeviationPercent, 0.01, "deviation");
        TestAssertions.Close(8.63, estimate.DeviationCents, 0.05, "deviation in cents");
        TestAssertions.Close(0.0196, estimate.Margin95, 1e-4, "95% margin");
        TestAssertions.Close(0.0299, estimate.StandardErrorPercent, 1e-3, "relative standard error");
    }

    [Fact]
    public void UnclassifiedEstimateSaysSoInsteadOfGuessing()
    {
        var estimate = SpeedEstimate.FromRpm(39.17, 0.01, 0.9);

        Assert.Null(estimate.Nominal);
        Assert.Contains("unclassified", estimate.ToString());
    }

    [Theory]
    [InlineData(3.490658503988659, 100.0 / 3.0)]
    [InlineData(4.71238898038469, 45.0)]
    [InlineData(8.168140899333463, 78.0)]
    public void RadiansPerSecondAndRpmRoundTrip(double radiansPerSecond, double rpm)
    {
        TestAssertions.Close(rpm, SpeedMath.RpmFromRadiansPerSecond(radiansPerSecond), 1e-9, "rad/s to rpm");
        TestAssertions.Close(radiansPerSecond, SpeedMath.RadiansPerSecondFromRpm(rpm), 1e-9, "rpm to rad/s");
        TestAssertions.Close(rpm, SpeedMath.RpmFromPeriod(SpeedMath.PeriodFromRpm(rpm)), 1e-9, "period round trip");
    }
}
