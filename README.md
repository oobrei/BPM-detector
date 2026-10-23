# BPM Detector

A compact desktop app that detects the tempo (BPM) of audio files and shows the detected beat grid.

## Build

```
dotnet build BPM-Detector.slnx -c Release
```

## Run

```
dotnet run --project src/Bpm.App/Bpm.App.csproj
```

Requires the .NET 10 SDK. ffmpeg must be on `PATH` (or set `BPM_FFMPEG`) for audio decoding.

## Credits

The tempo analysis is a C# port of Essentia's [RhythmExtractor2013](https://essentia.upf.edu/reference/std_RhythmExtractor2013.html)
(multi-feature onset functions, TempoTapDegara beat tracking, TempoTapMaxAgreement) from the
[MTG / Universitat Pompeu Fabra](https://github.com/MTG/essentia). Essentia is licensed under
AGPL-3.0, which this port inherits.

## Standalone exe

```
dotnet publish src/Bpm.App/Bpm.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```
