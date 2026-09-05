# 6. Acoustics: pitch deviation — fast, but with a caveat

Code: `Estimators/PitchGridEstimator.cs`, `Dsp/PhaseMath.cs` (circular statistics)

---

## 6.1. The idea and its fundamental limitation

> The background — equal temperament, the semitone grid, cents, harmonics — is covered in
> [00-Sound-theory.md](00-Sound-theory.md), especially §0.6–0.8.

If the turntable runs `(1+e)` times fast, every frequency in the recording is multiplied by
`(1+e)`. Western music is almost always written in equal temperament: the note frequencies sit
on a logarithmic grid with a step of one semitone = `2^(1/12)`. So we can look at **how far the
music sits from that grid** and obtain the deviation — in seconds, with no clicks needed at all.

The method gives a result instantly and is suited to a live indicator.

### The caveat that cannot be removed

The method measures **the pitch of what was pressed into the record**, not the speed of the
platter. There are at least three terms:

```
measured_deviation = turntable_error
                   + tuning_of_the_recording (the band may not have played at 440 Hz)
                   + mastering_drift (tape machine, cutting speed)
```

Historical facts that make this a real problem: before A=440 was standardised, 435, 432 and
444 Hz were in use; tape machines drifted during mixdown; many classic 1960s recordings deviate
noticeably from 440.

Separating these terms from inside the recording is **impossible** — that is not a shortcoming
of the algorithm but a property of the problem.

Hence, in the code:

```csharp
public const string Caveat =
    "Pitch deviation mixes the turntable's speed error with the record's own tuning: ...";
```

and the constants

```csharp
RecordTuningUncertaintyCents = 15.0    // a systematic, always in σ
ConfidenceCeiling = 0.5                // the confidence ceiling
```

15 cents ≈ 0.87 % is an estimate of the typical spread of tuning across real records. It is
**included in the reported σ**, so in inverse-variance fusion this estimator automatically gets
a small weight against the autocorrelation one, instead of winning on the strength of its narrow
statistical interval. The flag `mixesRecordTuning = 1` is always written into the diagnostics so
that the UI cannot display the number without the warning.

---

## 6.2. Frame analysis

| Parameter | Value | Rationale |
|---|---|---|
| Frame length | 4096 | at 48 kHz that is 85 ms, resolution 11.7 Hz |
| Hop | 50 ms | a requirement of the specification, ~40 % overlap |
| Window | Hann | a compromise between main-lobe width and sidelobe suppression |
| Band | 100–4000 Hz | lower and the bins are too coarse; higher and partials are denser than a semitone |

A ring buffer accumulates samples; a frame is emitted every `hop` samples.

**A frame's timestamp is placed at its centre**, not at its end. Downstream those timestamps are
used by the wow spectrum ([07](07-Wow-and-flutter.md)), which cares about *when the music
sounded*, not about when the buffer filled up. A half-frame error (43 ms) at 0.556 Hz is 8° of
phase, which is already noticeable.

Frame processing:

```
1. frame RMS (for weighting and the level indicator)
2. subtract the mean          (remove the DC component)
3. multiply by a Hann window
4. FFT → amplitude spectrum
5. divide by the window's coherent gain (0.5 for Hann)
```

Dividing by the coherent gain restores true amplitudes: the Hann window halves the signal, and
without the correction a sine of amplitude A would read as A/2.

A gap in the stream (>50 ms, or half a block) clears the buffers: a dropout of audio would
create a spurious discontinuity in the cents series, which the wow spectrum would take for
modulation.

---

## 6.3. Folding the spectrum onto one semitone

### Why peaks and not all bins

A bin between partials carries **spectral leakage** (the window's sidelobes), not pitch.
Including every bin would dilute the estimate with noise. So only the local maxima of the
amplitude spectrum are taken.

Peaks weaker than `2 %` of the strongest in the frame are discarded, and at most the 40
strongest are kept: a dense noisy spectrum would otherwise smear out the circular mean.

### Refining a peak's frequency — on the logarithm of the amplitude

```csharp
var refined = PeakInterpolation.Parabolic(
    Log(amplitudes[index - 1]),
    Log(amplitudes[index]),
    Log(amplitudes[index + 1]));
```

This matters. The Hann window's main lobe is close to a parabola on a **logarithmic** scale (its
shape resembles a Gaussian, whose logarithm is an exact parabola), and not on a linear one.
Interpolating on log amplitudes gives a noticeably smaller bias.

Accuracy here is essential: at a resolution of 11.7 Hz and a frequency of 440 Hz, one bin is
46 cents. Without sub-bin refinement the method would make no sense at all.

### Converting into a deviation from the grid

```
semitones_from_A4 = 12 · log₂(f / 440)
cents = (semitones − round(semitones)) · 100
```

Rounding to the nearest whole semitone throws away the information about *which* note this is
and leaves only the deviation from the grid, folded into **(−50, +50] cents**.

That is exactly why the method works on any music without knowing the key: every note of equal
temperament gives the same deviation from the grid.

### The peak's weight

```
w = log(1 + A/threshold)
```

A logarithmic weight, as the specification requires. The point: a loud bass note should not
outvote the rest of the chord. A linear weight would hand 90 % of the vote to the strongest
partial; the logarithm evens out the contributions of loud and quiet without erasing the
difference entirely.

---

## 6.4. The circular mean: why the ordinary one will not do

The deviations are folded into (−50, +50]. The quantity is **cyclic**: +49 and −49 cents are
almost the same thing (2 cents apart across the boundary), not 98 apart.

An ordinary mean gives nonsense here. The classic example: two peaks at +49 and −49. The
arithmetic mean is 0, whereas the true mean is ±50.

The right tool is **circular statistics**. The values are converted to angles and averaged as
unit vectors:

```
θᵢ = centsᵢ · 2π/100

C = Σ wᵢ cos θᵢ / Σ wᵢ
S = Σ wᵢ sin θᵢ / Σ wᵢ

θ̄ = atan2(S, C)                      the circular mean
R = √(C² + S²)                        the resultant length, 0..1
cents = θ̄ · 100/(2π)
```

Code: `PhaseMath.CircularMeanOverPeriod` — a generalisation to an arbitrary period instead of
2π.

### The resultant length as a quality measure

`R` is the length of the mean unit vector:

* `R → 1` — the angles are concentrated, the estimate is reliable;
* `R → 0` — the angles are spread uniformly, the mean direction is **undefined**.

This is a key diagnostic: an `R` near zero means the frame contained no tonal music (percussion,
noise, silence), and its cents value means nothing. Frames with `R < 0.15` take no part in the
averaging at all.

### The circular standard deviation

```
σ_angular = √(−2·ln R)
σ_cents = σ_angular · 100/(2π)
```

A standard result of circular statistics: for a von Mises distribution at high concentration
`R ≈ exp(−σ²/2)`, whence `σ = √(−2 ln R)`. The formula neatly diverges to infinity as `R → 0` —
which is correct: under complete dispersion the uncertainty is infinite.

---

## 6.5. Averaging across frames, and honest statistics

Frames are collected into a 6 s window and averaged with the same circular mean, weighted by
`R²` (a quadratic weight: a concentrated frame deserves disproportionately more trust).

### The correction for frame overlap

Frames 85 ms long arrive at a hop of 50 ms — they **overlap by 40 %**. Neighbouring frames
partly contain the same samples, so they are not independent. Counting them all as independent
observations understates the interval.

```
n_eff = window_span / frame_duration        (not the number of frames!)
σ_stat = σ_cents / √n_eff
```

That is, only **non-overlapping** frames are counted. For a 6 s window and an 85 ms frame that
is 70 rather than the 120 frames actually present.

### The final σ

```
σ_total = √( σ_stat² + 15² )
```

The 15 cents are the systematic from the record's own tuning. With `σ_stat` typically a few
cents, **the systematic dominates completely** — and rightly so: no amount of averaging reduces
our ignorance of the recording's tuning.

---

## 6.6. From cents to rpm

The estimator measures a **deviation**, not a speed. To report rpm it needs to know which
nominal the deviation is from — and pitch cannot tell 33⅓ from 45 even in principle (a record
played at 45 instead of 33⅓ sounds 35 % faster = 5.2 semitones, which is simply a transposition
into another key; the semitone grid is still a grid).

So `AssumedNominal` comes from outside — from the autocorrelation estimator or from the user.
Without it `Current` stays `null`, but the cents indication still works.

```
relative = 2^(cents/1200) − 1
rpm = rpm_nominal · (1 + relative)
```

### Error propagation

The derivative is needed:

```
d(relative)/d(cents) = (ln2/1200) · 2^(cents/1200)
```

Derivation: `relative = 2^(c/1200) − 1`, hence `d/dc = 2^(c/1200) · ln2 / 1200`.

```
σ_rpm = rpm_nominal · (ln2/1200) · 2^(cents/1200) · σ_cents
```

The derivative is evaluated **at the measured point** rather than treated as a constant —
because the dependence is exponential, and at large deviations `2^(c/1200)` differs noticeably
from 1. For small deviations this reduces to the familiar `σ_rpm ≈ rpm · σ_cents/1731`.

---

## 6.7. Confidence

```
concentration = (R − 0.2)/0.6              is there any tonal material at all
precision     = 1 − σ_stat/10              statistical accuracy
coverage      = usableFrames/100           have enough frames accumulated

confidence = (concentration · precision · coverage)^(1/3) · 0.5
```

The `0.5` factor is a **hard ceiling**. However concentrated the pitch may sit, the recording's
tuning is unknown. The estimator has no right to claim high confidence.

---

## 6.8. Summary

```
audio
   ↓
ring buffer 4096, hop 50 ms, timestamp at the frame's centre
   ↓
subtract the mean → Hann window → FFT → amplitudes / coherent gain
   ↓
local maxima in 100–4000 Hz, stronger than 2 % of the maximum, up to 40 of them
   ↓
refine the frequency with a parabola ON THE LOGARITHM of the amplitude
   ↓
cents = (12·log₂(f/440) − round(...)) · 100 ∈ (−50, +50]
   ↓
circular mean over the frame with weights log(1 + A/thr) → (frame_cents, R)
   ↓
6 s window, circular mean across frames with weights R², discarding R < 0.15
   ↓
σ = √( (σ_circ/√n_non-overlapping)² + 15² )   ← the tuning systematic dominates
   ↓
rpm = nominal·(1 + 2^(c/1200) − 1),  confidence ceiling 0.5
```
