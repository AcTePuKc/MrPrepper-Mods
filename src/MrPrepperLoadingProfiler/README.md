# Mr. Prepper Loading Profiler

Experimental BepInEx 5.x diagnostic and benchmarking tooling for investigating Mr. Prepper loading behavior.

This project is **not a finished performance fix**. Its purpose is to collect reproducible evidence, isolate loading phases, test candidate optimization levers, and identify where further instrumentation is worth doing.

Current game target:

- Mr. Prepper `1.43g`
- Unity `2019.4.40f1`
- Mono

## Components

The project DLL currently contains two BepInEx plugins:

### Mr. Prepper Loading Profiler 0.8.0

General diagnostic profiler for scene transitions, stalls, memory behavior, save-slot loading, scheduling, coroutines, and Unity async scene requests.

It records or inspects:

- Unity and game version;
- CPU, RAM, GPU and VRAM summary;
- active-scene, scene-load and scene-unload events;
- unload-to-loaded event gaps;
- long-frame counts and immediate stall warnings;
- managed GC collection deltas;
- managed/Mono/Unity memory metrics;
- Unity UI button presses and raw mouse input;
- save-slot `Continue` listener inspection;
- targeted timing for `SaveSlotControler` methods;
- coroutine scheduling and selected `MoveNext()` timings;
- `Invoke` / scheduling diagnostics;
- `SceneManager.LoadSceneAsync` request timing;
- returned `AsyncOperation` state, progress, priority and activation state;
- optional experimental `Application.backgroundLoadingPriority` override;
- selected `VideoPlayer` state changes.

The profiler's loading-priority override should normally be left disabled while running controlled benchmark tests.

### Mr. Prepper Load Benchmark 0.9.1

Compact repeatable benchmark for the save-slot load into `Main16`.

The benchmark starts when the save-slot `Continue` / `SavedPanel/Play` button is pressed and records:

- button -> `LoadSceneAsync("Main16", ...)` request;
- request -> approximately `0.9` async progress;
- `0.9` -> `Main16` `sceneLoaded` callback;
- request -> `sceneLoaded` total;
- largest frame before `sceneLoaded`;
- largest and second-largest frames in the first post-load sample window;
- total post-load sample-window duration;
- button -> end of the post-load sample window.

Completed benchmark runs are appended to:

```text
BepInEx\benchmark-results.csv
```

so repeated game launches do not destroy the benchmark dataset when BepInEx rewrites `LogOutput.log`.

## Current loading findings

A 30-run controlled test was completed on the same machine and save:

- 10 x `BelowNormal`
- 10 x `Normal`
- 10 x `High`

The game's natural `Application.backgroundLoadingPriority` immediately before loading the save was consistently observed as `BelowNormal`.

Median results:

| Priority | Request -> 0.9 | Request -> sceneLoaded | Largest pre-load frame | Largest post-load frame | Button -> end of post-load window |
| --- | ---: | ---: | ---: | ---: | ---: |
| BelowNormal | 5.854 s | 14.451 s | 5584.0 ms | 3744.3 ms | 21.217 s |
| Normal | 5.826 s | 14.255 s | 5768.5 ms | 3701.6 ms | 20.973 s |
| High | 5.834 s | 14.240 s | 5756.5 ms | 3748.5 ms | 21.008 s |

`Normal` and `High` are effectively tied in this dataset. The differences are too small to treat either one as a meaningful loading optimization.

More importantly, the same two major stalls remain under all three priorities:

- roughly `5.5-5.8 s` during Unity's async scene-loading path before/around progress `0.9`;
- roughly `3.7 s` in the immediate `Main16` post-load window.

The first large stall occurs after the managed `LoadSceneAsync` request has already returned. Current evidence therefore points away from `SaveSlotControler.Play()` or the managed scene-request call itself and toward Unity scene loading/integration work.

This does **not** prove that textures are the cause. Possible contributors include scene deserialization/integration, asset decompression/loading, textures, meshes, audio, object creation, materials/shaders, and other native-engine work.

Full methodology, limitations, interpretation, and raw data:

- [`docs/research/loading-priority-benchmark-2026-08-20.md`](../../docs/research/loading-priority-benchmark-2026-08-20.md)
- [`docs/research/loading-priority-benchmark-2026-08-20.csv`](../../docs/research/loading-priority-benchmark-2026-08-20.csv)

## Configuration

BepInEx creates the configuration files after the first run.

Profiler example:

```ini
[General]
ProfilerEnabled = true

[Frames]
StallThresholdMs = 50
ImmediateStallLogThresholdMs = 250
SummaryIntervalSeconds = 10
IgnoreUnfocused = true

[Experiment]
OverrideBackgroundLoadingPriority = false
BackgroundLoadingPriority = High
```

Benchmark example:

```ini
[Benchmark]
Enabled = true
BackgroundLoadingPriority = Normal
PostLoadFrames = 8
WriteCsv = true
```

For controlled priority tests, keep:

```ini
OverrideBackgroundLoadingPriority = false
```

in the profiler config so only the benchmark plugin controls the tested priority.

## Building

The project follows the same local-game-reference pattern as the other runtime mods in this repository. It reads `GameDir` from the repository-local `User.targets` file when present, otherwise checks `MR_PREPPER_DIR`, then falls back to the default Steam location.

```powershell
dotnet build .\src\MrPrepperLoadingProfiler\MrPrepperLoadingProfiler.csproj -c Release
```

After a successful build the DLL is staged to:

```text
dist\MrPrepperLoadingProfiler\MrPrepperLoadingProfiler.dll
```

and copied into the matching BepInEx plugin folder under `GameDir`.

## Automated benchmark harness

The branch also contains an AutoHotkey v2 + PowerShell harness for repeatable save-load testing.

Files:

```text
scripts\mrprepper-benchmark.ahk
scripts\run-loading-benchmark.ps1
scripts\compare-loading-benchmarks.ps1
```

Example:

```powershell
.\scripts\run-loading-benchmark.ps1 -Priority Normal -Runs 10
```

The harness:

1. updates the benchmark priority in the BepInEx config;
2. disables the profiler's experimental priority override;
3. starts Mr. Prepper;
4. uses AutoHotkey to follow the normal Continue -> save-slot Continue path;
5. waits for a new benchmark CSV row;
6. closes the game;
7. archives that run's `BepInEx\LogOutput.log` under `BepInEx\benchmark-logs`;
8. repeats for the requested number of runs.

Archived logs are named by priority, run number, success state, and timestamp, for example:

```text
Normal-001-ok-20260820-113812-421.log
Normal-002-ok-20260820-113905-118.log
High-003-failed-20260820-114002-774.log
```

The AutoHotkey helper uses Unity-coordinate reference points converted to AutoHotkey client coordinates and includes a best-effort click for the game's recovery/display-warning prompt. The benchmark interval itself begins later, at the save-slot button, so menu timing and that warning are outside the measured load interval.

## Interpreting the logs

Useful profiler markers include:

```text
[SCENE REQUEST]
[SCENE REQUEST RETURN]
[SCENE ASYNC START]
[SCENE ASYNC]
[SCENE LOADED]
[SCENE UNLOADED]
[STALL]
[POST-LOAD FRAME]
[TARGET METHOD]
[TARGET STEP]
[LOAD PRIORITY]
[LOAD BENCHMARK]
```

The `unload-to-loaded event gap` is deliberately labelled as an event gap. It is not the complete loading-screen duration because Unity may perform substantial work before the unload callback fires.

Likewise, reaching async progress `0.9` does not identify one exact Unity subsystem. It is only an observable boundary in the loading operation.

## Next investigation

The loading-priority experiment has largely ruled out `Application.backgroundLoadingPriority` as a strong optimization lever for this save/load path.

The next useful target is the approximately `3.7 s` `Main16` post-load frame.

Planned instrumentation is aggregate timing of game MonoBehaviour lifecycle work during the first few `Main16` frames, especially:

```text
Awake()
OnEnable()
Start()
```

The goal is to produce low-noise summaries such as total calls, total time, and maximum single-call time per `Type.Method`, rather than logging every lifecycle invocation individually and distorting the load.

If that post-load cost is mostly managed game initialization, it may expose actionable opportunities such as duplicated work, cache rebuilding, synchronous scans, or initialization that can safely be deferred. If it is not, the remaining bottleneck is more likely to require asset/content-level or native Unity profiling rather than a simple BepInEx patch.
