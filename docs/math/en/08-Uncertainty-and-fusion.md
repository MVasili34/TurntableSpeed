# 8. Uncertainty and the fusion of estimates

Code: `Contracts/SpeedEstimate.cs`, `Estimators/SensorFusionEstimator.cs`,
`Estimators/AudioFusionEstimator.cs`, `Dsp/LineFit.cs`

---

## 8.1. The principle: a point value without an interval is meaningless

The task is to measure a deviation of 0.1–1 % with instruments whose own systematic errors reach
1–3 %. Under those conditions the number "33.28 rpm" carries no information by itself:
everything turns on whether it is `± 0.01` or `± 0.7`.

So it is built into the architecture: **`SpeedEstimate` cannot be constructed without a
`StandardError`.** It is a field of the record, not an option.

```csharp
Margin95    = 1.959964 · StandardError      // z_{0.975}
LowerBound95 = rpm − Margin95
UpperBound95 = rpm + Margin95
```

Normality is justified: every estimate is either a regression slope over hundreds of points or
an average over many samples. The central limit theorem applies.

---

## 8.2. Three sources of uncertainty, and their different natures

### Statistical — shrinks as data accumulate

```
σ_stat ~ 1/√n
```

Sensor noise, the autocorrelation background, frame-to-frame scatter. Collect data longer and it
gets more accurate.

### Systematic — never shrinks

| Source | Magnitude | Where |
|---|---|---|
| Gyroscope scale | 2 % (1σ) | [03](03-Gyroscope.md) |
| The recording's tuning | 15 cents ≈ 0.87 % | [06](06-Acoustics-pitch.md) |
| Parabolic interpolation bias | 0.15 of a frame | [05](05-Acoustics-clicks.md) |

These quantities are **never divided by √n**. They add in quadrature with the statistical part
and often swamp it completely:

```
σ = √(σ_stat² + σ_syst²)
```

An example: the gyroscope at 33⅓ rpm over 30 seconds gives `σ_stat ≈ 0.001 rpm`, while
`σ_syst = 33.33·0.02 = 0.67 rpm`. The result is 0.67. The statistics are not visible at all.

This is not pessimism but honesty. Understating the interval here would mean presenting a
knowingly wrong result as an accurate one.

### Residual correlation — masquerading as statistical

The most insidious category. The formula `σ/√n` is valid **only for independent observations**.
Real data are correlated:

* magnetometer phase — slow magnetic wandering;
* gyroscope speed — wow, flutter and cogging torque are real oscillations;
* pitch analysis frames — they overlap by 40 %.

Ignoring this yields an interval that is formally narrow and factually wrong.

**The cure, via effective sample size.** For an AR(1) process with coefficient `ρ` the variance
of the mean is `(1+ρ)/(1−ρ)` times that of white noise. Hence:

```
ρ     = residual autocorrelation at lag 1
n_eff = n · (1 − ρ)/(1 + ρ)
σ     = σ_naive · √(n/n_eff)
```

| ρ | n_eff / n | σ inflation |
|---|---|---|
| 0.0 | 1.00 | ×1.00 |
| 0.3 | 0.54 | ×1.36 |
| 0.5 | 0.33 | ×1.73 |
| 0.8 | 0.11 | ×3.00 |
| 0.9 | 0.053 | ×4.36 |

For `ρ ≤ 0` no correction is applied — negative correlation would narrow the interval, and
exploiting that would be optimistic.

For overlapping frames a direct analogue is used: not all frames are counted, but
`span / frame_duration`, i.e. the number of non-overlapping ones.

---

## 8.3. Propagating uncertainty through transformations

Linearisation (the delta method) is used throughout: `σ_y = |dy/dx| · σ_x`.

| Transformation | Derivative | σ formula |
|---|---|---|
| `rpm = ω·60/2π` | `60/2π` | `σ_rpm = σ_ω · 60/2π` |
| `rpm = 60/T` | `−60/T² = −rpm/T` | `σ_rpm = (rpm/T)·σ_T` |
| `rpm = f·60` | `60` | `σ_rpm = σ_f · 60` |
| `rpm = N·2^(c/1200)` | `N·(ln2/1200)·2^(c/1200)` | `σ_rpm = N·(ln2/1200)·2^(c/1200)·σ_c` |

The first three are linear, so the propagation is exact. The fourth is exponential, and its
derivative is taken **at the measured point** rather than as a constant. At deviations of a few
percent the difference is already visible.

---

## 8.4. Confidence is not the same thing as σ

Two different quantities answering different questions:

| | σ (StandardError) | Confidence |
|---|---|---|
| The question | "how scattered is the number" | "does the method apply at all" |
| Units | rpm | dimensionless, 0..1 |
| Computed | from statistics | heuristically, from diagnostics |

Confidence catches what σ cannot: the phone not being in the plane of rotation, the ellipse
failing to fit, the autocorrelation peak not rising above the background, no tonal music in the
frame. Formally σ could be small while the result is garbage.

**The geometric mean of the individual factors is used everywhere:**

```
confidence = (f₁ · f₂ · ... · f_k)^(1/k)
```

The rationale: one failed component should drag the result down, not be averaged in with the
other good ones. The arithmetic mean of the five factors `(0, 1, 1, 1, 1)` is 0.8 — absurd. The
geometric mean gives 0.

There are also **hard ceilings** where a method is fundamentally limited:

```
gyroscope without scale calibration:  ceiling = 0.45
pitch estimator (always):             ceiling = 0.50
```

And a common factor from the nominal classification:

```
Confidence_final = Confidence_estimator · (0.35 + 0.65 · Confidence_classification)
```

---

## 8.5. Sensor fusion: NOT averaging

Code: `SensorFusionEstimator`

Here there is deliberately **no** inverse-variance fusion. The reason:

> The magnetometer and the gyroscope are not equals. The gyroscope has a 2 % systematic; the
> magnetometer has none by construction. Averaging them means mixing a knowingly biased estimate
> into an unbiased one.

The rule is simple: **the magnetometer wins whenever it has an answer.** The gyroscope stands in
for it only when the magnetometer is physically absent — and then its 2 % is already in its σ,
so the user sees an honestly wide interval.

The gyroscope's role is three things the magnetometer cannot do:

1. instantaneous `ω(t)` (high sample rate);
2. the wow-and-flutter spectrum;
3. spin-up time.

### What is done with disagreement

```
disagreement = (rpm_gyro_RAW − rpm_mag) / rpm_mag · 100
```

Raw, i.e. before `ScaleFactor` is applied. Comparing the calibrated value would be circular:
the calibration equates them by definition.

When `|disagreement| > 2 %`:

* the `MethodsDisagree` flag is raised;
* confidence is multiplied by 0.4;
* **the magnetometer's own value is left alone.**

This matters: a disagreement is **information that something is wrong** (magnetic interference,
the phone sliding, vibration), not grounds for averaging and obtaining something midway between
right and wrong.

### Scale calibration

```
k = ω_mag / ω_gyro_raw
```

under three conditions: the magnetometer's accuracy better than 0.2 %, at least 5 revolutions
accumulated, and the result within `0.9 < k < 1.1`. The details are in [03](03-Gyroscope.md) §5.

---

## 8.6. Acoustic fusion: inverse-variance weighting

Code: `AudioFusionEstimator`

Here the situation is different: the three methods are **independent and share no failure
mechanisms**.

| Method | Mechanism | Fails on |
|---|---|---|
| Click autocorrelation | impulses from defects | a perfectly clean record in a quiet passage |
| Semitone grid | equal temperament | atonal music, percussion, noise |
| Wow spectrum | modulation from eccentricity | a perfectly centred record |

None of these failures entails another. So **their agreement is the strongest evidence of
reliability available**, and combining them is meaningful.

### The mathematics of the fusion

For independent estimates `xᵢ ± σᵢ` the minimum variance is achieved by inverse-variance
weighting (which is both the least-squares optimum and the maximum likelihood solution for
Gaussians):

```
wᵢ = 1/σᵢ²
x̂  = Σ wᵢxᵢ / Σ wᵢ
σ̂  = 1/√(Σ wᵢ)
```

A remarkable property: `σ̂` is **smaller than any individual σᵢ**. Two independent measurements
of equal accuracy combine into one `√2` times more accurate.

The right hierarchy emerges automatically. Suppose the autocorrelation gives
`33.33 ± 0.10 rpm` and the pitch estimator `33.30 ± 0.29` (the 15-cent systematic):

```
w₁ = 1/0.01 = 100        w₂ = 1/0.084 = 11.9
x̂ = (100·33.33 + 11.9·33.30)/111.9 = 33.327
σ̂ = 1/√111.9 = 0.095
```

The pitch estimator moved the result by 0.003 rpm and narrowed the interval slightly. Exactly
the role it deserves: its 15-cent systematic, honestly entered into σ, put it in its place by
itself. No manual priorities were needed.

### The safety catch: only those that agree are fused

```
disagreement = (rpm_pitch − rpm_clicks)/rpm_clicks · 100
```

When `|disagreement| > 1 %` the pitch estimator is **excluded from the combination**, the
`MethodsDisagree` flag is raised and confidence is multiplied by 0.4. The wow spectrum passes
through the same consistency check.

A 1 % threshold against a 0.3 % acoustic target is comfortably outside the expected scatter.

### The bonus for agreement

```csharp
if (parts.Count > 1) confidence *= 1.15;
```

Agreement between mechanisms with no common cause of failure is the strongest evidence
obtainable without leaving the problem (specification §4.5). It is permitted to raise the
confidence. A contradiction does the reverse, and is not smoothed over.

The implementation picks a reference (`click ?? pitch`), adds those that agree with it, and
performs the fusion. The combination's confidence is taken as the **maximum** of the individual
ones (the combination is no worse than the best of its members), after which the bonus and the
penalty are applied.

---

## 8.7. Disagreement between the two modes is also a result

The final level. The sensor mode measures the speed **with the phone on the platter** (150–250 g
of extra mass on the bearing). The acoustic mode measures the speed during ordinary playback.

```
Δ = (rpm_acoustic − rpm_sensors) / rpm_sensors · 100
```

This is **not an error and not noise**: it is a quantitative measure of how far the drive sags
under the additional load. A negative Δ on a belt drive means the phone really is slowing the
platter down.

The specification requires this quantity to be shown to the user, not hidden. It answers the
question that having two modes was for in the first place.

---

## 8.8. Summary of all uncertainty sources

| Method | Statistical | Systematic | Correlation | Typical total σ |
|---|---|---|---|---|
| Magnetometer | SE of the regression slope (Huber, τ scale) | — | ρ of the phase residuals | 0.02–0.05 % |
| Gyroscope | `sd/√n_eff` | **2 % scale** | ρ of the speed series | 2 % before calibration |
| Clicks | ACF background, jitter | **0.15 frame of interpolation** | — | 0.1–0.3 % |
| Pitch | `σ_circ/√n_non-overlapping` | **15 cents of the record's tuning** | frame overlap | ~0.9 % |
| Wow spectrum | band background / curvature | — | — | 0.3–1 % |
