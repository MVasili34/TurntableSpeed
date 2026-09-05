namespace TurntableSpeed.Core.Dsp;

/// <summary>A one-sided amplitude spectrum on a uniform frequency grid.</summary>
public sealed record Spectrum(double[] Frequencies, double[] Amplitudes)
{
    public static Spectrum Empty { get; } = new([], []);

    public int Count => Amplitudes.Length;

    /// <summary>Bin spacing, i.e. the frequency resolution of the analysis.</summary>
    public double Resolution => Frequencies.Length >= 2 ? Frequencies[1] - Frequencies[0] : double.NaN;

    public double MaxFrequency => Frequencies.Length > 0 ? Frequencies[^1] : double.NaN;

    /// <summary>
    /// Compute an amplitude spectrum of a uniformly sampled series. The series is de-trended
    /// and Hann-windowed; amplitudes are corrected for the window's coherent gain, so a pure
    /// sinusoid of amplitude A reads A.
    /// </summary>
    public static Spectrum FromSeries(in UniformSeries series, bool detrend = true, int zeroPadFactor = 1)
    {
        if (series.Count < 8 || !(series.SampleInterval > 0.0))
        {
            return Empty;
        }

        var data = (double[])series.Values.Clone();
        if (detrend)
        {
            SeriesMath.DetrendLinearInPlace(data);
        }
        else
        {
            SeriesMath.RemoveMeanInPlace(data);
        }

        var window = WindowFunctions.Create(WindowFunctions.Kind.Hann, data.Length);
        WindowFunctions.ApplyInPlace(data, window);
        var gain = WindowFunctions.CoherentGain(window);

        var length = Fft.NextPowerOfTwo(data.Length * Math.Max(1, zeroPadFactor));
        var padded = Fft.ZeroPad(data, length);

        // Zero padding dilutes the amplitude by the padding ratio; the window by its gain.
        var amplitudes = Fft.MagnitudeSpectrum(padded);
        var correction = length / (double)data.Length / Math.Max(1e-12, gain);
        for (var i = 0; i < amplitudes.Length; i++)
        {
            amplitudes[i] *= correction;
        }

        var sampleRate = series.SampleRate;
        var frequencies = new double[amplitudes.Length];
        for (var i = 0; i < frequencies.Length; i++)
        {
            frequencies[i] = Fft.BinFrequency(i, length, sampleRate);
        }

        return new Spectrum(frequencies, amplitudes);
    }

    public int BinOf(double frequency)
    {
        var resolution = Resolution;
        if (!double.IsFinite(resolution) || resolution <= 0.0)
        {
            return -1;
        }

        var bin = (int)Math.Round(frequency / resolution);
        return bin >= 0 && bin < Count ? bin : -1;
    }

    /// <summary>
    /// Strongest bin in [<paramref name="fromHz"/>, <paramref name="toHz"/>], refined by
    /// parabolic interpolation. Returns NaN frequency when the band is empty.
    /// </summary>
    public (double Frequency, double Amplitude) PeakInBand(double fromHz, double toHz)
    {
        var resolution = Resolution;
        if (Count == 0 || !double.IsFinite(resolution) || resolution <= 0.0)
        {
            return (double.NaN, double.NaN);
        }

        var from = Math.Max(1, (int)Math.Floor(fromHz / resolution));
        var to = Math.Min(Count - 1, (int)Math.Ceiling(toHz / resolution));
        if (from > to)
        {
            return (double.NaN, double.NaN);
        }

        var (index, peak) = PeakInterpolation.RefineMaximum(Amplitudes, from, to);
        if (double.IsNaN(index))
        {
            return (double.NaN, double.NaN);
        }

        return (index * resolution, peak.Value);
    }

    /// <summary>RMS of the spectral content inside a band, in the same unit as the amplitudes.</summary>
    public double BandRms(double fromHz, double toHz)
    {
        var resolution = Resolution;
        if (Count == 0 || !double.IsFinite(resolution) || resolution <= 0.0)
        {
            return double.NaN;
        }

        var from = Math.Max(1, (int)Math.Floor(fromHz / resolution));
        var to = Math.Min(Count - 1, (int)Math.Ceiling(toHz / resolution));
        if (from > to)
        {
            return 0.0;
        }

        var sum = 0.0;
        for (var i = from; i <= to; i++)
        {
            sum += 0.5 * Amplitudes[i] * Amplitudes[i];
        }

        return Math.Sqrt(sum);
    }

    /// <summary>The <paramref name="count"/> strongest local maxima in a band, strongest first.</summary>
    public IReadOnlyList<(double Frequency, double Amplitude)> TopPeaks(double fromHz, double toHz, int count)
    {
        var resolution = Resolution;
        if (Count == 0 || !double.IsFinite(resolution) || resolution <= 0.0)
        {
            return [];
        }

        var from = Math.Max(1, (int)Math.Floor(fromHz / resolution));
        var to = Math.Min(Count - 2, (int)Math.Ceiling(toHz / resolution));
        var maxima = PeakInterpolation.LocalMaxima(Amplitudes, from, to);

        var peaks = new List<(double Frequency, double Amplitude)>(maxima.Count);
        foreach (var index in maxima)
        {
            var refined = PeakInterpolation.Parabolic(Amplitudes[index - 1], Amplitudes[index], Amplitudes[index + 1]);
            peaks.Add(((index + refined.Offset) * resolution, refined.Value));
        }

        peaks.Sort((a, b) => b.Amplitude.CompareTo(a.Amplitude));
        return peaks.Count <= count ? peaks : peaks.GetRange(0, count);
    }
}

/// <summary>
/// Wow &amp; flutter analysis of a speed series.
/// </summary>
/// <param name="RmsPercent">Unweighted RMS speed modulation over the analysis band, in percent.</param>
/// <param name="PeakPercent">Amplitude of the strongest single modulation component, in percent.</param>
/// <param name="DominantFrequency">Frequency of that component, Hz.</param>
/// <param name="Spectrum">Modulation spectrum in percent of nominal speed.</param>
public sealed record WowFlutterResult(
    double RmsPercent,
    double PeakPercent,
    double DominantFrequency,
    Spectrum Spectrum)
{
    public static WowFlutterResult Empty { get; } =
        new(double.NaN, double.NaN, double.NaN, Spectrum.Empty);

    /// <summary>Peaks the UI labels: disc eccentricity, motor pulley, belt circumference.</summary>
    public IReadOnlyList<(double Frequency, double Amplitude)> Components(int count = 5) =>
        Spectrum.TopPeaks(0.05, Math.Min(200.0, Spectrum.MaxFrequency), count);
}

public static class WowFlutter
{
    /// <summary>Lower edge of the analysis band, Hz. Below this it is drift, not wow.</summary>
    public const double DefaultBandLowHz = 0.2;

    /// <summary>Upper edge of the analysis band, Hz.</summary>
    public const double DefaultBandHighHz = 20.0;

    /// <summary>
    /// Turn a uniformly sampled speed series into a modulation spectrum expressed in percent
    /// of the mean speed.
    /// </summary>
    public static WowFlutterResult Analyze(
        in UniformSeries series,
        double bandLowHz = DefaultBandLowHz,
        double bandHighHz = DefaultBandHighHz)
    {
        if (series.Count < 32 || !(series.SampleInterval > 0.0))
        {
            return WowFlutterResult.Empty;
        }

        var mean = SeriesMath.Mean(series.Values);
        if (!double.IsFinite(mean) || Math.Abs(mean) < 1e-12)
        {
            return WowFlutterResult.Empty;
        }

        var relative = new double[series.Count];
        for (var i = 0; i < relative.Length; i++)
        {
            relative[i] = (series.Values[i] - mean) / mean * 100.0;
        }

        var spectrum = Spectrum.FromSeries(new UniformSeries(series.StartTime, series.SampleInterval, relative));
        var high = Math.Min(bandHighHz, series.SampleRate / 2.0 * 0.9);
        var rms = spectrum.BandRms(bandLowHz, high);
        var (frequency, amplitude) = spectrum.PeakInBand(bandLowHz, high);

        return new WowFlutterResult(rms, amplitude, frequency, spectrum);
    }
}
