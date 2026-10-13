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

## Unsaved changes and draft recovery

Closing, opening another file or composing over a modified draft prompts **Save / Don't save / Cancel**. Cancel leaves the editor and game connection intact. Save failures or a cancelled Save as also keep the window open. Don't save leaves the document unchanged and archives a recoverable history copy.

After 350 ms of idle typing, a separate checkpoint is atomically written in `work/draft-recovery`. Leaving the editor also checkpoints the draft. Saving the document clears the current checkpoint. A later startup offers recovery for checkpoints left by a crashed editor; **Recover draft…** previews checkpoints and older `.tas` backups. Save or Save as after recovery.

## Reliable replay

v0.3.2 Replay restores the fixed save and starts a new game process. The former same-process option is disabled: Basic-3 showed differing physics ticks, random records and collision outcomes after warm resets. Ordinary Host sessions reject it too; `verify-reset` explicitly enables unsafe diagnostics.

Inputs authored under a warm-reset state may need adjustment for the fixed cold baseline. This restores startup waiting; the former roughly 0.55 s practice reset is not a reliable authoring path. See the [actual investigation](public/draft-replay-fix-20261013.md).

## Community Speedrun Mod

Run `scripts/Prepare-CommunityMod.ps1`, then select **Community timers**. The community v2.8 assembly is loaded in memory. Timer clocks use complete 60 Hz cycles; a profile change applies on the next Replay through a fresh process. Reliable replay is used with either profile. [Setup and controls](public/community-mod-guide.md).
