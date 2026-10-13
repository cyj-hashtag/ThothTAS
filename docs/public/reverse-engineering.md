# Game integration evidence / 游戏接入摘要

The local read-only investigation identified Unity 5.3.6p5, 32-bit Mono, build THOTH-2019-09-10-174628-r239. Assembly fingerprints are pinned in `docs/game-fingerprint.json` and checked before playback. Full original CIL dumps remain local; use `src/ThothTAS.Inspect` to regenerate evidence from your own game copy.

Input is concentrated in PlayerInput; movement and aiming use Update, rooms and some enemies use FixedUpdate. The preloader patches input reads, original random call sites, scene initialization, window/fullscreen calls, focus handling and owned-resource lifecycle. The runtime uses complete native cycles and Time.captureFramerate, preserving the original physics timestep in Original rules. Deterministic-1 deliberately uses 60Hz physics. Physics.Simulate is not available on this engine version.

The three game assemblies contain 158 direct Unity random calls (147 / 9 / 2). The public inventory retains source locations, assembly/MVID identity, APIs and field-store candidates. Nearby writes do not prove random-value dataflow. Actual random parameters/results/order are recorded separately at runtime; the final representative passed-run index observed 41 static sites, leaving117 unexecuted in that index.

Random is seeded once before game initialization and continues through resources, entry and natural transitions. Pause blocks whole cycles including noise rendering. Loading some sound resources synchronously and normalizing LaserBeat/RhythmLaser gameplay audio clocks are documented TAS behavior changes.

Same-process reset rebuilds scenes/statics and releases only owned resources and registered game persistent roots. Five short resets in one Basic-1 process kept object counts stable, but native clocks and physics internals remain active. Cold and warm baselines differ; quick reset is practice, not exact cold-process equivalence.

Actual evidence and remaining scope are summarized in [verification-20261013.md](verification-20261013.md). The public repository contains tooling, metadata and summaries; it does not contain THOTH binaries, assets, full original game method bodies or local control tokens.

## v0.4.0 modified deterministic profile

Fresh REA inspection re-established the stock main assembly SHA256 `9675a94cce19c883f0e0927e9a9185ff8c793080698100a8717ed5aaa4b2555c`, MVID `25f80738-c9ed-41af-8c1e-1674c7fbcfe6`. Evidence `ev_32d819f034c1c5f31cbac0c8471ab95c617396375edc5ee4623818bd66d05121` is stored locally with current session evidence. No reliance on an earlier session's Evidence handle.

Deterministic-1 replaces the 158 audited direct random calls with neutral constants, changes physics to 60Hz, disables interpolation/physical rigidbody rotation and death slow motion, and uses logical game clocks. A first actual prototype had identical entity fields and random records but different music/sound-folder Update ordering. The final implementation dispatches original managed Update bodies in stable hierarchy/type/component order; it does not hide, sort or delete observed phase records.

Read-only CIL scanning found 8 AudioSource time reads, 16 isPlaying reads, 6 time writes and 6 PlayDelayed calls in the main assembly; the two firstpass assemblies had none of those calls. Community v2.8 has the same method/API set. GeneralSoundBank.PlayConnector (`06000618`, stock IL0006 isPlaying, IL0016 Play, IL0036 Range, IL003b set_time) conditionally consumes random calls. BeatMaster.FixedUpdate (`06000013`, IL003a get_time, IL0070 beatTimer, IL0086 hit) uses audio time for beats. MusicTrack uses seek before Play; connector uses Play before seek. Modified mode virtualizes those audio queries using frames while retaining native audio output. The purely cosmetic persistent launch jingle is removed at the uniform scene boundary.

These controls alter gameplay and do not reset or replace every internal PhysX structure. Exact cold/warm equivalence is established by actual replay comparison for the listed modified-rule paths; native telemetry and bootstrap traces remain separately available. [Rules and actual validation](deterministic-rules-20261013.md).
