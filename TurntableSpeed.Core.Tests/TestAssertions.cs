using System.Globalization;
using TurntableSpeed.Core.Contracts;
using Xunit.Sdk;

namespace TurntableSpeed.Core.Tests;

/// <summary>
/// Assertions that say what actually went wrong. A bare "expected 33.3333, got 33.4" is much
/// less useful here than "magnetometer speed was off by 0.20%, allowed 0.05%".
/// </summary>
public static class TestAssertions
{
    public static void Close(double expected, double actual, double tolerance, string what)
    {
        if (double.IsNaN(actual) || Math.Abs(actual - expected) > tolerance)
        {
            throw new XunitException(
                $"{what}: expected {Fmt(expected)} ± {Fmt(tolerance)}, got {Fmt(actual)} " +
                $"(off by {Fmt(actual - expected)}).");
        }
    }

    /// <summary>Assert a relative error, expressed in percent of <paramref name="expected"/>.</summary>
    public static void WithinPercent(double expected, double actual, double tolerancePercent, string what)
    {
        if (expected == 0.0)
        {
            throw new XunitException($"{what}: relative comparison against zero is meaningless.");
        }

        var errorPercent = (actual - expected) / expected * 100.0;
        if (double.IsNaN(errorPercent) || Math.Abs(errorPercent) > tolerancePercent)
        {
            throw new XunitException(
                $"{what}: expected {Fmt(expected)}, got {Fmt(actual)} — " +
                $"error {errorPercent:+0.0000;-0.0000}%, allowed ±{tolerancePercent}%.");
        }
    }

    /// <summary>
    /// The spec's central demand: every number carries an uncertainty. An estimator that is
    /// accurate but claims an interval far too tight has still failed.
    /// </summary>
    public static void IntervalCovers(double truth, SpeedEstimate estimate, string what, double sigmas = Tolerances.MaxSigmaFromTruth)
    {
        if (!(estimate.StandardError > 0.0) || !double.IsFinite(estimate.StandardError))
        {
            throw new XunitException(
                $"{what}: standard error is {Fmt(estimate.StandardError)} — a point value with no interval.");
        }

        var deviation = Math.Abs(estimate.RevolutionsPerMinute - truth) / estimate.StandardError;
        if (deviation > sigmas)
        {
            throw new XunitException(
                $"{what}: truth {Fmt(truth)} sits {deviation:F2}σ from the estimate " +
                $"{Fmt(estimate.RevolutionsPerMinute)} ± {Fmt(estimate.StandardError)} — " +
                $"the reported interval is too optimistic (limit {sigmas}σ).");
        }
    }

    /// <summary>Assert both accuracy and an honest interval in one go.</summary>
    public static void SpeedIs(
        double truthRpm,
        SpeedEstimate? estimate,
        double tolerancePercent,
        string what,
        double sigmas = Tolerances.MaxSigmaFromTruth)
    {
        if (estimate is null)
        {
            throw new XunitException($"{what}: estimator produced no result at all.");
        }

        WithinPercent(truthRpm, estimate.RevolutionsPerMinute, tolerancePercent, what);
        IntervalCovers(truthRpm, estimate, what, sigmas);
    }

    /// <summary>Compare two angles modulo <paramref name="period"/>.</summary>
    public static void CloseModulo(double expected, double actual, double period, double tolerance, string what)
    {
        var difference = (actual - expected) % period;
        if (difference > period / 2.0)
        {
            difference -= period;
        }
        else if (difference < -period / 2.0)
        {
            difference += period;
        }

        if (double.IsNaN(difference) || Math.Abs(difference) > tolerance)
        {
            throw new XunitException(
                $"{what}: expected {Fmt(expected)} (mod {Fmt(period)}), got {Fmt(actual)} — " +
                $"off by {Fmt(difference)}, allowed {Fmt(tolerance)}.");
        }
    }

    private static string Fmt(double value) =>
        value.ToString("G6", CultureInfo.InvariantCulture);
}
