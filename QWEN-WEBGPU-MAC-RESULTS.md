# Qwen2.5-7b WebGPU tool-calling on an 8 GB Mac

Recorded run: October 9, 2026. Same harness, prompts, tool schema, temperature 0
and automatic tool choice as `QWEN-WEBGPU-RESULTS.md` (Windows).

## Environment

| Item | Value |
|---|---|
| Machine | Apple Silicon Mac, arm64, 8 GB RAM, 3 GB swap |
| Runtime | .NET 10.0.107, `osx-arm64` |
| Foundry SDK | Microsoft.AI.Foundry.Local 1.2.4 (cross-platform package, not WinML) |
| Model | qwen2.5-7b-instruct-generic-gpu, 5,324 MB |
| Execution provider | WebGpuExecutionProvider |

Local csproj changes needed to build on macOS (not committed here): target
`net10.0` instead of `net10.0-windows10.0.22621.0`, package
`Microsoft.AI.Foundry.Local` instead of `.WinML`, and `RuntimeIdentifier`
`osx-arm64`.

Command: `dotnet run --no-build -- qwen2.5-7b 8192`

## Results (model already cached; all runs SUCCESS)

| Run | Load | Chat | Process RSS peak | System used (start to peak) | Swap peak |
|---|---:|---:|---:|---:|---:|
| 1 | 12.2 s | 6.4 s | 2.4 GB | 2.4 to 7.1 GB | 2.75 GB |
| 2 | 12.1 s | 6.7 s | 2.4 GB | 1.7 to 7.2 GB | 2.89 GB |
| 3 | 12.1 s | 7.1 s | 2.8 GB | 1.7 to 7.1 GB | 2.69 GB |
| 4 | 11.4 s | 6.3 s | 3.2 GB | 1.7 to 7.2 GB | 2.51 GB |

Swap was at 1.4 GB before the runs. An earlier uncached first run measured
load 13.2 s and chat 11.4 s (cold start outlier); download took 58.7 s.

## Comparison with Windows (Snapdragon X2 Elite, 32 GB)

| | Mac | Windows |
|---|---:|---:|
| Load | about 12 s | 6.8 s |
| Chat | about 6.3 to 7.1 s | 4.2 s |
| Result | SUCCESS | SUCCESS |

Both returned the same `<tool_call>` getWeather/Seattle response.

## Notes

- System used climbs to about 7.1 GB of 8 GB and swap grows by 1.1 to 1.5 GB per
  run, so the model fits but only just. Some of the slowdown is likely swapping.
- Process RSS excludes GPU buffers and understates the model footprint; system
  used is the better indicator. Sampling is machine-wide every 0.5 s, so other
  apps add noise.
- The console does not say whether the structured `tool_calls` path or the
  fallback parser produced the success on the Mac; this was not verified.
- Small sample: 4 runs plus 1 earlier cached run on one Mac and one Windows run.
- `bench.sh` produced the table. The first version printed swap 100x too large;
  the table above is corrected and the script is fixed.
