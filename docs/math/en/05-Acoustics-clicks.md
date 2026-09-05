# 5. Acoustics: click autocorrelation — the primary acoustic method

Code: `Estimators/ClickPeriodicityEstimator.cs`, `Dsp/Autocorrelation.cs`,
`Dsp/Envelope.cs`, `Dsp/Biquad.cs`, `Estimators/AudioProcessingDetector.cs`

---

## 5.1. The physics: why clicks are the ideal period meter

A vinyl record is not perfect. A speck of dust in the groove, a scratch, a chip, wear — each
produces a short impulse at the cartridge's output. The key property:

> **A defect is physically attached to a specific place on the record, so the stylus passes
> through it exactly once per revolution.**

This yields a sequence of impulses whose period is **exactly equal to the rotation period**. No
calibration, no reference, no assumptions about the content of the recording.

Compare with the competing methods:

| Method | What must be known in advance |
|---|---|
| **Clicks** | **nothing** |
| Pitch analysis | how the band was tuned, how the mastering was done |
| Test record | you need a test record |

Clicks work on the lead-in groove, in the gaps between tracks and under music — because their
spectrum (an impulse is broadband) differs from that of music in the high-frequency region, and
because they are impulsive while music is stationary.

The lag search range 0.6–2.2 s covers 27–100 rpm, i.e. all three nominals with a wide margin
for a malfunctioning turntable.

---

## 5.2. The precondition: an unprocessed input

This is **not an implementation detail but a condition for the mode to work at all.**

Android's platform processing of the microphone input is designed for voice calls and does
exactly what is fatal for us:

| Processing | What it does | Why it is fatal |
|---|---|---|
| **Noise suppressor** | attenuates stationary noise | a click is a short non-stationary burst and fits the definition of "noise" |
| **AGC** | levels the signal | breaks the amplitude relations between clicks, making the envelope uninformative |
| **Echo canceller** | adaptive filtering | introduces a varying delay — direct corruption of the period |

Hence:

* Android: `MediaRecorder.AudioSource.Unprocessed` (where supported; check
  `PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED`), otherwise `MIC`;
* explicitly disable `NoiseSuppressor`, `AutomaticGainControl`, `AcousticEchoCanceler`;
* 44100 or 48000 Hz, mono, PCM.

### But the platform may be lying

`AudioProcessingDetector` watches **the signal itself** and looks for the fingerprints of
processing.

**The noise-gate fingerprint.** A gate drops quiet passages to digital zero. A microphone in a
room cannot do that — it always has acoustic and thermal noise.

```
noiseFloor  = 5th percentile of short-term RMS (in dB)
gateDepth   = median − noiseFloor
silentFrac  = fraction of frames with RMS < 3e-5  (≈ −90 dBFS)

a gate is suspected if:
    silentFrac > 2 %
  OR  (noiseFloor < −80 dBFS  AND  gateDepth > 45 dB)
```

The −80 dBFS threshold: a real room through a phone microphone never gives that much silence.

**The AGC fingerprint.** AGC holds the long-term level constant. The naive test — "is the spread
of the slow level small?" — does not work: it needs a scale to compare against.

The arithmetic of the smoothing itself supplies the right scale. Averaging `w` frames whose
spread is σ leaves `σ/√w` **even for a perfectly stable source**. Therefore:

```
expected_slow_σ = frameStd / √w
slowExcess      = slowStd / expected_slow_σ
```

`slowExcess ≈ 1` means the long-term level carries nothing beyond the arithmetic of smoothing —
and that is the trace of AGC. Real material gives several times more.

```
AGC is suspected if  slowExcess < 2.5  AND  frameStd > 4 dB
```

The second condition is essential: the test only means anything on material that has something
to level. On stationary noise it would fire falsely.

Plus the usual checks: clipping (`|x| ≥ 0.999` more often than 1e-4), too quiet a signal
(RMS < −55 dBFS), a DC component (> 0.01), an unexpected sample rate.

The distinction between `IsClean` and `IsUsable`: the platform's refusal to set the
"unprocessed" flag is not by itself fatal — what matters is whether the signal actually looks
processed.

---

## 5.3. Step 1 — extracting the click envelope

### A 5 kHz high-pass, 4th-order Butterworth

An impulse has a broad spectrum; music is concentrated lower down. Cutting everything below
5 kHz sharply improves the clicks-to-music ratio.

The implementation is a cascade of biquads (see [09-DSP](09-DSP-primitives.md) §3). A 4th-order
Butterworth is two second-order sections with quality factors

```
Q_i = 1 / (2·cos(π(2i+1)/(2N))),  N = 4  →  Q₀ = 0.5412, Q₁ = 1.3066
```

Order 4 gives a 24 dB/octave rolloff — an octave lower (2.5 kHz) music is already 24 dB down.

Protection for devices with a low sample rate: the cutoff is capped at `0.4·f_s` so it cannot
run past Nyquist.

The filter is **streaming and keeps its state across blocks** — otherwise every block boundary
would produce a transient, that is an artificial "click" with a period equal to the block size.
That would be a disaster for the method. On a gap in the stream (>50 ms, or half a block) the
state is reset and the window is cleared.

### The RMS envelope, 5 ms frames

```
env[j] = √( (1/H) Σ x²[jH .. jH+H−1] ),   H = round(f_s · 0.005)
```

`RmsEnvelopeFollower` accepts audio in blocks of arbitrary size and emits exactly one envelope
sample per `H` input samples. The envelope rate stays exactly `f_s/H` no matter how the platform
chops up the stream.

The 5 ms choice is a compromise:

* shorter → better time resolution, but more noise in each frame;
* longer → the click smears out and loses prominence above the background.

The raw lag resolution is 5 ms, which at a period of 1.8 s is 0.28 %. Not enough for the ±0.3 %
target, so sub-sample peak interpolation (step 4) is mandatory.

Envelope timestamps are computed from a **global counter of consumed samples**, not by
accumulating local offsets: that way the timestamps do not drift over long recordings.

### The alternative: the Hilbert transform

`Dsp/Envelope.cs` also contains the exact analytic envelope via Hilbert (see
[09-DSP](09-DSP-primitives.md) §5). This estimator uses RMS: it is cheaper, streams, and gives
the required decimation for free.

---

## 5.4. Step 2 — suppressing the stationary component

The envelope contains both music (stationary) and clicks (impulsive). They are separated by a
running median:

```
residual[i] = env[i] − median(env[i−w/2 .. i+w/2]),   w = 0.25 s
residual[i] = max(0, residual[i])
```

**Why a median and not a mean.** The median is a robust statistic: a short strong impulse barely
shifts it. A moving average, by contrast, rises at the click's location and partially subtracts
the click from itself.

**Why a 0.25 s window.** It must be much longer than a click (5–20 ms) so the median does not
notice it, and much shorter than a revolution (0.77–1.8 s) so the median can keep up with
changes in the music. 0.25 s sits right between them.

**Why negative values are discarded.** A dip of the envelope below the median is music decaying,
not a click. It carries no information about the rotation period, and would contribute only
noise to the autocorrelation. Clipping at zero also turns the signal into a sequence of
near-deltas, whose autocorrelation is as sharp as possible.

The running median is implemented with an ordered list and binary search: `O(n·log w)`, with no
special handling of the edges needed (the window is clamped).

---

## 5.5. Step 3 — autocorrelation

```
r(τ) = Σ_i residual[i]·residual[i+τ]
```

It is computed via FFT using the Wiener–Khinchin theorem (power spectrum ↔ autocorrelation):

```
X    = FFT(residual, zero-padded to 2^k ≥ n + maxLag + 1)
S    = |X|²
r    = IFFT(S)
```

Complexity `O(n log n)` instead of `O(n·maxLag)`. Zero padding is mandatory — otherwise the
result is the **circular** autocorrelation, in which the end of the record wraps onto the
beginning and creates spurious peaks.

### Biased normalisation — a deliberate choice

There are two normalisation options:

```
unbiased:  r(τ) / (n − τ)     ← divide by the number of actually overlapping samples
biased:    r(τ) / n           ← divide by the full length
```

The code uses the **biased** one, and that matters.

The unbiased form is formally more correct (it gives an unbiased estimate of the
autocorrelation function), but it has a destructive side effect: it **compensates for the
shrinking overlap**, so the autocorrelation comb does not decay with lag. Then the peak at `2T`
can end up higher than the peak at `T` through random fluctuation (at long lags the estimate is
noisier — fewer terms are averaged). The result: **a turntable at 60 rpm reads as 30 rpm** —
exactly half.

Biased normalisation produces a natural `(n−τ)/n` decay, which tilts the comb towards short
lags. The true period beats its own multiples.

Everything is then divided by `r(0)`, so `r(0) = 1` and the values are comparable across
windows.

The mean is subtracted before the computation — otherwise the DC component would produce an
enormous parabola against which the peaks are invisible.

---

## 5.6. Step 4 — which peak is the period

This is the most involved logic in the estimator, and it solves a real problem.

### The comb problem

If there are **c** defects on the record, the clicks come at intervals of `s = T/c`, not `T`.
The autocorrelation is a **comb** with teeth at multiples of `s`, not of `T`.

The raw maximum can land on any tooth. The example from the comment in the code: 45 rpm
(`T = 1.333 s`), three clicks per revolution → `s = 0.444 s`. The maximum can end up at
`2s = 0.889 s`. Neither doubling nor halving gets you home from there: the true period is `3s`,
while the maximum was sitting on `2s`.

### The solution: measure the comb's step first

**Step 1. Find all the teeth.** Local maxima of the autocorrelation exceeding
`0.45 · raw_peak`, refined by parabolic interpolation.

**Step 2. Find the largest step `s` of which every tooth is a multiple.** This is a real-valued
analogue of the GCD. The divisors of the shortest tooth are tried:

```
for divisor = 4, 3, 2, 1:
    s = shortest_tooth / divisor
    error = mean| tooth/s − round(tooth/s) |    over all teeth
choose the s with the smallest error
```

Trying divisors is necessary because **the true step may be shorter than the minimum lag
searched**: at 78 rpm with three clicks per revolution `s = 0.769/3 = 0.256 s`, while the search
starts at 0.6 s. Without the division, the step would never be found.

On an exact tie the first divisor tried wins, i.e. **the finest step**. That is the safe
direction: a finer step merely offers more candidates, and each of them must still show real
autocorrelation.

**Step 3. The candidates are the multiples of `s`.** For each `m·s` the local maximum is refined
within ±30 ms, and a "support" is computed:

```
support = candidate_height / raw_peak_height
```

Candidates with `support < 0.45` are discarded: there simply is no periodicity there.

**Step 4. Ranking against the nominal grid.**

```
score = support · (0.25 + 0.75 · classification_confidence(60/period))
```

Here independent information is brought to bear: **no two of the periods 1.8 / 1.333 / 0.769 s
are related by a small integer ratio.**

```
1.8 / 1.333 = 1.35     1.8 / 0.769 = 2.34     1.333 / 0.769 = 1.73
```

Neither 2, nor 3, nor 3/2. So if a candidate lands on the nominal grid while its harmonics do
not, that is strong evidence.

The formula reads: "support says there really is periodicity here; the nominal grid says a
turntable really could be running at that speed". The `0.25` factor is a floor: even a candidate
off the grid is not zeroed out, because a turntable can be seriously broken.

**Step 5. Asymmetry in favour of the shorter period.** Candidates are scanned from short to
long, and a long one displaces a short one only if

```
score_long > score_short · 1.1
```

The rationale: a peak at `2T` is a harmonic of the period, not the period. The "twice too slow"
error is far more likely than "twice too fast", and the 10 % margin insures against it.

---

## 5.7. Step 5 — sub-sample refinement and the detection threshold

### Parabolic interpolation

The raw resolution is 5 ms, i.e. 0.28 % at 1.8 s. Not enough. A parabola is fitted through the
three points around the maximum (see [09-DSP](09-DSP-primitives.md) §4):

```
a = ½(y₋₁ + y₊₁) − y₀
b = ½(y₊₁ − y₋₁)
δ = −b/(2a)          the vertex offset, in samples
A = y₀ + ½·b·δ       the height
c = −a               the curvature
```

This improves the resolution by roughly an order of magnitude.

### The "peak or not a peak" threshold

```
noiseFloor = std(autocorrelation outside ±50 ms of the peak)
if  peak / noiseFloor < 5  →  the estimator stays silent
```

A bump three or four sigma above the background is a fluctuation, not a detection. Better to go
on saying nothing than to publish a period fished out of noise — with a narrow interval around
it, no less. This follows directly from the principle that every number comes with an honest
uncertainty.

---

## 5.8. The period's uncertainty

Three independent mechanisms, and **the worst of the three** is taken — because they do not
average each other out.

### 1. Background scatter

A perturbation `ε` of the autocorrelation values shifts the parabola's vertex by something of
order `ε/(2c)`, where `c` is the curvature. The autocorrelation background is that perturbation:

```
σ₁ = noiseFloor / (2·curvature) · Δt_envelope
```

### 2. Click jitter

A defect has finite extent, and record eccentricity shifts the moment of passage — so the peak
acquires a finite half-width `w`. From the parabola's shape `y ≈ A − c·x²`, where the value
halves at the half-width:

```
c = A/(2w²)   →   w = √(A/(2c))
```

The centre of a peak built from `N` revolutions is localised to

```
σ₂ = w · √2 / √N · Δt_envelope
```

This dominates on a worn or eccentric record.

### 3. Interpolation bias — a systematic error

An autocorrelation peak one or two frames wide **is not a parabola**. Fitting a parabola to a
non-parabola pulls the answer towards the sample grid by a fixed fraction of a frame, however
long the recording runs:

```
σ₃ = 0.15 · Δt_envelope       (0.15 measured on synthetic clicks)
```

This is a **systematic** error. It does not shrink as data accumulate, so it forms a **floor**
under the reported uncertainty. The estimator has no right to claim an accuracy better than the
interpolation method allows, however long it has been running.

```
σ_T = max(σ₁, σ₂, σ₃)
```

### Propagating to rpm

```
n = 60/T   →   dn/dT = −60/T² = −n/T
σ_n = (n/T) · σ_T
```

---

## 5.9. The confidence estimate

```
precision   = 1 − (σ_n/n)/0.003 · 0.5        the ±0.3 % target
prominence  = (peak/background − 4)/8        below 4σ it is not a peak; by 12σ it is beyond doubt
coverage    = revolutions/12
unambiguous = 1 − |support − 1|·0.5          the winner over its rival — less certain

confidence = (precision · prominence · coverage · unambiguous)^(1/4)
```

The geometric mean again: any factor that fails drags the result down.

---

## 5.10. Summary

```
audio 44.1/48 kHz (unprocessed!)
        ↓
   AudioProcessingDetector: gate? AGC? clipping?     → warnings
        ↓
   5 kHz 4th-order Butterworth high-pass (streaming, stateful)
        ↓
   RMS envelope, 5 ms frames → ~200 Hz
        ↓
   TimeWindow 40 s
        ↓
   subtract the running median (0.25 s), clip at zero
        ↓
   FFT autocorrelation, lags 0.6–2.2 s, BIASED normalisation
        ↓
   find the comb's teeth → step s → candidates m·s
        ↓
   ranking: support × closeness to the nominal grid, short periods favoured
        ↓
   parabolic interpolation of the vertex
        ↓
   peak/background ≥ 5, otherwise stay silent
        ↓
   σ_T = max(background, jitter, interpolation bias)
        ↓
   n = 60/T ± (n/T)·σ_T
```
