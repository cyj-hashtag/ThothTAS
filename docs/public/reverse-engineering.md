# Game integration evidence / 游戏接入摘要

The local read-only investigation identified Unity 5.3.6p5, 32-bit Mono, build THOTH-2019-09-10-174628-r239. Assembly fingerprints are pinned in `docs/game-fingerprint.json` and checked before playback. Full original CIL dumps remain local; use `src/ThothTAS.Inspect` to regenerate evidence from your own game copy.

Input is concentrated in PlayerInput; movement and aiming use Update, rooms and some enemies use FixedUpdate. The preloader patches input reads, original random call sites, scene initialization, window/fullscreen calls, focus handling and owned-resource lifecycle. The runtime uses complete native cycles and Time.captureFramerate, preserving the original physics timestep. Physics.Simulate is not available on this engine version.

The three game assemblies contain 158 direct Unity random calls (147 / 9 / 2). The public inventory retains source locations, assembly/MVID identity, APIs and field-store candidates. Nearby writes do not prove random-value dataflow. Actual random parameters/results/order are recorded separately at runtime; the final representative passed-run index observed 41 static sites, leaving117 unexecuted in that index.

Random is seeded once before game initialization and continues through resources, entry and natural transitions. Pause blocks whole cycles including noise rendering. Loading some sound resources synchronously and normalizing LaserBeat/RhythmLaser gameplay audio clocks are documented TAS behavior changes.

Same-process reset rebuilds scenes/statics and releases only owned resources and registered game persistent roots. Five short resets in one Basic-1 process kept object counts stable, but native clocks and physics internals remain active. Cold and warm baselines differ; quick reset is practice, not exact cold-process equivalence.

Actual evidence and remaining scope are summarized in [verification-20261013.md](verification-20261013.md). The public repository contains tooling, metadata and summaries; it does not contain THOTH binaries, assets, full original game method bodies or local control tokens.
