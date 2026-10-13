# THOTH TAS

English · [简体中文](README.zh-CN.md)

A Windows TAS editor and replay module for **THOTH**. English / Simplified Chinese UI, text scripts, analog movement/aim, world-coordinate targets, frame stepping, replay verification and natural multi-room composition.

## Run

1. Download the [Windows release](https://github.com/cyj-hashtag/ThothTAS/releases/latest) and extract its `ThothTAS` folder.
2. Run `scripts/Create-TasCopy.ps1`. It reads your existing local THOTH installation, validates the pinned version, creates `game`, and downloads the pinned BepInEx x86 package. For another game location, pass `-SourceDirectory`.
3. Run `dist/Launch-Editor.cmd`. Open a TAS, Validate and Play.

The release contains the .NET 10 runtime. You need your own local game files: no THOTH executable, Unity game assembly or game assets are distributed here. The source game is read only; all patches and saves belong to the independent TAS copy. Launch through the editor/host: TAS sessions disable Steam callbacks and achievements in memory.

## Editor

- The highlighted line has a blue **`--- 34`** marker: 34 input frames completed in that segment. It never becomes script text.
- **Compose TAS…** selects scripts and opens a window with Add, Remove, Move up/down and Create draft.
- **语言 / Language** switches between **简体中文** and **English** during playback and remembers the choice locally.
- Playback freezes an immutable snapshot. Editing and saving changes the draft; Replay applies it.
- Unsaved drafts prompt Save / Don't save / Cancel before closing or replacing a document. Draft checkpoints persist after 350 ms of idle typing and can be recovered after a crash.
- Select **Deterministic rules (modified game)** for same-process Replay. Original rules continue to use fresh processes. New drafts use modified rules; existing scripts retain their declared rules.

[English guide](docs/editing-guide-en.md) · [中文操作说明](docs/editing-guide-zh.md)

## Scripts

```text
Version,1
Start,Basic-1
Seed,1

60, M=45, A=270
60, M=45, A=(30,40)
30, N
60, M=0.5:0, A=1:0
```

Angles: right 0°, up 90°, left 180°, down 270°. `M=x:y` moves, `A=x:y` aims, `A=(x,y)` aims at a world point, `N` stops movement. Input periods are 1/60 second; original physics step is 0.01 second. Original deadzones and firing cadence are retained.

Compose naturally adjacent rooms with one Rules profile and seed. Each `Room` chapter starts when its player activates; completed rooms skip unused input tails and transition with zero input. Isolated success is not enough: later rooms inherit earlier RNG consumption, time and entry state. Verify the complete composed replay.

```powershell
.\dist\host\ThothTAS.Host.exe compose --out .\work\combined.tas .\examples\basic-1.tas .\examples\basic-2.tas
.\dist\host\ThothTAS.Host.exe verify .\work\combined.tas --count 2 --modes --out .\work\verification\combined
```

## Source and checks

Requires Windows and .NET 10 SDK for building. From a source clone, first obtain the release's `dist` folder and run `Create-TasCopy.ps1` to establish the licensed Unity/BepInEx references, then:

```powershell
.\scripts\Build.ps1
dotnet run --project .\tests\ThothTAS.Core.Tests -c Release
.\scripts\Verify-All.ps1 -OutputDirectory .\work\acceptance\manual
```

Pure core tests need no game. Actual replay verification must run the real game. The [public verification summary](docs/public/verification-20261013.md) distinguishes actual runs, pure checks and untested paths. Full original CIL and local runtime logs are not published; the read-only `ThothTAS.Inspect` source can regenerate local evidence from your own files.

## Validated scope and limits

Pinned build: `THOTH-2019-09-10-174628-r239`, Unity 5.3.6p5 x86 Mono. Single player, ordinary fixed rooms. The supplied Basic-1/Basic-2 composition completes at frame495; independent repeats and pause/step/speed checks have matched exact gameplay fields and complete random records on the current test machine.

No cross-computer guarantee, instant save, rewind, seeking, two-player, Hardcore or generated rooms. Same-process Replay is available under modified rules; original-rule `verify-reset` remains an explicit unsafe diagnostic. Selected sound loading is synchronous and LaserBeat/RhythmLaser gameplay clocks are normalized by input frames; this TAS copy is not claimed to match every instantaneous ordinary realtime behavior. Not all random sites and paths have been executed.

[Reverse-engineering summary](docs/public/reverse-engineering.md) · [Random-call inventory](docs/public/random-calls.json) · [Third-party notices](THIRD-PARTY-NOTICES.md)

## Community Speedrun Mod and reliable replay

Optional rmadlal v2.8 timers and window support: run `scripts/Prepare-CommunityMod.ps1`, then select **Community timers**. Timers use complete 60 Hz game cycles; profile changes apply on the next Replay through a fresh process.

## Modified deterministic rules and fast replay (v0.4.0)

Select **Deterministic rules (modified game)**, or add `Rules,Deterministic-1` after `Version,1`. New drafts include it; existing files keep original rules. First launch still loads and validates the game. Subsequent Replay uses the same process, rebuilding scenes/pools, restoring fixed saves and sound banks, and resetting logical clocks. Stop, Rules changes and community-profile changes require a new process.

The modified profile replaces all 158 scanned direct Unity random calls with neutral constants: float midpoint, legal integer midpoint, and value=0.5. These calls do not consume Unity random state; Seed does not change their results. Physics runs at 60Hz with no rigidbody interpolation or physical rotation and no death slow motion. Managed Update order is stable; game and relevant audio clocks use logical input frames. Native clocks remain in separate diagnostic logs.

Gameplay differs from original THOTH and existing TAS inputs may need adjustment. This is an authoring profile, not original-game or ordinary community RTA equivalence. Natural composition still requires complete verification. [Rules and actual measurements](docs/public/deterministic-rules-20261013.md).

```powershell
.\scripts\Verify-Deterministic.ps1 -ScriptPath .\examples\basic-1.tas -Count 5 -Modes
.\scripts\Verify-Deterministic.ps1 -Count 5 -CommunityMod -CheckAllStarts
```

`CheckAllStarts` requires the .NET 10 SDK; it checks the 64 rooms in the natural sequence. The extra Teleporters-5 resource has missing references and is excluded.
