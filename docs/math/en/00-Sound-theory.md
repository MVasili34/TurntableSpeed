# 0. Sound theory from scratch: frequency, octaves, cents

This document is **not about the code**. It explains the musical-acoustic concepts every other
chapter relies on: frequency, octave, semitone, cent, harmonics, temperament, beats. If the
words "cent" and "equal temperament" mean nothing to you, start here — after it,
[01-Fundamentals](01-Fundamentals.md), [05](05-Acoustics-clicks.md),
[06](06-Acoustics-pitch.md) and [07](07-Wow-and-flutter.md) will read without friction.

Nothing here is specific to turntables until §0.10 — general theory first, then the tie-in to
the problem at hand.

---

## 0.1. Sound is a vibration. Frequency and period

Sound is a vibration of air pressure. The simplest (sinusoidal) vibration has two independent
parameters:

| Parameter | Physically | How it is perceived | Unit |
|---|---|---|---|
| **Frequency** `f` | how many complete cycles per second | **pitch** (low/high) | hertz, Hz = 1/s |
| **Amplitude** `A` | how strongly the pressure swings | **loudness** | Pa, or arbitrary units |

Frequency and period are the same number written two ways:

```
T = 1 / f          period — the duration of one cycle
f = 1 / T
```

| Frequency | Period | What it is |
|---|---|---|
| 20 Hz | 50 ms | lower limit of hearing, a "rumble" |
| 100 Hz | 10 ms | a low male voice |
| **440 Hz** | 2.27 ms | the note **A above middle C (A4)**, the tuning reference |
| 1000 Hz | 1 ms | the region of maximum aural sensitivity |
| 20 000 Hz | 50 µs | upper limit of hearing (usually 15–16 kHz in adults) |

The key thing to carry through the rest of the text: **pitch is frequency, and nothing but
frequency.** Loudness does not affect pitch.

---

## 0.2. Two worlds of frequency in this project

The app has frequencies differing by a factor of thousands sitting side by side, and they are
easy to confuse.

```
AUDIO FREQUENCIES           20 … 20 000 Hz      what is heard as a tone
                                                  ↑ measured by the FFT in 06-Pitch

PLATTER ROTATION FREQUENCY  0.556 / 0.75 / 1.3 Hz   NOT audible in itself
                                                  ↑ measured by 02, 03, 05

WOW & FLUTTER FREQUENCIES   0.2 … 20 Hz         not audible in themselves,
                                                  but heard as pitch "swimming"
                                                  ↑ measured by 07-Wow-and-flutter
```

A platter at 33⅓ rpm rotates at `f = 33.333/60 = 0.556 Hz` — that too is a "frequency" and
also in hertz, but it cannot be heard as a tone: it is 40 times below the threshold of
hearing. It shows up differently — as **modulation**, that is a slow wobble in the pitch of
whatever sound is already playing. That is what §0.9 and all of
[07](07-Wow-and-flutter.md) are about.

A useful metaphor: the audio frequency is the **carrier**, and the rotation frequency is what
**jiggles** the carrier.

---

## 0.3. Why hearing is logarithmic

The central fact of psychoacoustics, from which everything that follows grows:

> The ear perceives **ratios** of frequencies, not their differences.

You can verify it in your head. Compare two pairs:

```
pair A:  100 Hz → 200 Hz        difference 100 Hz,  ratio 2
pair B:  400 Hz → 800 Hz        difference 400 Hz,  ratio 2
```

The differences differ by a factor of four, but to the ear this is **the same musical
interval** — both pairs sound like "the same note, higher up". Whereas the pair `400 → 500`
(the same 100 Hz difference as in A) is perceived as a much smaller step than `100 → 200`.

Two consequences follow, and they determine all the mathematics from here on:

1. **The unit of pitch must be a ratio, not a difference.** "100 Hz higher" is a meaningless
   description of an interval; "twice as high" is meaningful.
2. **To make intervals add, you need a logarithm.** Ratios multiply: going up by a factor of 2
   and then by a further factor of 3 is a factor of 6. The logarithm turns multiplication into
   addition, and intervals start adding like ordinary numbers.

The second point is why `log₂` appears everywhere in the code. It is not decoration — it is the
only way to obtain a quantity that can be added, subtracted and averaged.

---

## 0.4. The octave

An **octave** is the interval with a frequency ratio of exactly **2:1**.

```
440 Hz  →  880 Hz       up one octave
440 Hz  →  220 Hz       down one octave
```

The octave is the "strongest" interval in music: two tones an octave apart are perceived not
merely as consonant but literally as **the same note**, only higher or lower. That is why they
share a name (every "A" is 55, 110, 220, 440, 880, 1760 Hz).

Why that should be so is explained in the next section.

---

## 0.5. Harmonics: a note is not one frequency

A real musical sound (a string, a voice, a wind instrument) is not a sine. A string vibrates as
a whole, in halves, in thirds and so on all at once, so it radiates a **set** of frequencies:

```
f, 2f, 3f, 4f, 5f, 6f, …
```

`f` is the **fundamental**; it determines the perceived pitch. The rest are **harmonics**
(overtones, partials); they determine timbre — why a violin and an oboe playing the same note
sound different.

For the note A at 220 Hz:

```
220, 440, 660, 880, 1100, 1320, …
```

### Why an octave sounds like "the same note"

Compare the harmonic sets of the note `f` and the note `2f`:

```
f  :  f, 2f, 3f, 4f, 5f, 6f, …
2f :      2f,     4f,     6f, …      ← all already present in f's set
```

The harmonics of the upper note are a **subset** of the harmonics of the lower one. The
auditory system receives not a single new frequency, only a shift in weight towards the high
end. Hence the sense of identity.

### Simple ratios = consonance

The same mechanism explains why intervals with small whole-number ratios sound pleasant: they
have many **coinciding** harmonics.

| Ratio | Interval | Example from 440 Hz | Coinciding harmonics |
|---|---|---|---|
| 2:1 | octave | 880.0 | very many |
| 3:2 | fifth | 660.0 | many |
| 4:3 | fourth | 586.7 | noticeably |
| 5:4 | major third | 550.0 | fewer |
| 6:5 | minor third | 528.0 | fewer still |
| 16:15 | minor second (semitone) | 469.3 | almost none → harsh |

This is the physical basis of harmony. And here the trouble starts.

---

## 0.6. Where the 12 semitones came from, and what temperament is

### The problem

We would like to build music out of pure ratios (3:2, 5:4). But pure intervals **do not add up
to an octave**. The classic example is the circle of fifths: go up by pure 3:2 fifths twelve
times and hope to land exactly on seven octaves.

```
(3/2)¹²  = 129.746…
2⁷       = 128
```

The miss is `129.746/128 = 1.0136`, that is **23.5 cents** (the unit is explained below) —
clearly audible. The quantity is called the **Pythagorean comma**. Similar mismatches arise for
any other combination of pure intervals.

Conclusion: an instrument with fixed tuning (piano, guitar, organ) physically cannot be pure in
all keys at once. A compromise is needed — a **temperament**.

### The solution: equal temperament (ET)

The compromise that won out by the 19th century and utterly dominates today: divide the octave
into **12 equal** (in the logarithmic sense) steps. The step is called a **semitone**:

```
semitone = 2^(1/12) = 1.0594631…     that is +5.946 %
```

Twelve such steps give exactly `(2^(1/12))¹² = 2` — an octave, precisely, by construction. Pure
intervals drift slightly as a result, but equally in every key:

| Semitones | Interval | ET, cents | Pure ratio | Pure, cents | ET error |
|---|---|---|---|---|---|
| 0 | unison | 0 | 1:1 | 0.00 | 0 |
| 1 | minor 2nd | 100 | 16:15 | 111.73 | −11.7 |
| 2 | major 2nd | 200 | 9:8 | 203.91 | −3.9 |
| 3 | minor 3rd | 300 | 6:5 | 315.64 | **−15.6** |
| 4 | major 3rd | 400 | 5:4 | 386.31 | **+13.7** |
| 5 | fourth | 500 | 4:3 | 498.04 | +2.0 |
| 6 | tritone | 600 | 45:32 | 590.22 | +9.8 |
| 7 | fifth | 700 | 3:2 | 701.96 | −2.0 |
| 8 | minor 6th | 800 | 8:5 | 813.69 | −13.7 |
| 9 | major 6th | 900 | 5:3 | 884.36 | +15.6 |
| 10 | minor 7th | 1000 | 16:9 | 996.09 | +3.9 |
| 11 | major 7th | 1100 | 15:8 | 1088.27 | +11.7 |
| 12 | octave | 1200 | 2:1 | 1200.00 | 0 |

Note that the ET fifth is off by only 2 cents (inaudible), while the thirds are off by 14–16
cents (audible if you listen for it). This is the well-known "price" of equal temperament.

**Why 12 in particular.** Because `log₂(3/2) = 0.58496`, which is very close to `7/12 =
0.58333`. Twelve equal steps happen to form a grid the fifth fits into almost without error. No
smaller number of divisions gives a coincidence like that.

### What matters here for the app

Every note of equal temperament sits on a **regular logarithmic grid** with a step of exactly
100 cents. That lets [06-Pitch](06-Acoustics-pitch.md) measure a deviation without knowing
either the key or the individual notes: it is enough to look at how far the frequencies of the
musical material miss **the nearest grid node**.

---

## 0.7. The cent — the working unit

### Definition

A **cent** = 1/100 of a semitone = 1/1200 of an octave.

```
1 cent   = 2^(1/1200) = 1.000577789…    that is +0.0577789 %
1 octave = 1200 cents
1 semitone = 100 cents
```

Converting a frequency ratio to cents and back:

```
c = 1200 · log₂(f₂ / f₁)               ← how many cents from f₁ to f₂
f₂ = f₁ · 2^(c/1200)
```

In the code this is `SpeedMath.CentsFromRelativeDeviation` and `RelativeDeviationFromCents`,
where the relative deviation `e = f₂/f₁ − 1` stands in for the ratio:

```
c = 1200 · log₂(1 + e)
e = 2^(c/1200) − 1
```

### Why another unit when percent already exists

Three reasons, and all three are in play in this project.

**1. Cents add.** Ratios multiply; cents sum:

```
up a fifth, then up a fourth:
     ratios:  1.5 × 1.3333 = 2.0          (multiplication)
     cents:   702  +  498   = 1200        (addition)
```

That is exactly why [06](06-Acoustics-pitch.md) can write
`measured = turntable_error + tuning_of_the_recording + mastering_drift` — in cents the
contributions genuinely add, whereas in percent that would be an approximation.

**2. The cent is a unit of perception, not of physics.** One cent means the same audible
difference at 100 Hz and at 5000 Hz. Percent has that property only approximately, and only for
small values.

**3. There is a generally accepted audibility scale in cents** (see §0.9), against which the
instrument's accuracy target can be checked.

### Conversion landmarks

| Deviation | In cents | Comment |
|---|---|---|
| +0.01 % | +0.17 | far below the threshold of audibility |
| **+0.1 %** | **+1.73** | the sensor mode's target |
| **+0.3 %** | **+5.19** | the acoustic mode's target, ≈ the audibility threshold |
| +1 % | +17.23 | reliably audible when compared |
| +3 % | +51.2 | half a semitone |
| +5.946 % | +100 | exactly a semitone |
| +35 % | +519.6 | a 33⅓ record played at 45 |

Rules for mental arithmetic:

```
1 % ≈ 17.3 cents              (exactly: 1200/ln2 · e = 1731·e for small e)
1 cent ≈ 0.058 %
1 cent at 33⅓ rpm ≈ 0.0193 rpm
1 rpm at 33⅓      ≈ 3 % ≈ 51 cents
```

### On the asymmetry

The logarithm is not symmetric, and this regularly surprises people:

```
+1 %  →  +17.23 cents
−1 %  →  −17.40 cents
```

The 0.17-cent difference is small, but at 5–10 % the discrepancy becomes significant. That is
why the exact formula is used everywhere in the code, and the linear approximation `1731·e`
only for back-of-the-envelope figures in the documentation. For the same reason the derivative
used in error propagation ([06 §6.6](06-Acoustics-pitch.md)) is taken **at the measured
point**, not treated as a constant.

---

## 0.8. The note grid, A = 440, and folding into a semitone

The grid is anchored in absolute terms by the **tuning standard**: A4 = 440 Hz (ISO 16, since
1955). Everything else is computed:

```
semitone_number_from_A4 = 12 · log₂(f / 440)
f = 440 · 2^(semitones/12)
```

| Semitones from A4 | Note | Frequency, Hz |
|---|---|---|
| −12 | A3 | 220.00 |
| −3 | F♯4 | 369.99 |
| 0 | **A4** | **440.00** |
| +3 | C5 | 523.25 |
| +7 | E5 | 659.26 |
| +12 | A5 | 880.00 |

### Folding into (−50, +50]

The task in [06](06-Acoustics-pitch.md) is to find out not *which* note is sounding but *how
far off the grid* it sits. So the integer part is thrown away:

```
semitones = 12 · log₂(f / 440)
cents     = (semitones − round(semitones)) · 100
```

The result always lies in **(−50, +50]** cents: the distance to the nearest grid node. Example:
`f = 445 Hz` → `12·log₂(445/440) = 0.1957` semitones → `round = 0` → `+19.6 cents`. Another:
`f = 525 Hz` → `3.0058` semitones → `round = 3` → `+0.58 cents` (an almost perfect C5).

This works on any music without knowing the key, because **every** ET note gives the same
deviation from the grid if the whole recording is running with the same speed error.

An important limitation of the same construction: folding discards the octave and the note, so
pitch analysis **cannot tell 33⅓ from 45** — a shift of 519.6 cents looks almost like +19.6
(see [06 §6.6](06-Acoustics-pitch.md)).

---

## 0.9. What a person actually hears

The figures below are typical thresholds; individual variation is large, and everything depends
heavily on the nature of the material.

### Absolute pitch: almost nobody

Without an external reference the overwhelming majority of listeners **will not notice** that a
record is playing 1 % (17 cents) fast. Many will not notice a semitone. People with absolute
pitch are a fraction of a percent of the population, and even for them the accuracy is on the
order of tens of cents.

That is the whole point of the instrument: the ear is no good for absolute measurement, and a
phone is.

### Melodic intervals: 10–25 cents

Mistuning between successive notes (one after another) starts to be noticed at roughly 10–25
cents, and reliably at a semitone.

### Simultaneous tones: a few cents, through beats

Here sensitivity is an order of magnitude higher, and an entirely different mechanism is at
work. Two nearby tones `f` and `f·(1+e)` sum to a signal whose amplitude pulses slowly —
**beats**:

```
beat frequency = |f₂ − f₁| = f · e
```

In terms of cents:

```
beat frequency ≈ f · c / 1731
```

| Mistuning | At 440 Hz | How it sounds |
|---|---|---|
| 1 cent | 0.25 Hz | a faint "breathing" over 4 s |
| 5 cents | 1.3 Hz | a noticeable pulsation |
| 17 cents (1 %) | 4.4 Hz | an obvious "beat", the sound is dirty |
| 50 cents | 12.7 Hz | no longer beats, but dissonance |

This is exactly how instruments are tuned: you listen not to pitch but to the rate of the
beats. The threshold for detecting beats is on the order of **2–5 cents**, which is where the
"5–10 cents" estimate in [01 §1.5](01-Fundamentals.md) comes from.

### Pitch modulation: wow and flutter

If pitch is not offset but **oscillating**, that is a different phenomenon altogether. Hearing
is especially sensitive to modulation around **4 Hz** (the rate of natural vibrato); at that
rate a deviation of about 0.1 % (1.7 cents) can already be noticed on a sustained piano note.
This is precisely why the weighting curve in the wow-and-flutter standards (DIN 45507 and
others) peaks near 4 Hz — see [07](07-Wow-and-flutter.md).

Slow drift (0.5 Hz — the platter's rotation rate) is tolerated better, but on held notes it is
audible as a "swimming" sound.

### Summary in one table

| Phenomenon | Threshold, roughly |
|---|---|
| Absolute pitch without a reference | 100+ cents (or never) |
| Melodic interval | 10–25 cents |
| Beats between simultaneous tones | 2–5 cents |
| Modulation at 4 Hz (wow/flutter) | ~0.1 % ≈ 1.7 cents |

The app's sensor mode (±0.1 % = ±1.7 cents) is more accurate than any of these thresholds; the
acoustic mode (±0.3 % = ±5.2 cents) is roughly at the level of the best of them.

---

## 0.10. What happens to a recording at the wrong speed

Now the tie-in to the problem. A mechanical recording is literally the shape of a groove; the
cartridge reads it at whatever speed the platter dictates. If the platter runs `(1+e)` times
fast, the entire time axis is compressed by `(1+e)`, and therefore:

```
f_playback = f_recorded · (1 + e)        ← ALL frequencies, equally
duration   = recorded_duration / (1 + e)
```

The two effects are **inseparable** (unlike a digital pitch shift, which changes pitch without
touching tempo). A speed error always changes both pitch and tempo — the classic "chipmunk"
effect in the limit.

| Error | Pitch | A 20-minute side gets shorter by |
|---|---|---|
| +0.1 % | +1.7 cents | 1.2 s |
| +0.3 % | +5.2 cents | 3.6 s |
| +1 % | +17.2 cents | 12 s |
| +35 % (33⅓ → 45) | +520 cents (≈ a fourth and a bit) | 5 min 11 s |
| +134 % (33⅓ → 78) | +1472 cents (an octave plus a major 2nd) | — |

Because **all** frequencies are multiplied by the same factor, the ratios between them are
preserved: the music stays in equal temperament, chords stay the same chords, the whole grid is
simply shifted. This is both good news (the method in [06](06-Acoustics-pitch.md) works at all)
and bad — the internal structure of the sound gives no way of telling where the "correct" zero
is ([06 §6.1](06-Acoustics-pitch.md)).

---

## 0.11. Amplitude: RMS and decibels

Loudness comes up less often in these documents, but a couple of things are worth pinning down.

**RMS** (root mean square) is the standard measure of signal level:

```
RMS = √( (1/N) · Σ x[i]² )
```

The square is used rather than the absolute value because the square is proportional to the
**energy** of the vibration, and hearing responds roughly to energy.

**Decibel** is a logarithmic measure of an amplitude ratio:

```
dB = 20 · log₁₀(A / A_reference)
```

| Amplitude ratio | dB |
|---|---|
| 2 | +6.02 |
| 10 | +20 |
| 0.5 | −6.02 |
| 0.02 | −34 |

The last row is the peak-selection threshold in [06 §6.3](06-Acoustics-pitch.md) ("weaker than
2 % of the strongest"): in the units an audio engineer is used to, that is −34 dB from the
maximum.

Loudness, like pitch, is perceived roughly logarithmically (the Weber–Fechner law). That is why
the peak weight in [06](06-Acoustics-pitch.md) is taken as `log(1 + A/thr)`: a linear weight
would hand almost the entire vote to the loudest partial, whereas the ear hears the quiet ones
too.

---

## 0.12. Mini-glossary

| Term | Meaning |
|---|---|
| **Frequency** | cycles per second, Hz; determines pitch |
| **Period** | `1/f`, the duration of one cycle |
| **Amplitude** | the extent of the swing; determines loudness |
| **Fundamental** | the lowest frequency in the set; perceived as the note's pitch |
| **Harmonic / overtone / partial** | the multiple frequencies `2f, 3f, …`; determine timbre |
| **Spectrum** | the decomposition of a signal into frequencies; obtained by FFT ([09](09-DSP-primitives.md)) |
| **Bin** | one "cell" of a spectrum produced by the FFT |
| **Octave** | the 2:1 interval, 1200 cents |
| **Semitone** | 1/12 of an octave, `2^(1/12)`, +5.946 %, 100 cents |
| **Cent** | 1/100 of a semitone, `2^(1/1200)`, +0.0578 % |
| **Temperament** | a system for distributing the compromise across intervals |
| **Equal temperament (ET)** | 12 equal semitones per octave; the modern standard |
| **A = 440 Hz** | the tuning standard (ISO 16); fixes the grid's absolute position |
| **Beats** | loudness pulsation when nearby tones are summed, at rate `\|f₁−f₂\|` |
| **Consonance / dissonance** | how well tones blend; tied to the simplicity of the frequency ratio |
| **Modulation** | slow variation of a parameter (here, pitch) over time |
| **Wow / flutter** | slow / fast parasitic speed modulation ([07](07-Wow-and-flutter.md)) |
| **Envelope** | the slowly varying "loudness contour" of a signal ([09](09-DSP-primitives.md)) |
| **Sample rate** | how many samples per second the ADC writes (usually 44.1 or 48 kHz) |
| **Nyquist** | half the sample rate; frequencies above it are not representable |
| **RMS** | the root-mean-square level of a signal |
| **dB** | `20·log₁₀` of an amplitude ratio |

---

## 0.13. Where to go next

| If you are interested in | Read |
|---|---|
| how rpm relates to rad/s and cents, nominal classification | [01-Fundamentals](01-Fundamentals.md) |
| how a cents deviation is extracted from music | [06-Acoustics-pitch](06-Acoustics-pitch.md) |
| how the rotation period is extracted from clicks | [05-Acoustics-clicks](05-Acoustics-clicks.md) |
| how speed fluctuation is measured | [07-Wow-and-flutter](07-Wow-and-flutter.md) |
| what an FFT, a Hann window and an envelope are | [09-DSP-primitives](09-DSP-primitives.md) |

Three things from this chapter that are used literally everywhere:

```
1. Hearing works with RATIOS of frequencies  →  log₂ everywhere
2. A cent = 1/1200 octave, and cents ADD  →  errors sum directly
3. A speed error multiplies ALL frequencies by one factor  →  the relative
   structure of the sound is preserved, the absolute zero is lost
```
