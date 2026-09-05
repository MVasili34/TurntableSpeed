# 9. DSP primitives: the mathematics of the building blocks

Code: `TurntableSpeed.Core/Dsp/`

This chapter gathers the derivations for all the supporting numerical methods. Each of them is
covered by its own tests (specification §7.3), because an error in a primitive shows up as an
unexplainable systematic at the top level.

---

## 9.1. The FFT and its normalisation

Code: `Dsp/Fft.cs` (a wrapper over `MathNet.Numerics`)

### The normalisation convention

The forward transform is unscaled; the inverse is divided by N, so that `IFFT(FFT(x)) = x`.

The one-sided amplitude spectrum is built so that **a sine of amplitude A falling exactly into
bin k reads as A**:

```
X_k = Σ_{n=0}^{N−1} x_n · e^{−2πikn/N}

|A_k| = |X_k| · 2/N        for 0 < k < N/2
|A_k| = |X_k| · 1/N        for k = 0 (DC) and k = N/2 (Nyquist)
```

The factor of 2 for the middle bins — because the energy of a real sine is split equally between
`+f` and `−f`. DC and Nyquist have no partner, so they are not doubled.

### The frequency grid

```
f_k = k · f_s / N
Δf  = f_s / N        the resolution
```

The resolution is inversely proportional to the **duration** of the record `T = N/f_s`:

```
Δf = 1/T
```

Hence the figure from [07](07-Wow-and-flutter.md): 3 minutes → 0.0056 Hz.

### Window functions

```
Rectangular:  w = 1
Hann:         w = 0.5 − 0.5·cos(2πi/(N−1))
Hamming:      w = 0.54 − 0.46·cos(2πi/(N−1))
Blackman:     w = 0.42 − 0.5·cos(2πi/(N−1)) + 0.08·cos(4πi/(N−1))
```

**Why a window.** The FFT implicitly treats the signal as periodic over the block length. If the
beginning and the end do not join up, a discontinuity arises whose spectrum is `1/f` smeared
across every bin ("spectral leakage"). A window brings the edges smoothly to zero and removes
the discontinuity.

The price is a wider main lobe. Hann: the main lobe is twice as wide as the rectangular one, but
the sidelobes are down 31 dB instead of 13 and fall off as `1/f³` instead of `1/f`. For our
problems (finding a weak line next to a strong one) that is the right trade.

**Coherent gain** is the window's mean value:

```
CG = (1/N) Σ wᵢ        Hann: 0.5
```

The window attenuates the signal by CG, so amplitudes are divided by CG. Without this every
amplitude would be understated by a factor of two.

**Zero padding** adds no information (the resolution is still `1/T_data`) but interpolates the
spectrum, which helps parabolic peak interpolation. It dilutes the amplitude by
`N_padded/N_data`, which is compensated explicitly:

```
correction = (N_padded / N_data) / CG
```

---

## 9.2. Autocorrelation via the FFT

Code: `Dsp/Autocorrelation.cs`

The Wiener–Khinchin theorem: the autocorrelation function and the power spectral density are a
Fourier pair.

```
r(τ) = IFFT( |FFT(x)|² )
```

Complexity `O(N log N)` instead of `O(N·maxLag)`.

**Zero padding is mandatory**, up to `N + maxLag + 1`. Without it the result is the circular
autocorrelation: the end of the record wraps onto the beginning and creates spurious peaks. What
is needed is the linear one.

For the normalisations see [05](05-Acoustics-clicks.md) §5. Briefly: the **biased** one is used
(dividing by `n`, not by `n−τ`), because its natural `(n−τ)/n` decay stops multiples of the
period from overtaking the true one.

---

## 9.3. Biquads and Butterworth filters

Code: `Dsp/Biquad.cs`

### Transposed direct form II

```
y[n] = b₀·x[n] + z₁
z₁   = b₁·x[n] − a₁·y[n] + z₂
z₂   = b₂·x[n] − a₂·y[n]
```

Chosen for its better numerical stability in double precision and its minimal state (two
registers). The state is preserved across blocks — **mandatory** for streaming audio, since
otherwise every block boundary would produce a transient, that is an artificial click with a
period equal to the block size.

### Design by the RBJ cookbook

```
ω₀ = 2π·f_cutoff/f_s
α  = sin(ω₀)/(2Q)

LPF:  b = [(1−cos ω₀)/2,  1−cos ω₀,  (1−cos ω₀)/2]
HPF:  b = [(1+cos ω₀)/2, −(1+cos ω₀), (1+cos ω₀)/2]
both: a = [1+α, −2cos ω₀, 1−α]
```

The bilinear transform with frequency pre-warping, so that the −3 dB point lands exactly on the
requested digital frequency.

### Section quality factors for Butterworth

A Butterworth filter of order N has poles spaced evenly around the left half of the unit circle
in the s-plane. Splitting it into second-order sections, for the i-th section:

```
Qᵢ = 1 / (2·cos(π(2i+1)/(2N)))
```

For N = 4:

```
Q₀ = 1/(2·cos(π/8))  = 0.5412
Q₁ = 1/(2·cos(3π/8)) = 1.3066
```

A cascade of two such sections gives a maximally flat passband and a 24 dB/octave rolloff.

The frequency response check (used in the tests) goes through the complex response:

```
H(f) = (b₀ + b₁z⁻¹ + b₂z⁻²) / (1 + a₁z⁻¹ + a₂z⁻²),   z⁻¹ = e^{−2πif/f_s}
```

The section magnitudes multiply.

---

## 9.4. Parabolic peak interpolation

Code: `Dsp/PeakInterpolation.cs`

### Derivation

Through the three equally spaced points `(−1, y₋₁)`, `(0, y₀)`, `(+1, y₊₁)` there passes a
unique parabola `y = ax² + bx + c`:

```
c = y₀
a + b + c = y₊₁        →   a = ½(y₋₁ + y₊₁) − y₀
a − b + c = y₋₁            b = ½(y₊₁ − y₋₁)
```

The vertex is at `x* = −b/(2a)`, and the value there is:

```
y* = a·x*² + b·x* + y₀ = −b²/(4a) + y₀ = y₀ + ½·b·x*
```

The curvature is `c_curvature = −a` (positive for a maximum, since `a < 0`).

The offset is clamped to [−1, 1]: anything larger means the maximum is not at the central point,
i.e. the points were chosen wrongly.

### Sensitivity to noise

Perturbing the values by `ε` shifts the vertex. From `x* = −b/(2a)`, perturbing `b` by `Δb`
gives `Δx* = −Δb/(2a) = Δb/(2c_curvature)`. Hence the general order-of-magnitude estimate:

```
Δx* ~ ε / (2·curvature)
```

This is precisely the formula that converts autocorrelation noise and spectrum noise into the
uncertainty of a period ([05](05-Acoustics-clicks.md) §8) and of a frequency
([07](07-Wow-and-flutter.md)). A sharp peak (large curvature) is localised precisely; a flat one
is not.

### The half-width from the height and curvature

For `y = A − c·x²` the half-width `w` (where the value halves) satisfies `c·w² = A/2`:

```
w = √(A / (2c))
```

Used to estimate click jitter.

### Bias with a non-Gaussian peak shape

The autocorrelation peak from clicks, one or two frames wide, **is not a parabola**. Fitting a
parabola to a non-parabola produces a systematic attraction towards the sample grid — measured
as 0.15 of a frame. It is a systematic error, does not shrink as data accumulate, and forms a
floor under the uncertainty.

For spectral peaks the situation is better: the Hann window's main lobe is close to a parabola
on a **logarithmic** scale. That is why in `PitchGridEstimator` the interpolation is done on
`log(amplitude)` rather than on the amplitude.

---

## 9.5. The Hilbert transform and the analytic signal

Code: `Dsp/Envelope.cs`

The analytic signal of a real `x(t)`:

```
z(t) = x(t) + i·H{x}(t)
```

where `H` is the Hilbert transform (a −90° phase shift of every frequency). In the frequency
domain this is trivial:

```
Z(f) = 2·X(f)   for f > 0
Z(f) = X(f)     for f = 0 and f = Nyquist
Z(f) = 0        for f < 0
```

Hence the implementation: FFT → zero the negative frequencies, double the positive ones → IFFT.

From the analytic signal:

```
envelope             = |z(t)|
instantaneous phase  = arg z(t), unwrapped
instantaneous freq.  = d(phase)/dt / 2π
```

In this project it is used as the exact (non-block) envelope and as the reference for testing
the RMS envelope. The production click path uses RMS: it is cheaper, streams, and gives the
decimation for free.

---

## 9.6. Resampling

Code: `Dsp/Resampling.cs`

### Linear — for sensor data

`UniformResampler` places unevenly timestamped samples onto a uniform grid by linear
interpolation. It is needed because Android callbacks jitter, while any FFT downstream assumes
uniformity.

The default grid step is the **median** input interval. The median, not the mean: one dropped
sample doubles an interval and corrupts the mean, but not the median.

Linear interpolation introduces a slight high-frequency attenuation (its response is `sinc²`),
but when downsampling by a large factor (a sensor at 200–400 Hz → a 100 Hz grid, with a band of
interest up to 20 Hz) this is negligible.

### Lanczos — for simulating a speed error

`Resampler.ChangeSpeed` changes the playback speed. It is used in the tests: generate a signal,
"play" it 1.01 times faster and check that the estimator sees exactly +1 %.

The Lanczos kernel is a windowed sinc:

```
L(x) = sinc(x)·sinc(x/a)   for |x| < a
     = 0                    otherwise

where sinc(x) = sin(πx)/(πx),  a = the number of lobes (8 by default)
```

sinc is the ideal interpolator for band-limited signals (the sampling theorem); multiplying by a
second sinc confines the kernel to a finite length with acceptable artefacts.

**Anti-aliasing protection.** When speeding up, the spectrum is compressed towards Nyquist and
part of it runs past the boundary. So the kernel is stretched:

```
kernelScale = max(1, speedFactor)
w = L((position − k)/kernelScale, a)
```

A kernel stretched in time = narrowed in frequency = a built-in anti-aliasing filter.

The weights are normalised by their sum (`sum/weightSum`), which removes the small gain ripple
caused by truncating the kernel at the edges.

The physics of the correspondence:

```
playback s times faster  →  length × 1/s  →  pitch +1200·log₂(s) cents
```

---

## 9.7. Robust statistics

Code: `Dsp/SeriesMath.cs`

### Median and percentiles

A linearly interpolated percentile: position `p·(n−1)`, interpolating between neighbours.

### The median absolute deviation

```
MAD = 1.4826 · median|xᵢ − median(x)|
```

The factor `1.4826 = 1/Φ⁻¹(0.75)` makes MAD a **consistent estimator of σ** for normal data: for
the standard normal the median of `|x|` is 0.6745, and `1/0.6745 = 1.4826`.

MAD's breakdown point is 50 %: it stays meaningful as long as less than half the data are
corrupted. The ordinary σ has a breakdown point of 0 % — a single large enough outlier can
corrupt it arbitrarily.

### The running median

`O(n·log w)` via an ordered list with binary search: as the window slides, one element is
removed and one inserted. The edges are clamped (the window is shortened).

Its application is separating the impulsive from the stationary — see
[05](05-Acoustics-clicks.md) §4.

### Lag-1 autocorrelation

```
ρ = Σ_{i≥1}(xᵢ − x̄)(xᵢ₋₁ − x̄) / Σ(xᵢ − x̄)²
```

The result is clamped to [−0.999, 0.999] so that the formula `n(1−ρ)/(1+ρ)` cannot blow up.

---

## 9.8. Linear detrending

```
Σx = n(n−1)/2
Σx² = (n−1)n(2n−1)/6
slope = (n·Σxy − Σx·Σy) / (n·Σx² − (Σx)²)
```

Closed-form expressions for `x = 0..n−1` — no allocation for the axis.

Mandatory before any FFT of a drifting series: a trend creates a discontinuity between the
beginning and the end of the periodic continuation, whose `1/f` spectrum is smeared across the
low bins and buries the weak rotation peak there.

---

## 9.9. The sliding window

Code: `Dsp/TimeWindow.cs`

A ring buffer with two limits: by time (`WindowSeconds`) and by element count (`MaxCount`). The
second is insurance against a device delivering samples ten times more often than expected.

Eviction of old elements depends **only on the samples' timestamps**:

```
while  t_newest − t_oldest > WindowSeconds:  drop the oldest
```

No reference to the system clock — otherwise replaying a recording "in milliseconds" (the tests
of §7.5) would give quite different window contents than a real capture.

Every estimator recomputes **on a sample counter**, not on a timer. This is the determinism
requirement: a run over the same data must give a bit-identical result regardless of the feed
rate. Not a single reference to the system clock inside Core — time comes only from the samples'
timestamps.

---

## 9.10. What the primitive tests check

Specification §7.3 requires numerical tests for every block. The properties checked:

| Primitive | Check |
|---|---|
| FFT | against analytically known spectra; a sine's amplitude on a bin reads exactly |
| Phase unwrapping | jumps across ±π are recovered; an outlier does not shift the tail (`UnwrapSteady`) |
| Parabolic interpolation | a parabola with a known vertex is recovered exactly |
| Ellipse fitting | points on a known ellipse → parameters recovered; an arc does not yield a hyperbola |
| Filters | frequency response from the response to a set of sines; −3 dB exactly at the cutoff |
| Circular mean | correctness on data crossing the fold boundary |
| Robust regression | the slope is not shifted by outliers; σ is not understated on clean data |
| Resampling | speeding up by a known factor gives a known shift in cents |
