# THOTH TAS editor

Download the Windows release, extract it, and run `scripts/Create-TasCopy.ps1` in its `ThothTAS` folder. The script reads your existing `D:\Play\Steam\steamapps\common\THOTH` installation; use `-SourceDirectory` for another location. It validates the pinned game version and creates a separate `game` copy. Start `dist/Launch-Editor.cmd`.

## Playback and the inline frame marker

Open a UTF-8 `.tas`, Validate, then Play. Input frames last 1/60 second. The highlighted input line has a blue display marker:

```text
60, M=40, A=40    --- 34
```

`34` means the segment has completed 34 input frames. The marker is not inserted into the script, saved file or frozen run snapshot. It updates during playback and stepping, follows scrolling, and clears while waiting for a chapter transition or after Stop. The right panel also shows segment progress and room events.

Playback uses a frozen snapshot. Editing and saving only changes the draft. The run snapshot remains highlighted correctly; a modified draft loses its execution marker. Replay applies the changed draft.

Use **语言 / Language** to switch between **简体中文** and **English** without restarting playback. The setting persists locally in `work/editor-settings.json`. Script grammar, game scene names, file paths and original game/log diagnostics are unchanged by this presentation setting.

## Compose independent level scripts

Click **Compose TAS…**, select the scripts, and arrange them in natural level order. The window provides Add scripts, Remove, Move up and Move down. Click Create composed draft, then Save as a new file. Originals are read only by composition.

The sources must use the same Seed and naturally adjacent ordinary rooms. The output uses `Room,Basic-1`, `Room,Basic-2`, etc. A chapter's unused tail is skipped when that room completes. Input is zero during the natural transition; the next chapter starts at its first local input frame after the player becomes active. A chapter that exhausts its input budget without completing fails visibly.

The included `examples/composed/basic-1-basic-2.tas` combines the supplied examples. The nominal input budget is 799 frames; actual successful playback is 495 frames. Basic-1 completes at frame 147; Basic-2 becomes active at 273 and completes at 495.

Successful isolated levels do not automatically compose successfully. Later rooms inherit earlier random consumption, time and entry state. Seed is applied once per replay, not per Room. Verify the entire composed replay, and adjust later chapter inputs from their continuous entry state if needed.

Click Verify ×5 to compare independent cold processes, optionally including pause, step and speed modes. Or run:

```powershell
.\dist\host\ThothTAS.Host.exe compose --out .\work\combined.tas .\examples\basic-1.tas .\examples\basic-2.tas
.\dist\host\ThothTAS.Host.exe verify .\work\combined.tas --count 2 --modes --out .\work\verification\combined
```

## Quick reset

Quick reset (same process) keeps the owned game PID and rebuilds the start using a new frozen draft and the fixed save. It is intended for practice. It is not equivalent to a new process: native absolute time and physics state are not fully reset. Exact verification always uses independent processes.

## Scope

Windows x64 editor, Unity 5.3.6p5 x86 game, THOTH `THOTH-2019-09-10-174628-r239`. Single player, ordinary fixed rooms. No instant save, rewind, seeking, two-player, Hardcore, generated modes or cross-computer determinism guarantee.

See [public verification](public/verification-20261013.md) for actual tests and their limits. Source-level pure tests do not substitute for running the actual game.

## Community Speedrun Mod and faster practice reset

Optional rmadlal v2.8 timers and window support: run `scripts/Prepare-CommunityMod.ps1`, then select **Community timers** in the editor. The game assembly is replaced in memory only. Timer clocks use complete 60 Hz game cycles, so pause and playback speed do not change the same TAS result; they are not ordinary wall-clock RTA. Changing the profile takes effect on the next Replay through a new process.

Warm reset restores the cached sound-bank hierarchy, selectors and audio settings and rebuilds other gameplay scenes and pools. Enable **Quick reset (same process)** beside Replay: measured time to the active frame-zero baseline is now about 0.55 s, down from about 2.56 s. First launch still loads normally. Native clocks and floating-point differences remain; some longer paths diverge in random records. Exact verification uses independent cold processes. `verify-reset` reports field differences throughout the replay. [Actual measurements and limitations](public/reset-subsecond-20261013.md). [Setup, controls and verification](public/community-mod-guide.md) · [Actual compatibility and reset report](public/community-reset-20261013.md).
