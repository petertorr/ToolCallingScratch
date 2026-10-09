# Qwen2.5-7b WebGPU tool-calling smoke test

Recorded run: October 9, 2026. This reports the existing single-model run,
not a new run or the full GPU suite.

## Environment

| Item | Value |
|---|---|
| Machine | Surface Laptop for Business 13.8in 8th Ed Snapdragon |
| Processor | Snapdragon X2 Elite @ 4.03 GHz |
| GPU | Qualcomm Adreno X2-85 |
| RAM | 32 GB class (33,822,822,400 bytes reported by Windows) |
| OS | Windows 11 Pro Insider Preview, 10.0.28120 |
| Process architecture | ARM64 |
| Foundry SDK | Microsoft.AI.Foundry.Local.WinML 1.2.4 |
| Model alias | qwen2.5-7b |
| Model full name | qwen2.5-7b-instruct-generic-gpu |
| Reported model size | 5,324 MB |
| Execution provider | WebGpuExecutionProvider |

The console did not print the model variant version, quantization, or Foundry
runtime version; these are not inferred here.

## Command and workload

From the `gpu-tool-calling` branch:

```powershell
dotnet run --project ToolCallingScratch.csproj --no-build --no-restore -r win-arm64 -- qwen2.5-7b 8192
```

System prompt:

> You provide weather forecast information using a tool. Use tools through the structured tool_calls API only. Do not write fake JSON or XML tool calls as text. When a tool is needed, call it via tool_calls instead of saying you will do it.

User prompt:

> What is the weather in Seattle?

One tool was supplied:

- Name: `getWeather`
- Description: `Returns the weather for a specific city, in degrees celcius.`
- Required parameter: `city`, a string described as `The name of the city to get the weather for.`

Temperature was `0`, tool choice was `Auto`, and the configured chat timeout
was 60 seconds. This tests one response only: it does not execute the weather
tool or send a tool result back for a final answer.

## Observed timings

| Operation | Duration |
|---|---:|
| Create SDK instance | 126 ms |
| Register execution providers | 50 ms |
| Get catalog | 2,848 ms |
| Get model | 3 ms |
| Download model | 82,349 ms |
| Load model | 6,774 ms |
| Get chat client | 15 ms |
| Complete chat request | 4,182 ms |
| Total application time | 96.51 s |

The total includes the model download, setup, inference, and cleanup. The
4,182 ms chat duration measures the complete non-streaming request, not
time to first token. Token counts, tokens/second, and peak memory were not
measured. This was one sample, not an averaged benchmark.

## Actual console response and result

```text
Verbatim response:
---
<tool_call>
{"name": "getWeather", "arguments": {"city": "Seattle"}}
</tool_call>
---
SUCCESS: Correct tool-call syntax.
```

Although the displayed content includes tool-call tags, the harness accepted
the structured response: its success path required `FinishReason == "tool_calls"`
and a `message.ToolCalls` entry for `getWeather` with `city == "Seattle"`.
The fallback parser was retained but **was not used** in this run.

## Comparing with an 8 GB Mac mini M1

Use the same prompts, tool schema, temperature, and automatic tool choice.
Record the Mac model artifact, quantization, runtime, and GPU/backend selection:
the same model family does not guarantee the same weights or inference path.

Compare model-load duration and complete-chat duration separately; do not compare
download-inclusive total time as inference performance. Also distinguish a
native structured tool call from text recovered by a fallback parser.

The Windows machine has substantially more RAM than the 8 GB Mac. Record memory
pressure or swapping on the Mac if observed. No Mac timings or results were
provided for this report, so no performance comparison is claimed.
