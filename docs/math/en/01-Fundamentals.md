# 1. Fundamentals: quantities, units, classification

Code: `TurntableSpeed.Core/Contracts/NominalSpeed.cs`, `SpeedEstimate.cs`

---

## 1.1. What is actually being measured

A turntable platter is a rigid body rotating about a fixed axis. In a rigid body the
**angular velocity ω is the same at every point**. Linear velocity `v = ω·r` depends on the
radius; angular velocity does not.

The practical consequence, and one worth telling the user: **where exactly the phone lies on
the platter, and how well it is centred, does not affect the accuracy** of the gyroscope or the
magnetometer. Both measure an angular quantity. Being off-centre affects only the mechanical
balance (and the centripetal acceleration — see [04](04-Accelerometer.md)).

The measured quantity is the speed `n` in revolutions per minute (rpm). Every other form is
derived from it:

| Quantity                        | Formula                          | Name in code |
|---------------------------------|----------------------------------|---|
| Revolutions per second (frequency) | `f = n / 60`                  | `RevolutionsPerSecond` |
| Revolution period               | `T = 60 / n`                     | `PeriodSeconds` |
| Degrees per second              | `360 * f = 360 * n / 60 = n · 6` | `DegreesPerSecond` |
| Angular velocity                | `ω = 2π·n / 60`                  | `RadiansPerSecond` |

The inverse conversions (`SpeedMath`):

```
n = ω · 60 / (2π)      RpmFromRadiansPerSecond
n = 60 / T             RpmFromPeriod
```

The three nominal speeds:

| Nominal | rpm (exact) | rev/s | °/s | rad/s | Period, s |
|---|---|---|---|---|---|
| 33⅓ | 100/3 | 0.555556 | 200.000 | 3.490659 | 1.800000 |
| 45 | 45 | 0.750000 | 270.000 | 4.712389 | 1.333333 |
| 78 | 78 | 1.300000 | 468.000 | 8.168141 | 0.769231 |

In the code 33⅓ is stored as `100.0 / 3.0`, not as `33.33`: rounding it there would introduce a
systematic error of 0.01 %, comparable to the 0.1 % accuracy target.

---

## 1.2. Speed and pitch: why cents

> If "cent", "octave" and "equal temperament" are unfamiliar terms, the whole apparatus is
> built up from nothing in [00-Sound-theory.md](00-Sound-theory.md). What follows is only the
> summary.

If the turntable runs `(1 + e)` times faster than nominal, the whole recording plays back
`(1 + e)` times faster, which means every frequency is multiplied by the same factor:

```
f_played = f_recorded · (1 + e)
```

Human pitch perception is logarithmic: the ear responds to **ratios** of frequencies, not to
their differences (going 100→200 Hz and 400→800 Hz is the same musical interval, even though
the differences differ by a factor of four). So the unit of pitch has to be a ratio — and so
that intervals can be added rather than multiplied, the logarithm of that ratio is taken.

The musical unit is the **cent**, one hundredth of an equal-tempered semitone. An octave is a
frequency ratio of `2:1`; it is divided into 12 equal semitones, so a semitone is `2^(1/12)`
(+5.946 %) and a cent is `2^(1/1200)` (+0.0578 %). There are 1200 cents in an octave. Hence:

```
cents = 1200 · log₂(1 + e)
```

and back again

```
e = 2^(cents/1200) − 1
```

Code: `SpeedMath.CentsFromRelativeDeviation` / `RelativeDeviationFromCents`.

Some useful landmarks:

| Deviation | Cents |
|---|---|
| +0.1 % | +1.73 |
| +0.3 % | +5.19 |
| +1 % | +17.23 |
| +2 % | +34.3 |
| +5.95 % | +100 (exactly a semitone) |

For small `e` the expansion `log₂(1+e) ≈ e/ln2` gives the linear approximation
`cents ≈ 1731·e`, i.e. **1 % ≈ 17.3 cents**. The code uses the exact formula anyway: at
deviations of a few percent the linearisation is already wrong in the third digit.

Note the asymmetry: `+1 %` gives `+17.23` cents, while `−1 %` gives `−17.40`. The logarithm is
not symmetric, and that is precisely why the derivative (needed for error propagation, see
[06](06-Acoustics-pitch.md)) is evaluated at the measured point rather than treated as a
constant.

---

## 1.3. Classification: which nominal is this

Given a measured `n`, we need to decide whether this is "33⅓ with a deviation" or "45 with a
deviation", and how confidently.

For each candidate the relative deviation in percent is computed:

```
d_k = (n − N_k) / N_k · 100
```

The candidate with the smallest `|d|` wins. Then there are two questions that have to be
answered honestly.

### Question 1: is this a turntable speed at all?

If `|d_best| > 8 %`, no nominal is assigned at all (`Nominal = null`). The 8 % threshold is
chosen so that **the midpoint between 33⅓ and 45 falls into neither band**:

```
midpoint = (33.333 + 45) / 2 = 39.167
from 33⅓:  (39.167 − 33.333)/33.333 = +17.5 %
from 45:   (39.167 − 45)/45         = −13.0 %
```

Both figures exceed 8 %, so a speed exactly halfway is honestly left unclassified. This is a
direct requirement of the specification (§7.2).

### Question 2: how convincing is the margin over the runner-up

Let `d₁ = |d_best|` and `d₂ = |d_runner-up|`. The separation measure is:

```
separation = (d₂ − d₁) / (d₂ + d₁)
```

This is a normalised contrast: it equals 0 when `d₁ = d₂` (equidistant — complete uncertainty)
and tends to 1 when the winner is much closer. Plus a penalty for the winner itself being
rather far away:

```
proximity  = 1 − min(1, d₁ / tolerance)
confidence = separation · (0.5 + 0.5 · proximity)
```

The `(0.5 + 0.5·proximity)` factor is built so that proximity can at most halve the confidence:
the main argument is still the margin over the competitor, not absolute closeness to the
nominal (a turntable really can be running 5 % off).

---

## 1.4. The result contract

```csharp
sealed record SpeedEstimate(
    double RevolutionsPerMinute,   // point estimate
    double StandardError,          // 1σ, same units
    NominalSpeed? Nominal,         // 33⅓ / 45 / 78 / null
    double DeviationPercent,       // from Nominal
    double Confidence,             // 0..1
    IReadOnlyDictionary<string, double> Diagnostics);
```

Derived properties:

```
DeviationCents        = 1200·log₂(1 + DeviationPercent/100)
Margin95              = 1.959964 · StandardError      // half of the 95 % interval
LowerBound95          = rpm − Margin95
UpperBound95          = rpm + Margin95
StandardErrorPercent  = StandardError / rpm · 100
```

The 1.959964 coefficient is the `z_{0.975}` quantile of the standard normal distribution.
Normality is justified here: every estimate in the project is either a regression slope over
hundreds of points or an average over many samples, so the central limit theorem applies.

`SpeedEstimate.FromRpm` does not merely assemble the fields — it multiplies the estimator's
confidence by the classification confidence:

```
Confidence = confidence_estimator · (0.35 + 0.65 · confidence_classification)
```

The logic: even if the estimator did a perfect job, if the speed sits exactly between two
nominals the final confidence must drop — but not to zero, because the `rpm` number itself was
measured beautifully. Hence the floor of 0.35.

---

## 1.5. The accuracy target and what it means

| Mode | Target | In cents | In rpm at 33⅓ |
|---|---|---|---|
| Sensors | ±0.1 % | ±1.7 | ±0.033 |
| Acoustic | ±0.3 % | ±5.2 | ±0.100 |

For comparison: the typical audible threshold for mistuning, to a musical ear, is on the order
of 5–10 cents on simultaneously sounding tones (the mechanism is beating — see
[00 §0.9](00-Sound-theory.md)). So the acoustic mode aims roughly at the threshold of
audibility, and the sensor mode noticeably below it.

The same numbers explain why **the gyroscope cannot be trusted in absolute terms**: its
factory scale error of 1–3 % is 17–52 cents, 10 to 30 times the accuracy target. In detail in
[03](03-Gyroscope.md).
