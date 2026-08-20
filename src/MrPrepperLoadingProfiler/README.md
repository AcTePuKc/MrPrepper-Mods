# Mr. Prepper Loading Profiler

Experimental BepInEx 5.x diagnostic mod for measuring scene transitions, long frames, memory usage, and managed GC activity in Mr. Prepper.

This is a baseline profiler, not a performance fix. Its purpose is to collect reproducible evidence before attempting loading or stutter optimizations.

## What it records

- Unity and game version
- CPU, RAM, GPU and VRAM summary
- scene load, unload and active-scene events
- the elapsed gap between a scene unload event and the following scene-loaded event
- long-frame counts and longest frame per summary window
- immediate warnings for very long frames
- managed GC collection deltas
- managed memory and process private memory

The scene `unload-to-loaded event gap` is deliberately labelled as an event gap. It is not claimed to represent the complete loading-screen duration because Unity may perform work before the unload callback fires.

## Default thresholds

```ini
[General]
Enabled = true

[Frames]
StallThresholdMs = 50
ImmediateStallLogThresholdMs = 250
SummaryIntervalSeconds = 10
IgnoreUnfocused = true

[Scenes]
LogSceneEvents = true

[Memory]
LogMemory = true
```

BepInEx creates the actual configuration file after the first run.

## Building

The project follows the same local-game-reference pattern as the other runtime mods in this repository. It reads `GameDir` from the repository-local `User.targets` file when present and otherwise checks `MR_PREPPER_DIR` before falling back to the default Steam location.

```powershell
dotnet build .\src\MrPrepperLoadingProfiler\MrPrepperLoadingProfiler.csproj -c Release
```

After a successful build the DLL is staged to:

```text
dist\MrPrepperLoadingProfiler\MrPrepperLoadingProfiler.dll
```

and copied into the matching BepInEx plugin folder under `GameDir`.

## Testing workflow

For useful comparison data, test the same save and repeat the same transition several times. Keep the game focused while profiling because minimized or unfocused frames can create misleading stall measurements.

Useful test sequence:

1. Start the game and load a known save.
2. Remain in one area for at least 20 seconds to capture a baseline.
3. Travel between the same two areas three or more times.
4. Perform a transition known to feel unusually slow.
5. Exit normally.
6. Inspect `BepInEx\LogOutput.log` for lines beginning with `[SCENE`, `[STALL]`, `[SUMMARY]`, `[ACTIVE SCENE]`, `[START]`, or `[QUIT]`.

## Next step

Once a log identifies a repeatable slow transition, the profiler can be expanded with targeted Harmony instrumentation around specific Mr. Prepper systems. That is preferable to patching large numbers of game methods blindly, which would add profiling overhead and could distort the measurements.
