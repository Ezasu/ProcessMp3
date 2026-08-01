# ProcessMp3

ProcessMp3 is a C# prototype for extracting musical timing from an audio file.
It estimates BPM, locates bar boundaries, and reports a musical offset: the
most likely start of bar 1 relative to the beginning of the file.

## Processing pipeline

1. `AudioLoader` decodes an audio file, converts it to mono PCM, and peak-normalizes it.
2. `FrameGenerator` divides the PCM into overlapping frames.
3. `FeatureExtractor` calculates spectrum, RMS, bass energy, spectral flux,
   MFCC, and chroma for every frame.
4. `HybridBarDetector` estimates BPM and a local beat/bar grid using
   `BeatDetector` and `PatternDetector`.
5. `GlobalMusicalAlignment` refines the grid origin using whole-song structure.

`frameSize` is the number of PCM samples analysed at once (currently `2048`).
`hopSize` is the number of samples moved forward before the next frame
(currently `512`). A smaller hop gives more precise timing but creates more
frames and requires more processing.

## Offset detection

Using the first strong onset as the offset is unreliable: a quiet intro,
fade-in, ambient section, or delayed drum entrance may occur before it.

The global alignment stage does not try to identify a chorus. Instead, it
compares phrase descriptors built from chroma, MFCC, RMS, and spectral flux.
High similarity between non-adjacent phrases establishes a structural anchor.
From that anchor, it walks backwards by phrase duration and scores each start
candidate using:

- beat/bar-grid alignment (30%);
- local onset and bass-transient strength (25%);
- feature change across the candidate boundary (25%);
- similarity to a later non-adjacent phrase (20%);
- a penalty for a locally silent or very weak region (15%).

The original first detected bar and `0:00` are always retained as candidates.
This lets the detector keep immediate starts while correcting edge cases such
as silence before the first musical bar, fades, and weak openings.

Examples where this is useful include a track with several seconds of ambient
pad before drums enter, a song that fades into its first full phrase, and a
recording whose first loud transient occurs after an already-established beat.

## Configuration

`HybridBarDetector.Detect` accepts the following optional settings:

- `barsPerPhrase` — phrase length in bars; defaults to `4`.
- `beatsPerBar` — meter used when building bars; defaults to `4`.
- `debugOutput` — prints the anchor, every candidate score, and the selected
  offset; defaults to `false`.

```csharp
var bars = HybridBarDetector.Detect(
    frames,
    audio.SampleRate,
    out var bpm,
    hopSize: 512,
    barsPerPhrase: 8,
    beatsPerBar: 4,
    debugOutput: true);
```

## Run and debug

Set the input file path in `Starter/Program.cs`, then run:

```powershell
dotnet run --project Starter/Starter.csproj
```

With `debugOutput: true`, the console lists each candidate offset with its
total score and the individual beat, onset, boundary, repetition, and silence
terms, followed by the selected offset. This is useful when tuning material
with ambient intros, gradual fade-ins, or a first strong beat that arrives
after the actual musical beginning.
