# Mr. Prepper loading-priority benchmark - preliminary findings

Date: 2026-08-20

Branch: `feature/loading-profiler`

Game: Mr. Prepper 1.43g

Engine: Unity 2019.4.40f1 / Mono

Profiler: `MrPrepperLoadingProfiler` 0.8.0

Benchmark plugin: `Mr. Prepper Load Benchmark` 0.9.1

## Purpose

This experiment tested whether `Application.backgroundLoadingPriority` is a useful optimization lever for loading an existing save into the `Main16` scene.

The goal was not to prove a specific Unity subsystem is responsible for the loading time. The goal was to isolate the observable loading phases, vary only the background loading priority, and see whether the large stalls or total loading time change materially.

## Test method

The benchmark starts when the save-slot `Continue` / `SavedPanel/Play` button is pressed.

For each run it records:

- time from the save-slot button to the first `SceneManager.LoadSceneAsync("Main16", ...)` request;
- time from that request until the returned `AsyncOperation` reaches approximately `0.9` progress;
- time from `0.9` progress until the `Main16` `sceneLoaded` callback;
- request-to-`sceneLoaded` total;
- largest frame before `sceneLoaded`;
- the largest and second-largest frames in the first 8 post-load frames;
- total time from the save-slot button until the end of that 8-frame post-load window.

The normal loading profiler's priority override was disabled during these runs. The benchmark plugin alone applied the requested priority and restored the game's observed natural priority after `Main16` loaded.

The game's natural priority immediately before loading the save was consistently observed as `BelowNormal`.

Automation used AutoHotkey + PowerShell so the same UI path could be repeated without manually timing the clicks. The benchmark itself starts at the save-slot button, so startup-menu timing and occasional recovery/display-warning behavior before that point are outside the measured interval.

## Dataset

30 automated runs were collected on the same machine and save:

- 10 x `BelowNormal`
- 10 x `Normal`
- 10 x `High`

Raw data: [`loading-priority-benchmark-2026-08-20.csv`](./loading-priority-benchmark-2026-08-20.csv)

Test machine:

- AMD Ryzen 7 5800X
- 32 GB RAM
- NVIDIA GeForce RTX 5070 Ti
- Windows 11 64-bit

## Median results

| Priority | Button -> request | Request -> 0.9 | 0.9 -> sceneLoaded | Request -> sceneLoaded | Largest pre-load frame | Largest post-load frame | Post-load 8-frame window | Button -> end of post-load window |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| BelowNormal | 2.538 s | 5.854 s | 8.601 s | 14.451 s | 5584.0 ms | 3744.3 ms | 5039.4 ms | 21.217 s |
| Normal | 2.532 s | 5.826 s | 8.428 s | 14.255 s | 5768.5 ms | 3701.6 ms | 4988.5 ms | 20.973 s |
| High | 2.536 s | 5.834 s | 8.404 s | 14.240 s | 5756.5 ms | 3748.5 ms | 5068.8 ms | 21.008 s |

## What the data supports

`Normal` and `High` are extremely close. Their median request-to-scene-loaded times differ by only about 15 ms, and their median total button-to-post-load-window times differ by about 36 ms. Those differences are too small to treat as a meaningful optimization result from this dataset.

`BelowNormal` is somewhat slower in the overall timing metrics in this sample: roughly 0.21 s behind `High` for request-to-scene-loaded and roughly 0.24 s behind `Normal` for the total measured window. However, it also produced the smallest median largest pre-load frame. This is another reason not to reduce the result to "higher priority is faster".

The large stalls themselves remain present under all three priorities:

- roughly 5.5-5.8 seconds for the large pre-load frame around the `AsyncOperation` reaching `0.9`;
- roughly 3.7 seconds for the largest frame immediately after `Main16` is loaded.

Changing `Application.backgroundLoadingPriority` therefore does not appear to materially remove either major bottleneck.

## Current interpretation

The first large stall occurs after `LoadSceneAsync` returns and while Unity advances the scene-loading operation toward `0.9`. The managed call to `LoadSceneAsync` itself completes in well under a millisecond in prior profiler runs. This strongly suggests that the large block is inside Unity's scene-loading / integration work rather than inside `SaveSlotControler.Play()` or the managed request call itself.

This does **not** identify a single exact cause. Candidate work can include scene deserialization/integration, asset decompression/loading, textures, meshes, audio, object creation, materials/shaders, and other native engine work. The current instrumentation is not sufficient to say that textures specifically are the bottleneck.

The approximately 3.7 s post-load frame is a separate and potentially more actionable target for a BepInEx mod. The next useful experiment is to profile `Main16` initialization work, especially aggregate `Awake`, `OnEnable`, and `Start` costs for game MonoBehaviours during the first few frames after the scene loads.

## Limitations

These results should be treated as preliminary rather than as a general performance benchmark:

- one PC;
- one save;
- one game version;
- 10 runs per priority;
- the three priority groups were run sequentially rather than randomized/interleaved, so OS/file-cache warming or run-order effects can influence small differences;
- this is managed/BepInEx instrumentation, not a Unity native profiler capture;
- the experiment measures loading behavior but does not identify exact asset or native-engine costs.

Because the observed differences between `Normal` and `High` are tiny, substantially more repetitions would not be the best next use of time unless the priority experiment is redesigned as randomized/interleaved testing. The larger opportunity is to identify what consumes the multi-second pre-load and post-load frames.

## Preliminary conclusion

`Application.backgroundLoadingPriority` is not a strong loading optimization lever for this save/load path. `Normal` and `High` perform effectively the same within this test, while `BelowNormal` is slightly slower overall but does not produce uniformly worse frame behavior.

The experiment is useful because it rules out a simple priority change as the main solution and narrows the next investigation toward scene/asset loading and `Main16` post-load initialization.

This is a diagnostic result, not yet a finished "faster loading" mod.
