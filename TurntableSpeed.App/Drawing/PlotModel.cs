using System.Globalization;
using TurntableSpeed.App.Localization;
using TurntableSpeed.Core.Dsp;
using TurntableSpeed.Core.Estimators;

namespace TurntableSpeed.App.Drawing;

/// <summary>
/// Plot-ready data. Built in the view models so the drawable stays a dumb renderer, and so the
/// awkward part — choosing sensible axis bounds for a series that may be empty, flat, or full
/// of NaN — is in one place rather than repeated per chart.
/// </summary>
public sealed record PlotModel(
    double[] X,
    double[] Y,
    double XMin,
    double XMax,
    double YMin,
    double YMax,
    string XUnit,
    string YUnit)
{
    public static PlotModel Empty { get; } =
        new([], [], 0, 1, 0, 1, string.Empty, string.Empty);

    public bool HasData => X.Length >= 2 && X.Length == Y.Length;

    /// <summary>Vertical line the renderer marks, e.g. the chosen autocorrelation peak.</summary>
    public double? MarkerX { get; init; }

    public string? MarkerLabel { get; init; }

    /// <summary>Horizontal reference, e.g. the nominal speed the reading is compared against.</summary>
    public double? ReferenceY { get; init; }

    /// <summary>Fill under the curve. Suits spectra; wrong for a time trace.</summary>
    public bool FillUnderCurve { get; init; }

    /// <summary>
    /// Build from paired arrays, choosing bounds that show the data rather than the origin.
    /// A flat series is given an artificial span so it draws as a line instead of collapsing.
    /// </summary>
    public static PlotModel From(
        double[] x,
        double[] y,
        string xUnit,
        string yUnit,
        double? forcedYMin = null,
        double? forcedYMax = null)
    {
        var count = Math.Min(x.Length, y.Length);
        if (count < 2)
        {
            return Empty with { XUnit = xUnit, YUnit = yUnit };
        }

        double xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        var used = 0;

        for (var i = 0; i < count; i++)
        {
            if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
            {
                continue;
            }

            xMin = Math.Min(xMin, x[i]);
            xMax = Math.Max(xMax, x[i]);
            yMin = Math.Min(yMin, y[i]);
            yMax = Math.Max(yMax, y[i]);
            used++;
        }

        if (used < 2)
        {
            return Empty with { XUnit = xUnit, YUnit = yUnit };
        }

        yMin = forcedYMin ?? yMin;
        yMax = forcedYMax ?? yMax;

        Pad(ref xMin, ref xMax);
        Pad(ref yMin, ref yMax);

        return new PlotModel(x, y, xMin, xMax, yMin, yMax, xUnit, yUnit);
    }

    /// <summary>The wow spectrum, in percent of nominal speed against modulation frequency.</summary>
    public static PlotModel FromSpectrum(Spectrum spectrum, double fromHz, double toHz, string yUnit)
    {
        if (spectrum.Count < 4 || !double.IsFinite(spectrum.Resolution) || spectrum.Resolution <= 0.0)
        {
            return Empty with { XUnit = AppStrings.UnitHertz, YUnit = yUnit };
        }

        var from = Math.Max(1, (int)Math.Floor(fromHz / spectrum.Resolution));
        var to = Math.Min(spectrum.Count - 1, (int)Math.Ceiling(toHz / spectrum.Resolution));
        if (from >= to)
        {
            return Empty with { XUnit = AppStrings.UnitHertz, YUnit = yUnit };
        }

        var length = to - from + 1;
        var x = new double[length];
        var y = new double[length];
        for (var i = 0; i < length; i++)
        {
            x[i] = spectrum.Frequencies[from + i];
            y[i] = spectrum.Amplitudes[from + i];
        }

        return From(x, y, AppStrings.UnitHertz, yUnit, forcedYMin: 0.0) with { FillUnderCurve = true };
    }

    /// <summary>The autocorrelation curve with its chosen period marked — spec §6 asks for this.</summary>
    public static PlotModel FromAutocorrelation(AutocorrelationView view)
    {
        if (view.Lags.Length < 2)
        {
            return Empty with { XUnit = AppStrings.UnitSeconds, YUnit = string.Empty };
        }

        var model = From(view.Lags, view.Values, AppStrings.UnitSeconds, string.Empty);
        if (!double.IsFinite(view.PeakLagSeconds))
        {
            return model;
        }

        return model with
        {
            MarkerX = view.PeakLagSeconds,
            MarkerLabel = Tr.Format(AppStrings.RpmValue,
                (60.0 / view.PeakLagSeconds).ToString("F2", CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>A time trace from timestamped values, re-based so the newest sample is at zero.</summary>
    public static PlotModel FromTrace(IReadOnlyList<TimedValue> trace, string yUnit, double? referenceY = null)
    {
        if (trace.Count < 2)
        {
            return Empty with { XUnit = AppStrings.UnitSeconds, YUnit = yUnit };
        }

        var newest = trace[^1].T;
        var x = new double[trace.Count];
        var y = new double[trace.Count];
        for (var i = 0; i < trace.Count; i++)
        {
            x[i] = trace[i].T - newest;
            y[i] = trace[i].Value;
        }

        return From(x, y, AppStrings.UnitSeconds, yUnit) with { ReferenceY = referenceY };
    }

    private static void Pad(ref double min, ref double max)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            min = 0.0;
            max = 1.0;
            return;
        }

        var span = max - min;
        if (span <= 0.0)
        {
            // A flat series still deserves to be drawn as a line rather than a division by zero.
            var magnitude = Math.Max(1e-9, Math.Abs(max));
            min -= magnitude * 0.05;
            max += magnitude * 0.05;
            return;
        }

        min -= span * 0.05;
        max += span * 0.05;
    }
}
