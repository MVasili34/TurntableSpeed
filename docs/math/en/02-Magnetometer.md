# 2. The magnetometer estimator — the primary instrument

Code: `Estimators/MagnetometerSpeedEstimator.cs`, `Dsp/PrincipalAxes.cs`,
`Dsp/EllipseFit.cs`, `Dsp/PhaseMath.cs`, `Dsp/LineFit.cs`

---

## 2.1. The physics: why the magnetometer and not the gyroscope

The Earth's magnetic field **B** is constant in the laboratory frame: roughly 25–65 µT, with a
fixed direction (at mid latitudes, inclined 60–70° to the horizontal).

The phone lies on the platter and rotates with it. In **the phone's own axes** that same
stationary field vector appears to rotate at the same angular velocity, in the opposite
direction:

```
B_phone(t) = R(−ωt) · B_Earth
```

where `R` is the rotation matrix about the platter's axis of rotation (the vertical).

Decompose `B_Earth` into a component along the rotation axis and a component perpendicular to
it:

```
B = B_∥ · ê + B_⊥
```

* `B_∥` **does not change** under rotation — it is the projection onto the axis.
* `B_⊥` **traces a circle** of radius `|B_⊥|` in the plane perpendicular to the axis.

So the tip of the vector traces a circle in some plane in the phone's axes, and the phone goes
exactly once around it per platter revolution.

**The key point.** The measured quantity is not the amplitude `|B_⊥|` (we are entirely
indifferent to it, and its scale error does not worry us) but the **rate at which phase
accumulates** around that circle. Phase accumulates exactly 2π per revolution — a geometric
fact independent of the sensor's sensitivity, of the field strength and of the latitude. The
only thing the result depends on is the **time scale**, and that comes from the phone's crystal
oscillator with an accuracy of a few ppm (0.0001 %).

Hence the main architectural conclusion: **the magnetometer method has no scale-factor error by
construction.** The gyroscope method has one, and it is 1–3 %.

---

## 2.2. What interferes, and how it is cured

| Interference | Physics | Cure |
|---|---|---|
| **Hard iron** | Permanently magnetised material near the sensor (the phone's speaker, the turntable motor's magnet, a magnetised platter) adds a constant vector. The circle is displaced from the origin. | Offset = centre of the fitted ellipse |
| **Soft iron** | Nearby ferromagnetic material distorts the field anisotropically. The circle becomes an ellipse. | Ellipticity = axes of the fitted ellipse |
| **Tilted axis** | The phone does not lie perfectly flat; the rotation axis does not coincide with the sensor's Z axis. | PCA: the plane is found from the data |
| **Transient interference** | A magnet is carried past, a motor switches on. | Radius-based rejection + robust regression |
| **Slow drift** | Sensor zero drift, iron moved around in the room. | Residual autocorrelation accounted for in σ |

The first two are cured by one and the same trick, and it is elegant: **hard iron is the centre
of the ellipse, soft iron is its shape.** A single fit yields both calibrations.

---

## 2.3. Step 1 — PCA: find the plane of rotation

Code: `Dsp/PrincipalAxes.cs`

We do not know how the phone is oriented relative to the axis of rotation. But we do know that
the trajectory `(mx, my, mz)` lies in a **plane**. So the point cloud is flat, and the plane can
be found by principal component analysis.

The sample covariance matrix of the three-dimensional cloud is computed:

```
μ = (1/n) Σ mᵢ

C = 1/(n−1) · Σ (mᵢ − μ)(mᵢ − μ)ᵀ      (symmetric 3×3)
```

Its eigenvalues `λ₀ ≥ λ₁ ≥ λ₂` and eigenvectors `v₀, v₁, v₂` give:

* `v₀, v₁` — a basis for the plane of rotation (`PlaneU`, `PlaneV`);
* `v₂` — **the normal to the plane, i.e. the axis of rotation** (`Normal`);
* `λ₀, λ₁ ≈ |B_⊥|²/2` — the spread within the plane;
* `λ₂ ≈ σ_noise²` — the spread across the plane, ideally noise only.

The planarity diagnostic:

```
Flatness = λ₂ / λ₀
```

If it is close to zero, the points really do lie in a plane and the method's premise holds. If
it is appreciable (the confidence threshold in the code is 0.05), the phone is not following a
single rotation axis: it is sliding, the platter is wobbling, or this is not rotation at all.

`μ` is a first approximation to the hard-iron offset (the cloud's centroid).

### Solving the eigenvalue problem

The matrix is symmetric 3×3, so the **cyclic Jacobi method** is used
(`SymmetricEigen3.Decompose`). The idea: successive Givens rotations

```
J(p,q,θ):  zeroes the off-diagonal element a[p,q]
```

reduce the matrix to diagonal form. The angle is chosen from the condition `a'[p,q] = 0`:

```
θ = (a_qq − a_pp) / (2·a_pq)
t = sign(θ) / (|θ| + √(θ² + 1))        (the smaller root of the quadratic, in magnitude)
c = 1/√(t² + 1),   s = t·c
```

Choosing the smaller root is the standard stability trick: it produces a rotation of less than
45°, so error accumulation is minimal. The method converges quadratically; the 24 sweeps in the
code are ample. The accumulated rotations give the eigenvector matrix.

Why Jacobi rather than something from a library: it fits into 100 lines with no dependencies,
is completely deterministic, and for 3×3 is faster than any general algorithm.

### Projection onto the plane

```
u = (m − μ) · v₀
v = (m − μ) · v₁
```

From here on all the work happens in the two-dimensional coordinates `(u, v)`.

---

## 2.4. Step 2 — ellipse fitting: hard iron and soft iron

Code: `Dsp/EllipseFit.cs`

### Statement of the problem

In the plane the points should lie on a circle, but soft iron puts them on an ellipse and hard
iron displaces it. The general conic section:

```
A·x² + B·x·y + C·y² + D·x + E·y + F = 0
```

Six coefficients up to a common factor — five degrees of freedom, exactly as many as an ellipse
has (centre 2, semi-axes 2, angle 1).

### Why not "just least squares"

The naive approach — minimising `Σ (A xᵢ² + ... + F)²` under some normalisation such as
`|a| = 1` — has a fatal flaw: **the solution may turn out to be a hyperbola or a parabola**.
Especially if the arc is incomplete. And incomplete arcs happen to us regularly: at 78 rpm
fewer than three revolutions pass through a 2-second window, and at the very start of a
measurement, fewer than one.

### The solution: Fitzgibbon's method in the Halíř–Flusser formulation

An **ellipse-specific** constraint is imposed:

```
4AC − B² = 1
```

The discriminant of a conic, `B² − 4AC`, is negative for an ellipse and only an ellipse. Fixing
it at `−1` makes a hyperbola impossible: **the solution is an ellipse by construction.** It also
fixes the scale, ruling out the trivial solution `a = 0`.

The problem becomes:

```
minimise  aᵀ S a   subject to   aᵀ C₁ a = 1
```

where `S = DᵀD` is the scatter matrix, `D` is the design matrix whose rows are
`(x², xy, y², x, y, 1)`, and `C₁` is the constraint matrix.

Lagrange multipliers give the generalised eigenvalue problem `S a = λ C₁ a`. Solving it
directly is numerically unstable (`S` is nearly singular). Halíř and Flusser split `a` into its
quadratic and linear parts `a = (a₁, a₂)` and reduced the problem to an ordinary 3×3 one:

```
S₁ = D₁ᵀD₁,  S₂ = D₁ᵀD₂,  S₃ = D₂ᵀD₂

T = −S₃⁻¹ S₂ᵀ                  (the linear part expressed through the quadratic one)
M = S₁ + S₂ T                  (the reduced 3×3)
M' = C₁⁻¹ M                    (an explicit row permutation in the code)
```

`C₁⁻¹` for the constraint `4AC − B² = 1` is just a permutation with factors, so it is written
out by hand in the code:

```csharp
constrained[0, col] =  0.5 * m[2, col];
constrained[1, col] = -1.0 * m[1, col];
constrained[2, col] =  0.5 * m[0, col];
```

Then the eigenvector of `M'` satisfying `4a₀a₂ − a₁² > 0` is sought. There is exactly one. The
linear part is recovered as `a₂ = T·a₁`.

### Conditioning: normalisation is mandatory

Raw magnetometer values are tens of µT. Then `x²` is in the thousands while the constant term
is 1. The spread of the scatter matrix's elements is six orders of magnitude, and `S₃` becomes
numerically singular.

So before fitting, the data are centred and scaled:

```
x' = (x − x̄) / s,   s = √( (1/n) Σ [(xᵢ−x̄)² + (yᵢ−ȳ)²] )
```

that is, by the cloud's RMS radius. After the fit the normalisation is undone:

```
centre = centre' · s + mean
semiAxis = semiAxis' · s
```

This is not cosmetic — without it the method simply does not work on real data.

### From coefficients to geometry

`EllipseFit.FromConic`:

```
Δ  = B² − 4AC                        (< 0 for an ellipse)
x₀ = (2CD − BE) / Δ
y₀ = (2AE − BD) / Δ
F₀ = F + ½(D·x₀ + E·y₀)              (the constant term after shifting to the centre)
```

The quadratic form `[[A, B/2], [B/2, C]]` is diagonalised analytically:

```
tr = A + C,   det = AC − B²/4
λ₁,₂ = ½ (tr ± √(tr² − 4·det))
```

The semi-axes:

```
aᵢ = √(−F₀ / λᵢ)
```

The **larger** eigenvalue corresponds to the **smaller** semi-axis (a stiffer quadratic form →
a shorter axis). The angle comes from the eigenvector.

### What this gives for calibration

```
CenterX, CenterY  →  hard iron (in the plane's coordinates)
SemiMajor/SemiMinor = AxisRatio  →  soft iron (1.0 = a perfect circle)
ResidualRms  →  how well the model describes the data at all
```

Hard iron is lifted back into the phone's axes (`ComputeHardIron`):

```
offset = μ + x₀·v₀ + y₀·v₁
```

### Normalising a point: the central operation

`EllipseFitResult.Normalize` maps a measured point onto the **unit circle**:

1. subtract the centre (remove hard iron);
2. rotate into the ellipse's own axes by `−angle`;
3. divide the coordinates by the semi-axes (remove soft iron);
4. rotate back.

```
(u, v) = R(−θ)·(x − x₀, y − y₀)
(u', v') = (u / a, v / b)
(nx, ny) = R(+θ)·(u', v')
```

The point of it: **after this operation phase accumulates uniformly.** That is exactly what the
linear regression downstream requires. Without removing the ellipticity the phase would
"breathe" twice per revolution, and the regression would pick up a sinusoidal residual at
frequency `2ω` — which does not bias the slope on average, but inflates the variance estimate.

---

## 2.5. Step 3 — rejecting outliers by radius

After the fit, a normalised radius is computed for each point:

```
r = |Normalize(u, v)|
```

An ideal point gives exactly 1. Points with `|r − 1| > 0.35` are discarded.

**Why this is done BEFORE taking the phase, rather than left to the robust regression.** This is
a subtle spot, and it carries its own comment in the code. Phase unwrapping is **cumulative**:
if one sample is thrown half a revolution off by a magnet, the classic unwrap assigns it the
wrong 2π branch, and **every subsequent point shifts by 2π**. That is not an outlier, it is a
level step. Huber weights do not reject a step — they are designed for isolated excursions, not
for a shift affecting half the sample.

A magnetic disturbance changes the **magnitude** of the field, so it shows up in the radius
before it has a chance to corrupt the phase. This is cheap and reliable.

A safety catch: if more than 50 % is rejected, then it is not the signal that is suspect but
the ellipse itself. In that case the filter is **not applied at all**, and a bad fit travels
honestly into the diagnostics (`ellipseResidualRms`, `rejectedFraction`) instead of being
papered over.

---

## 2.6. Step 4 — phase and its unwrapping

Code: `Dsp/PhaseMath.cs`

The phase of a point:

```
φ = atan2(ny, nx)  ∈ (−π, π]
```

This is a sawtooth: once per revolution it jumps from `+π` to `−π`. It needs to be unwrapped
into a continuous, linearly growing quantity.

### The naive unwrap and its trouble

```
φ_unwrapped[i] = φ_unwrapped[i−1] + wrap(φ[i] − φ_unwrapped[i−1])
```

where `wrap` folds into `(−π, π]`. This works perfectly under noise and catastrophically under
outliers: the step is bounded by ±π **by construction**, so a sample that jumps more than half a
revolution does not produce a large step — it quietly eats 2π of accumulated phase and shifts
the entire remainder of the record.

### The robust alternative: `UnwrapSteady`

The key observation is that the platter turns **uniformly**. So instead of chaining from the
previous sample, each sample can be placed on the 2π branch closest to a **prediction**:

```
φ_unwrapped[i] = φ_predicted[i] + wrap(φ[i] − φ_predicted[i])
```

Then a corrupted sample costs only its own residual and drags nobody along with it.

The prediction comes from an **α–β filter** (a simplified Kalman filter for a "constant
velocity" model):

```
prediction:  φ̂ = level + rate·Δt
innovation:  ι = wrap(φ_measured − φ̂)

output:      φ_unwrapped[i] = φ̂ + ι          ← the full innovation, this is the residual
correction:  ι_clip = clamp(ι, ±0.6 rad)     ← clipped, so an outlier cannot shift the tracker
             level = φ̂ + α·ι_clip
             rate  = rate + β·ι_clip / Δt
```

with `α = 0.08`, `β = α²/2`. The relation `β ≈ α²/2` is the standard choice, giving roughly
critical damping: the tracker follows genuine speed changes but does not oscillate.

Two important decisions:

1. **The measurement is given the full innovation.** That is its residual; how much to believe
   it is decided by the regression downstream, not by the tracker. The tracker has no business
   "fixing" the data.
2. **The tracker is given the clipped innovation.** The clip is symmetric, so it introduces no
   bias — it merely prevents the tracker from believing an outlier.

The tracker's initial rate is not taken from nowhere but from the **median of the step-wise
rates**:

```
rate₀ = median{ wrap(φᵢ − φᵢ₋₁) / Δtᵢ }
```

The median is robust to any number of isolated outliers up to 50 %. The method's limitation is
`|ω·Δt| < π`, i.e. **more than two samples per revolution** — the Nyquist condition for phase.
At 50 Hz and 78 rpm that gives 38 samples per revolution, an enormous margin.

The trade-off between the two approaches is named explicitly in the code: chaining is ideal
under noise and hopeless under outliers; anchoring to a single global straight line is the
reverse, because the slope of that line is itself estimated from the same corrupted data. The
α–β tracker is the middle ground.

---

## 2.7. Step 5 — regressing the phase: the measurement proper

Code: `Dsp/LineFit.cs`

The unwrapped phase is linear in time:

```
φ(t) = ω·t + φ₀
```

The angular velocity is the **slope**. That is the measurement.

### Weighted least squares

```
ω̂ = (Σw Σwxy − Σwx Σwy) / (Σw Σwx² − (Σwx)²)
```

The residual variance and the standard error of the slope:

```
s² = Σ wᵢ rᵢ² / (n − 2)
Var(ω̂) = s² · Σw / (Σw Σwx² − (Σwx)²)
```

The weights are normalised to mean 1 — then `s²` remains interpretable as a residual variance
instead of depending on what units the weights happen to be in.

**This gives the confidence interval on the speed directly** — precisely what the specification
demands. No separate heuristic is needed: `SE(ω̂)` converts to rpm by the same factor `60/2π` as
`ω` itself.

### Robustness: IRLS with Huber weights

A magnet is carried past and the phase twitches. Ordinary least squares penalises residuals
quadratically, so one strong outlier tilts the whole line.

The Huber loss is quadratic near zero and **linear** beyond a threshold:

```
ρ(r) = ½r²           for |r| ≤ k
     = k|r| − ½k²    for |r| > k
```

In iteratively reweighted least squares (IRLS) form this is equivalent to the weights:

```
wᵢ = 1              for |rᵢ| ≤ k·s
   = k·s / |rᵢ|     for |rᵢ| > k·s
```

The scale `s` is taken robustly, via the **median absolute deviation**:

```
MAD = 1.4826 · median|rᵢ − median(r)|
```

The factor 1.4826 = `1/Φ⁻¹(0.75)` makes MAD a consistent estimator of σ for normal data (for
the standard normal the median of `|x|` is 0.6745, and `1/0.6745 = 1.4826`).

The constant `k = 1.345` is the classic choice: it gives **95 % efficiency** relative to least
squares on purely normal data, with substantial resistance to contamination. Convergence
usually takes 3–5 iterations.

### The M-estimator's error: a subtlety that is easy to miss

IRLS ends in a weighted fit, and it is tempting to take its standard error as it stands. That
is wrong.

The weighted formula treats the weights as **known observation precisions**. But they are not
known — they are a function of the residuals themselves. The residual variance `Σw r²/(n−2)` is
understated by exactly the amount by which we pressed the large residuals down. On clean normal
data the interval comes out **about 10 % narrower than the truth**. For the app's primary
instrument that is unacceptable: the estimate would be accurate while the stated uncertainty
was a lie.

The correct scale for an M-estimator (`WithRobustStandardErrors`):

```
τ² = E[ψ²] / (E[ψ'])²
```

where `ψ(r) = clamp(r, ±k·s)` is the derivative of the loss function, and `ψ'` equals 1 inside
the threshold and 0 outside. In the code:

```
E[ψ²]  = (1/n) Σ clamp(rᵢ, ±k·s)²
E[ψ']  = (the fraction of points inside the threshold)
```

Then the standard step:

```
Var(ω̂) = τ² / Σ(xᵢ − x̄)²
```

On clean normal data this comes out **2.6 % higher** than plain least squares — the honest
price of robustness, rather than 10 % lower, which would be self-deception.

### Residual autocorrelation

The phase residuals are not white: slow magnetic wandering, platter eccentricity, motor cogging
torque — all of these create deviations correlated in time. Treating such points as independent
means understating the interval, because `SE ~ 1/√n` requires exactly that independence.

The lag-1 autocorrelation of the residuals is computed:

```
ρ = Σ(rᵢ − r̄)(rᵢ₋₁ − r̄) / Σ(rᵢ − r̄)²
```

and an **effective sample size** is introduced (the standard result for AR(1)):

```
n_eff = n · (1 − ρ) / (1 + ρ)
```

The meaning: for an AR(1) process the variance of the mean is `(1+ρ)/(1−ρ)` times that of white
noise. At `ρ = 0.5` the effective sample is a third of the nominal one. The standard error is
inflated:

```
SE_corrected = SE · √(n / n_eff)
```

For `ρ ≤ 0` no correction is applied (negative correlation would narrow the interval — we
decline to exploit that, as it would be optimistic).

---

## 2.8. The confidence estimate

Five individual factors, each in [0, 1]:

| Factor | Formula | What it catches |
|---|---|---|
| `precision` | `1 − (SE/rpm)/0.0005 · 0.5` | whether the target relative accuracy has been reached |
| `coverage` | `revolutions / 6` | whether enough revolutions have accumulated |
| `geometry` | `(1 − res/0.15)·(1 − (ratio−1)/1.5)` | whether the ellipse fit is any good |
| `planarity` | `1 − flatness/0.05` | whether the points lie in a plane |
| `cleanliness` | `1 − outlierFraction/0.2` | how many outliers there are |

The result is their **geometric mean**:

```
confidence = (precision · coverage · geometry · planarity · cleanliness)^(1/5)
```

Why geometric rather than arithmetic: one bad component should drag the result down, not be
averaged away against four good ones. The geometric mean goes to zero if any single factor is
zero — exactly the behaviour required. (In the code each factor is floored at `1e-9` to avoid
NaN.)

`coverage = revolutions/6` reflects the physics: up to about 3 revolutions the phase line has
not yet averaged out platter eccentricity and motor cogging torque — both periodic with the
revolution period.

---

## 2.9. Window requirements and why they are what they are

| Parameter | Value | Rationale |
|---|---|---|
| `WindowSeconds` | 60 | ≈33 revolutions at 33⅓ — the "better than 0.05 % in a minute" target |
| `MinimumSpanSeconds` | 2 | shorter and the phase line is too short |
| `MinimumRevolutions` | 1 | **mandatory** (see below) |
| `MinimumSamples` | 64 | regression statistics |
| `RecomputeEverySamples` | 25 | determinism (see below) |

**Why a minimum of one revolution.** With an incomplete arc, the ellipse's centre offset and
the arc's curvature are **not separable**: a short arc of a large circle centred at the origin
is indistinguishable from an arc of a small circle with a displaced centre. The fit will
cheerfully invent some centre, and it will be wrong. Only a closed loop pins down both the
centre and the radius.

**Why recomputation is counted in samples rather than in time.** The determinism requirement:
a run over the same data must give a bit-identical result regardless of how fast the data are
fed in. Not a single reference to the system clock inside Core.

---

## 2.10. The complete pipeline

```
(t, mx, my, mz) @ 50–100 Hz
        ↓
    TimeWindow (60 s sliding window)
        ↓
    PCA → plane of rotation + axis + flatness
        ↓
    projection onto the plane → (u, v)
        ↓
    EllipseFit → centre (hard iron), semi-axes (soft iron), angle
        ↓
    rejection by radius |r − 1| > 0.35
        ↓
    repeated PCA + EllipseFit on the cleaned data
        ↓
    Normalize → phase φ = atan2(ny, nx)
        ↓
    UnwrapSteady (α–β tracker + branch anchoring)
        ↓
    RobustLineFit.Huber → ω = slope, SE(ω)
        ↓
    SE corrected for residual autocorrelation
        ↓
    rpm = ω · 60/(2π),  SE_rpm = SE(ω) · 60/(2π)
        ↓
    nominal classification + confidence estimate
```
