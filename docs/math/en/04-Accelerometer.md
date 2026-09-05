# 4. The accelerometer — orientation and sanity

Code: `Estimators/OrientationMonitor.cs`

The accelerometer takes **no part** in the speed estimate. Its job is two checks: that the phone
really is lying flat, and a rough estimate of the placement radius.

---

## 4.1. What the accelerometer sees on a rotating platter

An accelerometer measures **specific force** — the sum of all non-inertial accelerations. For a
phone lying on a platter there are two of them.

### Gravity

A constant vector of magnitude `g ≈ 9.807 m/s²` pointing up in the phone's axes (the sensor is
at rest, so the support is pushing up). For a phone lying perfectly flat this is `(0, 0, +g)`.

### Centripetal acceleration

The phone travels on a circle of radius `r` about the platter's centre, so it experiences an
acceleration towards the centre:

```
a_c = ω² · r
```

It is directed **in the plane of the platter**, towards the axis of rotation. Its magnitude at
various radii:

| Nominal | ω, rad/s | r = 2 cm | r = 5 cm | r = 8 cm |
|---|---|---|---|---|
| 33⅓ | 3.491 | 0.24 m/s² | 0.61 m/s² | 0.97 m/s² |
| 45 | 4.712 | 0.44 m/s² | 1.11 m/s² | 1.78 m/s² |
| 78 | 8.168 | 1.33 m/s² | 3.34 m/s² | 5.34 m/s² |

An important detail: in **the phone's own axes** this vector is constant. The phone rotates with
the platter, so the direction "towards the centre" does not change in its axes. Which means a
low-pass filter **does not remove it** — it looks like yet another constant force.

---

## 4.2. Extracting the constant component

An exponential low-pass filter with `τ = 0.7 s`:

```
α = 1 − exp(−Δt/τ)
g⃗ ← g⃗ + α·(a⃗ − g⃗)
```

The same form of α as in the gyroscope, and for the same reason: **uneven sampling**. The
formula `1 − exp(−Δt/τ)` is the exact solution over a step `Δt`, so the time constant does not
depend on callback jitter.

`τ = 0.7 s` cuts off vibration and jolts, leaving gravity plus the centripetal term.

---

## 4.3. Tilt: why only the vertical component

This is the subtlest point in the whole file, and it carries its own comment in the code.

### The naive (and wrong) approach

Take the angle between the phone's Z axis and the low-passed vector:

```
θ_apparent = arccos(g_z / |g⃗|)          ← WRONG as a "is it flat" check
```

The problem: `|g⃗|` includes the centripetal term. A phone lying perfectly flat 8 cm from the
centre at 78 rpm produces a horizontal component of 5.34 m/s² against a vertical 9.81 — that is
an angle of

```
arctan(5.34 / 9.81) = 28.6°
```

So **a correctly placed phone would be warned that it "is not lying flat"** and thrown out of
the measurement.

### The correct approach

The centripetal vector lies **in the plane of the platter**. For a phone lying flat, the plane
of the platter is the phone's XY plane. So the centripetal term **does not enter the Z component
at all**.

Gravity, meanwhile, contributes exactly `g·cos θ` to Z, where `θ` is the tilt from horizontal.
Hence:

```
θ = arccos( g_z / g_reference )
```

This quantity honestly measures tilt and stays correct on a rotating platter.

The threshold: `θ ≤ 5°` counts as "flat" (`IsFlat`).

### Both quantities are useful

The code computes both:

* `TiltDegrees` — from `g_z`, the true tilt;
* `ApparentTiltDegrees` — from the full vector, including the centripetal term.

**The difference between them is itself a diagnostic:** it grows with distance from the centre.
Zero means the phone is sitting on the axis.

---

## 4.4. The gravity reference: removing the accelerometer's factory error

The accelerometer has a scale error too. By default `GravityReference = 9.80665` (standard
gravity), but that is doubly approximate: real `g` depends on latitude and altitude (from 9.78
at the equator to 9.83 at the pole), and the sensor may have its own percentage error.

`CaptureGravityReference()` adopts the current magnitude as the reference:

```
g_reference = |g⃗|
```

**It may only be called with the platter stopped** — then there is no centripetal term and the
magnitude equals pure gravity. Conveniently, that is exactly the moment when the gyroscope zero
is taken: the user has already been asked to stop the platter and not touch the phone.

A sanity check: it is accepted only if `|g⃗|` differs from 9.80665 by less than 1.5 m/s².
Otherwise the phone is clearly not at rest.

After that, `θ = arccos(g_z / g_reference)` no longer contains the sensor's scale error — it has
cancelled.

---

## 4.5. Estimating the placement radius

```
r = a_horizontal / ω²,     a_horizontal = √(g_x² + g_y²)
```

Three restrictions, each of them essential:

1. **Only when `IsFlat`.** On a tilted phone gravity contributes to the horizontal components,
   and `a_horizontal` stops being the centripetal acceleration. Hence the explicit check before
   the computation.

2. **`ω` is taken from the magnetometer**, not from the accelerometer. From the single equation
   `a = ω²r` with two unknowns, `r` can only be extracted by knowing `ω` from an independent
   source.

3. **The result is discarded if `r ≥ 0.5 m`.** An LP has a radius of 15 cm; half a metre is
   plainly nonsense, so something is wrong with the data.

### Why this is NOT part of the speed estimate

The temptation is `ω = √(a/r)`. But `r` is unknown, and there is no way to measure it other than
through the very same formula. Besides, the error would be catastrophic — the relative
uncertainty is

```
δω/ω = ½·(δa/a + δr/r)
```

With `r` known to within a centimetre at a radius of 5 cm, that is already 10 % — two orders of
magnitude worse than the target. Plus the accelerometer's own scale error.

So `EstimatedRadiusMeters` is purely a sanity indicator: "the phone appears to be lying about
5 cm from the centre". Useful for explaining to the user where vibration or imbalance is coming
from, but not a measurement.

---

## 4.6. The plausibility check

```
IsGravityPlausible = |g_z − g_reference| < 1.5 m/s²
```

Below the band — the sensor is faulty or the phone is falling. Above it — the phone is being
picked up. The centripetal term does not enter here (it is in the XY plane), so the check stays
valid on a rotating platter — unlike a check on the vector's magnitude.

---

## 4.7. Separately: the mass of the phone

This is not mathematics, but a fundamental physical fact the app is obliged to report.

A phone weighs 150–250 g. A vinyl record weighs 120–180 g. In other words, **the phone doubles
the mass sitting on the platter**.

The consequences on a budget belt drive:

* the moment of inertia rises → longer spin-up;
* the bearing load rises → more friction → the motor may fail to reach nominal;
* being off-centre creates imbalance → bearing runout → wow and flutter.

So the sensor mode measures the speed **with the phone on the platter**, and that is not the
same as the speed during playback. In `SensorFusionEstimator` the flag
`SensorWarning.LoadAffectsMeasurement` is set **always** — not as a warning about an error, but
as a statement of what exactly was measured.

This is precisely why the second, acoustic mode is needed, and why **the disagreement between
the two results is itself informative**: it shows numerically how far the turntable sagged under
the load.
