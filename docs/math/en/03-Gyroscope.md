# 3. The gyroscope estimator — fast, but not absolute

Code: `Estimators/GyroscopeSpeedEstimator.cs`, `Estimators/GyroZeroCalibrator.cs`

---

## 3.1. The physics of a MEMS gyroscope and the nature of its error

A MEMS gyroscope is a vibrating micromechanical structure that measures the **Coriolis force**.
A mass oscillates inside it at a known velocity `v`; when the housing rotates at angular
velocity `ω`, the mass experiences

```
F_Coriolis = −2m (ω × v)
```

This force is perpendicular to the oscillation and proportional to `ω`. Its capacitive
deflection is measured and converted into rad/s.

Two fundamental kinds of error follow.

### Zero error (bias)

The sensor reports a non-zero value while completely stationary. The causes: mechanical
imperfections of the suspension, parasitic capacitive coupling, thermal drift. The magnitude is
a few degrees per second on cheap sensors.

**The good news: this is an additive error, and it can be measured.** Record 2–3 seconds with
the platter stopped and average.

### Scale-factor error

The sensor reports `ω_measured = k · ω_true`, where `k ≠ 1`. The causes: geometric spread in
manufacturing, deviation of the vibrating structure's resonant frequency, temperature. MEMS
gyroscope datasheets honestly quote **1–3 % (1σ) from the factory**.

**The bad news: this is a multiplicative error, and it cannot be measured on a stationary
platter.** At `ω = 0` it does not manifest at all — `k·0 = 0`.

### Why this kills the gyroscope as an absolute instrument

We are measuring speed deviations on the order of **0.1–1 %**. The scale error is **1–3 %**.
The instrument has a systematic error 10 to 30 times larger than the quantity being measured.

In cents: a 2 % scale error is 34 cents, while the sensor mode's target is ±1.7 cents.

This does not make the gyroscope useless. It remains an excellent instrument for **relative
changes**: the scale factor `k` is constant over the course of a measurement, so wow and
flutter, spin-up and instantaneous fluctuations show up beautifully. It is only the absolute
value that is bad.

### The two instruments compared

| | Magnetometer | Gyroscope |
|---|---|---|
| What it measures | frequency (phase accumulation) | amplitude (Coriolis force) |
| Its reference | the phone's crystal, a few ppm | the factory scale factor, 1–3 % |
| Sample rate | 50–100 Hz | 200–500 Hz |
| Absolute accuracy | excellent | poor |
| Response to change | slow (needs an arc) | instantaneous |
| Interference | magnetic | vibration |
| **Role** | **reference** | **secondary, diagnostic** |

---

## 3.2. Zero calibration

Code: `Estimators/GyroZeroCalibrator.cs`

The user is **actively asked** to stop the platter and not touch the phone for 2.5 seconds.
Then, component-wise:

```
bias = ( mean(ωₓ), mean(ω_y), mean(ω_z) )
σ    = ( sd(ωₓ),   sd(ω_y),   sd(ω_z)   )
```

Every incoming sample is then corrected: `ω_corrected = ω_raw − bias`.

### Checking that the data really are stationary

A bias captured while the user was still lowering the phone onto the platter is **worse than no
bias at all**: it introduces a systematic error nobody suspects. Hence two checks:

```
|bias| > 0.05 rad/s          → "the platter is still turning"
max(σₓ, σ_y, σ_z) > 0.03     → "the phone is being held in the hand"
```

The first catches a platter that was not switched off (0.05 rad/s is 0.5 rpm, certainly less
than any nominal, but certainly more than the zero of a healthy sensor). The second catches
hand tremor: on a phone lying still the gyroscope noise is an order of magnitude smaller.

If a check fails, the UI must ask for a retry rather than quietly accept the result. `Progress`
(0..1) drives the indicator for accumulating the required 2.5 s.

---

## 3.3. The rotation axis: not necessarily Z

The phone does not lie perfectly flat, and the platter's rotation axis does not coincide with
the sensor's Z axis. The angular velocity vector `ω` points along the real rotation axis
(always, by the definition of the angular velocity of a rigid body).

Here the **mean direction** (`PrincipalAxes.MeanDirection`) is used, not PCA:

```
ê = mean(ωᵢ) / |mean(ωᵢ)|
```

The difference matters. For the magnetometer the field vector **rotates**, so its mean is
uninformative and PCA is needed (we are looking for a plane). For the gyroscope the vector `ω`
is **constant** — it points along the rotation axis the whole time. So the mean is the direction
we want, and — importantly — **it preserves the sign**, and therefore distinguishes the
direction of rotation. PCA loses the sign (an eigenvector is defined only up to sign).

The instantaneous speed is the projection onto this axis:

```
ω_projected(t) = ω(t) · ê
```

The projection, not the magnitude `|ω|`: the magnitude is always positive and sums the noise of
all three axes, overstating the result (for a true speed of zero, `E|ω_noise| > 0`). The
projection is unbiased and signed.

---

## 3.4. The speed estimate and its error

```
ω̄ = mean(ω_projected)
sd = std(ω_projected)
```

### The statistical component

Neighbouring gyroscope samples are **correlated**: wow and flutter, cogging torque,
eccentricity — these are real physical speed fluctuations, not measurement noise. As with the
magnetometer, an effective sample size is introduced:

```
ρ     = autocorrelation of ω_projected at lag 1
n_eff = n · (1 − ρ) / (1 + ρ)
SE_stat = sd / √n_eff
```

Without this correction the interval would shrink as `1/√n` on data that are nowhere near
independent: at 400 Hz over 30 seconds `n = 12000`, and the naive formula would give a
fantastically narrow interval around a systematically biased value.

### The systematic component — the one that dominates

```
SE_scale = rpm · 0.02        (1σ = 2 %, until calibrated)
```

They add in quadrature (being independent):

```
SE = √( SE_stat² + SE_scale² )
```

**This is the single most important place in the whole estimator.** Before calibration
`SE_scale` swamps everything else by orders of magnitude: at 33⅓ rpm it is `±0.67 rpm`, whereas
`SE_stat` is in the thousandths. The estimator reports an honest "33.3 ± 0.7 rpm", and any
inverse-variance fusion automatically gives it a negligible weight against the magnetometer's
`±0.02 rpm`.

A systematic error is not removed by averaging — it does not shrink with time. So it is never
divided by `√n`.

There is also a hard ceiling on confidence:

```
ceiling = IsScaleCalibrated ? 1.0 : 0.45
```

The absolute reading of an uncalibrated MEMS gyroscope does not deserve high confidence, no
matter how quiet the data may look.

---

## 3.5. Calibrating the scale factor against the magnetometer

Code: `SensorFusionEstimator.CalibrateGyroScaleFactor`

The magnetometer gives the true `ω` with no scale error. Therefore:

```
k = ω_magnetometer / ω_gyroscope_raw
```

Three safety catches, each of them mandatory:

1. **The reference must be accurate.** The magnetometer is admitted to the calibration only at
   a relative accuracy better than 0.2 % (`SE/rpm < 0.002`). Otherwise we would transfer its
   noise into the calibration.

2. **The reference must be accumulated.** At least 5 revolutions are required. With fewer, the
   platter's eccentricity has not been averaged out.

3. **The raw reading, not the calibrated one.** The formula takes `omegaRawRadPerSecond` — the
   value **before** `ScaleFactor` is applied. Otherwise the calibration would feed on itself:
   apply `k`, measure, obtain `k' = k_mag/(k·ω)`, apply again — and the coefficient would crawl
   off anywhere.

4. **A sanity check:** only `0.9 < k < 1.1` is accepted. A deviation greater than ±10 % is not
   a scale-factor error (that is 1–3 %) — it is a broken measurement: the wrong sensor, the
   wrong axis, extraneous vibration.

After a successful calibration `SE_scale` goes to zero and the gyroscope becomes a full
instrument. `ScaleFactor` is shown in the diagnostics — an interesting characteristic of that
particular phone.

---

## 3.6. Instantaneous speed and spin-up

### Instantaneous speed

A moving average of the projection over the last second (`SmoothingSeconds = 1.0`), multiplied
by `ScaleFactor`. This is what the `ω(t)` graph on screen plots.

### The spin-up trace

In parallel, a trace of the magnitude `|ω|` is kept, smoothed by an exponential filter:

```
α = 1 − exp(−Δt/τ),   τ = 0.2 s
ema ← ema + α·(|ω| − ema)
```

The form `1 − exp(−Δt/τ)` rather than a constant — because **samples arrive unevenly**. Android
callbacks jitter, and a fixed α would give different effective smoothing at different intervals.
This form is the exact solution of `dy/dt = (x − y)/τ` over a step `Δt`, so the time constant
stays exactly 0.2 s regardless of jitter.

The trace is decimated to 20 Hz — for measuring spin-up time, 50 ms resolution is more than
enough.

### Computing the spin-up time

```
plateau   = |ω̄|                                  (the settled speed)
tolerance = plateau · 1 %
```

The **last** moment at which the speed was outside the tolerance band is sought; the spin-up
time is the next sample after it.

Why "the last exit" and not "the first entry": the platter can fly into the band and back out
again (belt overshoot is common). The real time to reach nominal is the moment after which the
speed never left the band again.

Three cases, distinguished honestly:

* `trace[0] > 0.5·plateau` → the recording began with the platter already up to speed → `null`,
  there is nothing to report;
* the last exit is the last sample → the platter is **still spinning up** → `null`;
* otherwise → the time from the first sample to entry into the band.

---

## 3.7. Wow and flutter

See [07-Wow-and-flutter.md](07-Wow-and-flutter.md) for the detail. Briefly, what the gyroscope
estimator does:

1. The series `ω_projected(t)` is resampled onto a uniform 100 Hz grid by linear interpolation
   (`UniformResampler`) — the FFT requires uniformity, and sensor callbacks do not provide it.
2. `WowFlutter.Analyze` converts the series into percent of the mean, detrends it, applies a
   Hann window and takes the FFT.
3. From the spectrum it takes the RMS in the 0.2–20 Hz band and the strongest component.

The expected peaks and their physical meaning:

| Frequency | Source |
|---|---|
| `f_rot` = 0.556 / 0.75 / 1.3 Hz | platter eccentricity, bearing runout, an off-centre spindle hole |
| `f_pulley` | motor shaft revolutions (usually several times higher) |
| `1/T_belt` | uneven belt thickness |

The gyroscope is ideal for this: wow and flutter is a **relative** change of speed, and relative
changes are unaffected by the scale-factor error (it cancels when dividing by the mean).

---

## 3.8. The confidence estimate

```
precision  = 1 − (SE/rpm)/0.01       // SE already includes the scale systematic
steadiness = 1 − (sd/ω̄)/0.10         // how steadily the platter runs
coverage   = n_eff / 500             // whether the statistics have accumulated

confidence = (precision · steadiness · coverage)^(1/3) · ceiling
```

where `ceiling = 0.45` until the scale has been calibrated.

---

## 3.9. Summary

```
(t, ωₓ, ω_y, ω_z) @ 200–500 Hz
        ↓
   subtract bias (GyroZeroCalibrator, 2.5 s on a stationary platter)
        ↓
   TimeWindow 30 s
        ↓
   MeanDirection → rotation axis ê (signed)
        ↓
   projection ω·ê → the speed series
        ↓                            ↘
   mean ω̄                             resample to 100 Hz → FFT → WowFlutter
        ↓                            ↘
   × ScaleFactor (from the magnetometer)  EMA trace → spin-up time
        ↓
   SE = √(SE_stat² + SE_scale²)      ← the systematic dominates until calibration
        ↓
   rpm ± SE, confidence ceiling 0.45 without calibration
```
