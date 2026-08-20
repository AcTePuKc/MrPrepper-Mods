# Dialogue tag Regex cache benchmark - 2026-08-20

## Purpose

Measure whether reusing the `Regex` instances created by `TextTag.GetTag(String&, String, Boolean)` reduces Main16 post-load time.

The experiment replaces the single `new Regex(pattern)` inside `TextTag.GetTag` with a cache lookup. Game tag parsing logic is otherwise left unchanged.

## Method

- Game: Mr. Prepper 1.43g
- Unity: 2019.4.40f1
- Machine: Ryzen 7 5800X, RTX 5070 Ti, 32 GB RAM
- Benchmark priority: High
- Natural game priority: BelowNormal
- Profiler mode: Clean
- Variants: Regex cache On / Off
- Runs: 10 per variant, 20 total
- Ordering: alternating, with pair order reversed every round
- Benchmark harness: `scripts/run-loading-benchmark.ps1`
- Raw experiment data: `dialogue-tag-regex-cache-benchmark-2026-08-20.csv`

Diagnostic profilers were disabled for this A/B run. Only the load benchmark and the cache experiment were active.

## Median results

| Metric | Cache Off | Cache On | On - Off | Change |
| --- | ---: | ---: | ---: | ---: |
| Request -> SceneLoaded | 14.276 s | 14.300 s | +0.024 s | no material change |
| Largest pre-load frame | 5673.3 ms | 5613.6 ms | -59.7 ms | -1.1% |
| Largest post-load frame | 3807.7 ms | 3214.6 ms | -593.1 ms | -15.6% |
| Post-load 8-frame window | 5143.1 ms | 4544.6 ms | -598.5 ms | -11.6% |
| Button -> end of post-load window | 21.127 s | 20.559 s | -0.568 s | -2.7% |

## Paired-round consistency

For the 10 alternating A/B rounds:

- Largest post-load frame was faster with cache enabled in 10/10 pairs.
- Post-load 8-frame window was faster with cache enabled in 10/10 pairs.
- Total button-to-post-window time was faster with cache enabled in 10/10 pairs.
- Mean paired reduction in largest post-load frame: about 603 ms.
- Mean paired reduction in post-load window: about 606 ms.
- Mean paired reduction in total button-to-post-window time: about 0.598 s.
- Request-to-SceneLoaded did not move consistently, which matches the optimization being post-load rather than scene-loading work.

## Interpretation

The cache produces a repeatable reduction of roughly 0.6 seconds in Main16 post-load work on this machine and save. The effect is isolated to the post-load phase and does not materially change the earlier scene-loading stall.

Earlier diagnostics showed `TextTag.GetTag` being called 22,203 times for the tested Main16 load, while only seven distinct tag names/patterns were involved. The cache experiment therefore removes repeated construction of thousands of equivalent `Regex` instances.

This is strong evidence that Regex instance reuse is a valid optimization target. It should still receive functional smoke testing of dialogue tags (`random`, `voice`, `animation`, `duration`, `if`, `event`, `prepper`) before being promoted from an experiment to a standalone performance mod.

## Scope and caveats

These results are from one machine, one game build, and one save. They establish a robust local performance effect, not a universal timing guarantee. The pre-load ~5.6 s main stall remains a separate bottleneck and is not addressed by this optimization.
