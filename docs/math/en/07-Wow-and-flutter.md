# 7. Wow and flutter: the modulation spectrum

Code: `Dsp/Spectrum.cs` (`WowFlutter`), `Estimators/WowSpectrumEstimator.cs`,
`Estimators/GyroscopeSpeedEstimator.cs`

---

## 7.1. What wow and flutter is physically

The platter's speed is not constant. The real speed is

```
ω(t) = ω₀ · (1 + m(t))
```

where `m(t)` is the relative modulation, usually a fraction of a percent. Traditionally it is
divided up by frequency:

| Term | Band | Source | How it sounds |
|---|---|---|---|
| **Drift** | < 0.2 Hz | motor warm-up, belt tension | slow pitch wander |
| **Wow** | 0.2–6 Hz | platter eccentricity, bearing runout, an off-centre hole | a "swimming" sound on long notes |
| **Flutter** | 6–20 Hz | motor shaft, belt irregularities, pulley | shaking, a "rattle" |

Each mechanism is attached to a rotating part and therefore produces a **narrow spectral line**
at that part's rotation frequency. The modulation spectrum is, in effect, a list of the
turntable's faults.

These frequencies themselves (0.2–20 Hz) lie below the threshold of hearing and are not
perceived as sound — what is heard is the **modulation** they impose on a musical tone. Hearing
is most sensitive to modulation around 4 Hz, where a deviation of about 0.1 % (≈1.7 cents) is
noticeable; hence the shape of the DIN/IEC weighting curves discussed in §7.3. More on the
perception of modulation in [00 §0.9](00-Sound-theory.md).

### Diagnostic frequencies

| Frequency | Source |
|---|---|
| `f_platter` = 0.556 / 0.75 / 1.3 Hz | record eccentricity, spindle misalignment, bearing runout |
| motor `f_shaft` (usually 4–25 Hz) | rotor imbalance, cogging torque |
| `v_belt / L_belt` | uneven belt thickness, the glued seam |

A peak exactly at the platter's rotation frequency is the most common and the most harmless:
most often it is simply a record whose hole was drilled slightly off centre.

---

## 7.2. Two independent routes to the modulation spectrum

The project measures wow and flutter in **two different ways**, and this is not duplication:

| | Sensor (gyroscope) | Acoustic (pitch spectrum) |
|---|---|---|
| Source series | `ω(t)` directly | the cents deviation series |
| Band | 0.2–20 Hz | 0.2–10 Hz |
| Duration | seconds | 2–3 minutes |
| Complication | the phone on the platter changes the regime | needs tonal material |
| What it measures | rotation of the **platter** | rotation during **playback** |

The second route matters precisely because the phone on the platter has already changed the
mechanics ([04](04-Accelerometer.md) §7). The acoustic route measures what actually happens
during playback.

---

## 7.3. The general algorithm: from a speed series to a modulation spectrum

Code: `WowFlutter.Analyze`

### Step 1. A uniform grid

Sensor callbacks jitter, and analysis frames are not perfectly uniform either. The FFT demands
strict uniformity. `UniformResampler.Resample` performs linear interpolation onto a grid whose
step is the median input interval (or an explicitly given one — 100 Hz for the gyroscope, 20 Hz
for the acoustic path).

The median interval rather than the mean, for robustness to dropped samples: one dropout doubles
an interval and corrupts the mean, but not the median.

### Step 2. Normalising into percent

```
m[i] = (ω[i] − ω̄) / ω̄ · 100
```

Something important happens here: **the gyroscope's scale factor cancels**. If the sensor
reports `k·ω`, then

```
(k·ω − k·ω̄)/(k·ω̄) = (ω − ω̄)/ω̄
```

The scale error vanishes. This is why a gyroscope useless for absolute speed measures wow and
flutter excellently.

### Step 3. Detrending

`SeriesMath.DetrendLinearInPlace` subtracts a least-squares line (on a uniform grid, via
closed-form expressions, with no allocation):

```
Σx = n(n−1)/2,   Σx² = (n−1)n(2n−1)/6
slope = (n·Σxy − Σx·Σy)/(n·Σx² − (Σx)²)
```

**Why it is mandatory.** Slow drift (motor warm-up) is an unbounded trend, and the FFT
implicitly assumes periodicity over the window length. A trend creates a step between the
beginning and the end, whose spectrum is `1/f` smeared across every low bin. That is exactly
where the platter rotation peak sits (0.556 Hz), and it would be buried.

### Step 4. Window and FFT

A Hann window, zero padding to a power of two, FFT, amplitude correction:

```
correction = (padded_length / data_length) / window_coherent_gain
```

Both factors are required:

* the Hann window's **coherent gain** is 0.5; without the division the amplitudes are halved;
* the **length ratio** compensates for the dilution from zero padding (the same energy spread
  over more bins).

After the correction a sine of amplitude A gives exactly A in its bin.

### Step 5. Reading off the results

**Band RMS.** From Parseval's relation for an amplitude spectrum: a sine of amplitude A has RMS
`A/√2`, so

```
RMS_band = √( Σ_band ½·A_k² )
```

This is the reported wow & flutter figure in percent (unweighted; professional DIN/IEC
instruments also apply a frequency weighting around 4 Hz, which is not done here — the raw
number is more informative for diagnosis).

**The peak.** The strongest local maximum in the band, refined with a parabola. Its frequency is
the diagnosis: which part is at fault.

**The components.** `TopPeaks` returns the 5 strongest maxima — what the UI labels as
"eccentricity / pulley / belt".

---

## 7.4. The long acoustic mode

Code: `Estimators/WowSpectrumEstimator.cs`

Here the same machinery is applied to the **pitch deviation series** from
[06](06-Acoustics-pitch.md), and it yields **a third independent way of measuring speed**.

### The idea

Record eccentricity modulates the pitch exactly once per revolution. So the spectrum of the
cents series has a line exactly at the rotation frequency:

```
0.5556 Hz at 33⅓
0.7500 Hz at 45
1.3000 Hz at 78
```

Find that line and you have the speed **directly**: `rpm = f_peak · 60`.

### Why the caveat about the recording's tuning does NOT apply here

This is an important and elegant point. The pitch estimator measures an absolute deviation which
is inseparably mixed with the recording's tuning. But here:

1. A constant tuning offset is a **constant** in the series, and detrending removes it.
2. What is measured is a **modulation frequency**, and a frequency is indifferent to the level
   it oscillates about.

A recording tuned to 432 Hz instead of 440 corresponds to a constant offset of −32 cents — which
goes entirely into the detrend. The rotation frequency is unaffected.

So the long mode delivers an **honest absolute speed** from the very same material out of which
the instantaneous indication gives only an estimate with a caveat.

### Unwrapping the cents series

A subtlety that is easy to miss. The cents are folded into (−50, +50]. Slow drift crossing the
fold boundary looks like a **100-cent jump** — which the spectrum would take for a powerful
impulse.

So before anything else the series is unwrapped over a period of 100 cents:

```
θᵢ = centsᵢ · 2π/100
θ_unwrapped = Unwrap(θ)
cents_unwrapped = θ_unwrapped · 100/(2π)
```

Only after that do detrending and the FFT make sense.

### Resolution and duration

The FFT resolution:

```
Δf = 1/T_recording
```

| Duration | Δf | Δf in rpm at 33⅓ | Relative |
|---|---|---|---|
| 60 s | 0.0167 Hz | 1.0 rpm | 3.0 % |
| 120 s | 0.0083 Hz | 0.50 rpm | 1.5 % |
| 180 s | 0.0056 Hz | 0.33 rpm | 1.0 % |

Hence the parameters: a minimum of 60 s (otherwise the line is not resolved), with 180 s as the
target.

A resolution of 1 % is coarse compared with the autocorrelation method (0.3 %). But sub-bin peak
interpolation improves it by roughly an order of magnitude, and — more importantly — **the
mechanism is entirely different**, so agreement with the autocorrelation method is strong
independent evidence. That is the mode's main value.

An analysis rate of 20 Hz is more than sufficient: the band of interest reaches 10 Hz, so
Nyquist is satisfied.

### The peak frequency's uncertainty

The same logic as for the autocorrelation:

```
σ_f = (band_background / (2·curvature)) · Δf
```

clamped to the range `[0.05·Δf, 4·Δf]`. The lower bound is roughly where interpolation stops
being meaningful; the upper one stops the uncertainty running off to infinity on a poor peak.

```
σ_rpm = σ_f · 60
```

### The detection threshold

As in the autocorrelation: the background is computed over the band excluding ±0.05 Hz around
the peak, and confidence falls to zero at a peak-to-background ratio below 4 and rises to 1 by
12.

---

## 7.5. The division of labour between the three acoustic estimators

```
                        speed   wow&flutter   time to answer   independent of tuning
Clicks (autocorrelation)  ★★★         —           10–15 s             fully
Pitch (semitone grid)      ★          —            2–3 s          NO (the caveat)
Wow spectrum              ★★         ★★★         60–180 s      fully (detrended)
```

All three work on the same audio stream. The wow estimator does not run FFTs of its own — it
subscribes to the pitch estimator's frames through `FrameSink`, so the expensive part
(4096-point FFTs every 50 ms) is performed exactly once.

---

## 7.6. Summary of the long mode

```
audio, 2–3 minutes
   ↓
PitchGridEstimator → frames (t, cents, R) every 50 ms
   ↓
FrameSink → filter R ≥ 0.15
   ↓
unwrap over a period of 100 cents      ← otherwise drift across the fold = a jump
   ↓
resample to 20 Hz
   ↓
LINEAR DETREND                          ← this is where the recording's tuning goes
   ↓
cents → percent, Hann window, FFT
   ↓
peak in the 0.3–2.0 Hz band, parabola
   ↓
rpm = f_peak · 60 ± σ_f·60
   ↓
plus band RMS over 0.2–10 Hz = wow & flutter, %
```
