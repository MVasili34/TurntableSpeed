# The mathematics and physics of TurntableSpeed

> 🇷🇺 [Русская версия](../ru/README.md)

This section explains **why** the app measures speed the way it does and not some other way, and
derives every formula that actually appears in the `TurntableSpeed.Core` code.

The documents were written as an explanation, not as a reference: each method is first justified
physically, then derived mathematically, then tied to a specific file and class in the code.

The assumed starting point is school-level physics and mathematics. Musical acoustics (cents,
octaves, temperament) is **not assumed known**: it is built up from nothing in
[00-Sound-theory.md](00-Sound-theory.md).

## Reading order

| № | Document | About |
|---|---|---|
| 0 | [00-Sound-theory.md](00-Sound-theory.md) | **Primer:** frequency, octave, harmonics, temperament, cents, beats. Read first if you have not worked with sound before |
| 1 | [01-Fundamentals.md](01-Fundamentals.md) | The domain quantities, rpm ↔ rad/s ↔ cents, nominal classification |
| 2 | [02-Magnetometer.md](02-Magnetometer.md) | The primary instrument: PCA, ellipse fitting, phase, robust regression |
| 3 | [03-Gyroscope.md](03-Gyroscope.md) | Why it cannot be trusted in absolute terms; bias, scale factor |
| 4 | [04-Accelerometer.md](04-Accelerometer.md) | Tilt, centripetal acceleration, radius estimation |
| 5 | [05-Acoustics-clicks.md](05-Acoustics-clicks.md) | Envelope autocorrelation, the comb, choosing the period |
| 6 | [06-Acoustics-pitch.md](06-Acoustics-pitch.md) | Folding the spectrum onto 12 pitch classes, circular mean, cents |
| 7 | [07-Wow-and-flutter.md](07-Wow-and-flutter.md) | Wow & flutter, the modulation spectrum, the long mode |
| 8 | [08-Uncertainty-and-fusion.md](08-Uncertainty-and-fusion.md) | Where each method's σ comes from, and how the methods combine |
| 9 | [09-DSP-primitives.md](09-DSP-primitives.md) | FFT, Butterworth, parabolic interpolation, resampling, Hilbert |

## The whole thing on one page

If you need to grasp the structure quickly:

```
                     PHYSICAL QUANTITY              WHAT IS REALLY MEASURED
                     ─────────────────              ───────────────────────
Mode A   magnetometer   Earth's field vector   →   the FREQUENCY of phase   ★ reference
(sensors) gyroscope     angular velocity       →   the AMPLITUDE of a signal  secondary
          accelerometer g + ω²r                →   orientation, sanity check

Mode B   clicks         impulses from dust     →   the PERIOD of repetition ★ reference
(audio)  pitch          musical pitch          →   deviation in cents         secondary
         wow spectrum   pitch modulation       →   the modulation FREQUENCY   long run
```

The key idea, repeated in all four "reference" methods: **a frequency or a period is what gets
measured reliably, not an amplitude.** Frequency derives from the phone's crystal oscillator (a
few ppm), so it has no scale error. The amplitude of any MEMS sensor carries a factory
scale-factor error of 1–3 %, which is larger than the quantity being measured (speed deviations
of order 0.1–1 %). The whole architecture follows from that: the magnetometer matters more than
the gyroscope, and click autocorrelation matters more than pitch analysis.

The second idea running throughout: **every number comes with an uncertainty estimate**, and
disagreement between independent methods is shown rather than silently averaged away.

## How the code maps onto the documents

```
TurntableSpeed.Core/
├── Contracts/
│   ├── NominalSpeed.cs          → 01-Fundamentals
│   └── SpeedEstimate.cs         → 08-Uncertainty
├── Dsp/
│   ├── PrincipalAxes.cs         → 02-Magnetometer §3
│   ├── EllipseFit.cs            → 02-Magnetometer §4
│   ├── PhaseMath.cs             → 02-Magnetometer §5-6, 06-Pitch §4
│   ├── LineFit.cs               → 02-Magnetometer §7, 08-Uncertainty
│   ├── Autocorrelation.cs       → 05-Clicks §4
│   ├── PeakInterpolation.cs     → 09-DSP §4
│   ├── Biquad.cs                → 09-DSP §3
│   ├── Fft.cs / Spectrum.cs     → 09-DSP §1-2, 07-Wow-and-flutter
│   ├── Envelope.cs              → 05-Clicks §3, 09-DSP §5
│   ├── Resampling.cs            → 09-DSP §6
│   └── SeriesMath.cs            → everywhere
└── Estimators/
    ├── MagnetometerSpeedEstimator.cs → 02
    ├── GyroscopeSpeedEstimator.cs    → 03
    ├── GyroZeroCalibrator.cs         → 03 §2
    ├── OrientationMonitor.cs         → 04
    ├── ClickPeriodicityEstimator.cs  → 05
    ├── PitchGridEstimator.cs         → 06
    ├── WowSpectrumEstimator.cs       → 07
    ├── AudioProcessingDetector.cs    → 05 §2
    ├── SensorFusionEstimator.cs      → 08 §5
    └── AudioFusionEstimator.cs       → 08 §6
```
