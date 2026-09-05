namespace TurntableSpeed.Core.Tests.Synth;

/// <summary>Small helpers shared by the audio generators.</summary>
public static class AudioSynth
{
    /// <summary>
    /// Scale a signal so its loudest sample sits at <paramref name="peak"/>. Mixing a chord and
    /// a click train easily exceeds full scale, and a clipped fixture is not a fixture: the
    /// input diagnostics would rightly flag it, and the distortion would land in the spectrum
    /// the pitch estimator reads.
    /// </summary>
    public static float[] NormalizePeak(float[] samples, double peak = 0.8)
    {
        var loudest = 0.0;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs((double)sample);
            if (magnitude > loudest)
            {
                loudest = magnitude;
            }
        }

        if (!(loudest > 0.0))
        {
            return samples;
        }

        var gain = peak / loudest;
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            result[i] = (float)(samples[i] * gain);
        }

        return result;
    }
}
