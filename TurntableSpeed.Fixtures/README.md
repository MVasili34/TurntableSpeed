# TurntableSpeed.Fixtures

Recorded material the golden tests replay (spec §7.4). Every file here is listed in
`manifest.csv`, and every row in the manifest is checked twice: once against the physical truth
to the accuracy the specification demands, and once against the value the estimator produced
when the fixture was recorded, so that an algorithm change cannot quietly move the answer.

## What is here now

Synthetic fixtures only — generated click trains and magnetometer traces with parameters known
exactly. They prove the golden mechanism works and give it something to guard, but they cannot
do the job real recordings do: synthetic clicks are cleaner than dust, and a generated magnetic
field has none of the awkwardness of a real motor.

## What should be added

Short recordings from real turntables, 10–20 seconds each so the repository stays small:

- **Audio** — mono WAV, 44100 or 48000 Hz, captured through an unprocessed input. The lead-in
  groove is ideal: the clicks are unobstructed and the speed is already up.
- **Sensors** — CSV written by the diagnostics screen's export, `t,x,y,z`, with the sensor and
  the conditions in a `#` comment at the top.

Each needs a manifest row with an `expectedRpm` established independently of this app — a
strobe disc, a frequency counter on the motor, or a second measurement device. A golden fixture
whose expected value came from the software under test proves nothing.

## Manifest format

```
file,kind,expectedRpm,tolerancePercent,goldenRpm,notes
```

| Column | Meaning |
|---|---|
| `file` | File name in this directory |
| `kind` | `AudioClick` or `Magnetometer` |
| `expectedRpm` | The physical truth, measured independently |
| `tolerancePercent` | Accuracy the specification demands of this method |
| `goldenRpm` | What the estimator produced when the fixture was recorded |
| `notes` | Free text; commas are replaced with semicolons |

Lines starting with `#` are comments.

## Re-recording the golden column

`goldenRpm` is deliberately not updated by the test run — a regression test that rewrites its
own expectation catches nothing. When a change to the estimators is intended to move these
numbers, call `FixtureBuilder.Build` for the synthetic entries, or edit the row by hand for a
real recording, and say in the commit why the value moved.
