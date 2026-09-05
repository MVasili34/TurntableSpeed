using TurntableSpeed.Core.Estimators;
using TurntableSpeed.Core.Tests.Synth;

namespace TurntableSpeed.Core.Tests.Properties;

/// <summary>
/// "Every number carries an estimate of its uncertainty" (spec §10) is only worth something if
/// the uncertainty is the right size. Checking a single measurement against ±3σ cannot tell a
/// well-stated interval from one that is 10% too tight — over enough draws both look much the
/// same, and the difference only shows in the <em>spread</em>.
/// <para>
/// So these tests simulate many recordings and measure z = (measured − truth) / reported error.
/// A correctly stated interval gives RMS(z) = 1. Below 1 the estimator is being pessimistic;
/// above 1 it is claiming a precision it does not have, which is the failure that matters.
/// </para>
/// </summary>
public class IntervalCalibrationTests
{
    private static (double Rms, double Mean, int Count) Calibrate(Func<int, (double Truth, double Measured, double Sigma)> trial, int trials)
    {
        var sum = 0.0;
        var sumSquares = 0.0;
        var used = 0;

        for (var i = 0; i < trials; i++)
        {
            var (truth, measured, sigma) = trial(i);
            if (!double.IsFinite(sigma) || sigma <= 0.0 || !double.IsFinite(measured))
            {
                continue;
            }

            var z = (measured - truth) / sigma;
            sum += z;
            sumSquares += z * z;
            used++;
        }

        return (Math.Sqrt(sumSquares / used), sum / used, used);
    }

    [Fact]
    public void MagnetometerIntervalIsTheRightSize()
    {
        const double rpm = 100.0 / 3.0;
        const int trials = 60;

        var (rms, mean, count) = Calibrate(i =>
        {
            var estimator = new MagnetometerSpeedEstimator();
            foreach (var sample in new MagnetometerSignalGenerator
                     {
                         Rpm = rpm,
                         DurationSeconds = 20.0,
                         SampleRateHz = 50.0,
                         NoiseSigma = 0.5,
                         Seed = 1 + i,
                     }.Generate())
            {
                estimator.Push(sample);
            }

            estimator.Flush();
            var estimate = estimator.Current!;
            return (rpm, estimate.RevolutionsPerMinute, estimate.StandardError);
        }, trials);

        Assert.Equal(trials, count);

        // Sixty draws put the sampling error of RMS(z) near 9%, so this catches an interval that
        // is badly wrong rather than slightly so; the tight check lives on the primitive, in
        // LineFitTests, where thousands of trials cost milliseconds.
        Assert.InRange(rms, 0.7, 1.4);
        Assert.InRange(mean, -0.4, 0.4);
    }

    [Fact]
    public void GyroscopeIntervalCoversItsUncalibratedScaleError()
    {
        // The gyroscope's dominant error is a fixed factory scale factor, not noise, so the
        // interval must be wide enough to contain it before calibration — for every one of a
        // range of scale errors, not on average.
        const double rpm = 100.0 / 3.0;

        for (var scaleError = -0.03; scaleError <= 0.0301; scaleError += 0.005)
        {
            var estimator = new GyroscopeSpeedEstimator();
            foreach (var sample in new GyroSignalGenerator
                     {
                         Rpm = rpm,
                         DurationSeconds = 20.0,
                         SampleRateHz = 200.0,
                         NoiseSigma = 0.01,
                         ScaleFactorError = scaleError,
                     }.Generate())
            {
                estimator.Push(sample);
            }

            estimator.Flush();

            TestAssertions.IntervalCovers(rpm, estimator.Current!,
                $"uncalibrated gyroscope with a {scaleError:P1} scale error");
        }
    }

    [Fact]
    public void ClickPeriodicityIntervalIsTheRightSize()
    {
        const double period = 1.8;
        const int trials = 25;

        var (rms, mean, count) = Calibrate(i =>
        {
            var estimator = new ClickPeriodicityEstimator();
            new ClickTrainGenerator
            {
                PeriodSeconds = period,
                DurationSeconds = 20.0,
                JitterSeconds = 0.002,
                Seed = 1 + i,
            }.ToSource().PushAll(estimator.Push);

            estimator.Flush();
            var estimate = estimator.Current!;
            return (60.0 / period, estimate.RevolutionsPerMinute, estimate.StandardError);
        }, trials);

        Assert.Equal(trials, count);

        // The acoustic interval is deliberately floored by the interpolation bias, which is a
        // systematic rather than a random error, so a spread below 1 is expected and correct
        // here. What must not happen is a spread above 1.
        Assert.InRange(rms, 0.05, 1.4);
        Assert.InRange(mean, -1.0, 1.0);
    }
}
