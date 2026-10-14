# ToolCallingScratch

This program is used for testing the quality of Foundry Local AI models. I wrote it after becoming
increasingly frustrated by the issues I was seeing with the models.

Its purpose is to load one or more Foundry Local models and check for successful answers to a set of
prompts that require tool-calling. Each prompt response is graded from **A** to **F**, where an **A** 
is the only acceptable grade (from a pure "does the model work as advertised?" sense) but a **B** should
be sufficient for most purposes. Any other grade means the model failed and can't be used reliably.

# Buliding
Open the VS Project, select your architecture and go. Supports Windows, Mac, and Linux (personally I only 
test Windows). 

# Command Line
The tool has built-in help; running `ToolCallingScrath help` (or any invalid command-line) will show the 
available options, and a description of each of the grades. The top-level command-lines are:

* `ToolCallingScratch list` - lists models that meet selection criteria, but doesn't run them.
* `ToolCallingScratch run` - runs the tests on models that meet selection criteria (see next section).
* `ToolCallingScratch load` - loads the models that meet selection criteria, but doesn't run them.
* `ToolCallingScratch help` - shows help.

The `load` command is useful for testing which models are so broken they can't even load on your
machine.

# Running the test
`ToolCallingScrath run` will run each model using the default ("best") execution provider for your device. 
By default it will generate a lot of output, including various timings and intermediate results. You can
suppress most of the output spew with the `-q` ("quite mode") option.

If the run completes without fatal errors, you get a table such as this:

```text
                               |  Avg  |    Avg    |        |
                         Alias | Grade | Time (ms) | Device | EP [name]
                       --------+-------+-----------+--------+------------
                gemma-4-e2b-it |   F   |         0 |   GPU  | CUDAExecutionProvider [gemma-4-e2b-it-cuda-gpu]
  ministral-3-3b-instruct-2512 |   F   |       380 |   GPU  | CUDAExecutionProvider [ministral-3-3b-instruct-2512-cuda-gpu]
            olmo-3-7b-instruct |   F   |     2,572 |   GPU  | CUDAExecutionProvider [olmo-3-7b-instruct-cuda-gpu]
                    phi-4-mini |   F   |     1,409 |   GPU  | CUDAExecutionProvider [Phi-4-mini-instruct-cuda-gpu]
          phi-4-mini-reasoning |   F   |    30,672 |   GPU  | CUDAExecutionProvider [Phi-4-mini-reasoning-cuda-gpu]
                  qwen2.5-0.5b |   A   |       202 |   GPU  | CUDAExecutionProvider [qwen2.5-0.5b-instruct-cuda-gpu]
                  qwen2.5-1.5b |   A   |       318 |   GPU  | CUDAExecutionProvider [qwen2.5-1.5b-instruct-cuda-gpu]
                    qwen2.5-7b |   A   |     2,103 |   GPU  | CUDAExecutionProvider [qwen2.5-7b-instruct-cuda-gpu]
            qwen2.5-coder-0.5b |   F   |       279 |   GPU  | CUDAExecutionProvider [qwen2.5-coder-0.5b-instruct-cuda-gpu]
            qwen2.5-coder-1.5b |   F   |       457 |   GPU  | CUDAExecutionProvider [qwen2.5-coder-1.5b-instruct-cuda-gpu]
              qwen2.5-coder-7b |   F   |     2,027 |   GPU  | CUDAExecutionProvider [qwen2.5-coder-7b-instruct-cuda-gpu]
                    qwen3-0.6b |   F   |     2,956 |   GPU  | CUDAExecutionProvider [qwen3-0.6b-cuda-gpu]
                    qwen3-1.7b |   F   |     2,025 |   GPU  | CUDAExecutionProvider [qwen3-1.7b-cuda-gpu]
                      qwen3-4b |   C   |     2,977 |   GPU  | CUDAExecutionProvider [qwen3-4b-cuda-gpu]
                      qwen3-8b |   C   |    54,249 |   GPU  | CUDAExecutionProvider [qwen3-8b-cuda-gpu]
          qwen3-vl-2b-instruct |   A   |       842 |   GPU  | CUDAExecutionProvider [qwen3-vl-2b-instruct-cuda-gpu]
          qwen3-vl-4b-instruct |   A   |     1,438 |   GPU  | CUDAExecutionProvider [qwen3-vl-4b-instruct-cuda-gpu]
          qwen3-vl-8b-instruct |   A   |    33,713 |   GPU  | CUDAExecutionProvider [qwen3-vl-8b-instruct-cuda-gpu]
                  qwen3.5-0.8b |   F   |        76 |   GPU  | CUDAExecutionProvider [qwen3.5-0.8b-cuda-gpu]
                    qwen3.5-2b |   F   |       226 |   GPU  | CUDAExecutionProvider [qwen3.5-2b-cuda-gpu]
               qwen3.5-2b-text |   F   |        72 |   GPU  | CUDAExecutionProvider [qwen3.5-2b-text-cuda-gpu]
                    qwen3.5-4b |   F   |       208 |   GPU  | CUDAExecutionProvider [qwen3.5-4b-cuda-gpu]
                    smollm3-3b |   F   |        10 |   GPU  | CUDAExecutionProvider [smollm3-3b-cuda-gpu]
```

In this example only **6** out of **23** models that *claimed* to support tool-calling actually called tools 
successfully. If you use the `-b` ("brute-force") command-line option to attempt manual parsing of the model 
output, the number of passing models increases to **12** but still falls far short of all tests.

If you want to see the actual output generated, use the `-r` ("response") command-line option. Phi's "reasoning"
is comedy gold.

## Selecting models to run

Various command-line options modify which models are selected as candidates to run. The most basic ones are `-m`
(select specific model); `-v` (run all variants), `-s` (maximum file size) and `-i` (ignore specific model). 
The command-line help has more details.

## Runtime errors

The tool handles all *catchable* runtime errors from Foundry Local / ONNX (and prints truncated erorr details),
but sometimes ONNX throws unhandled exceptions inside worker threads that cannot be caught. In this case,
the app will print a message *something like* this and terminate:

```text
******************************************************************************
*** Visual C++ Exception '.?AVOnnxRuntimeException@onnxruntime@@' detected ***
******************************************************************************
  Exception Address=7FF98137408A, Code=E06D7363, Flags=81, Params=4.

If the process crashes, add -i qwen3-4b-cuda-gpu to the command-line to ignore this model.
See https://github.com/microsoft/foundry-local/issues/1191 for bug on this issue.
```

Note it tells you which model to ignore for your next run.

### Runtime errors while debugging
If a debugger is attached, the above exception-detection code dosn't execute - .NET isn't happy if you try 
to do things with vectored exceptions while VS is attached, and you get a fatal app exit:

```text
Invalid Program: attempted to call a UnmanagedCallersOnly method from managed code
```

Instead, make sure you debug **Managed and Native** code to get the actual unhandled
native exceptions.

# The prompts and tools

The app asks each model three questions, and each model has three tools available to it (one per
question). As of writing, the `system` prompt is straightforward:

```text
You are a helpful assistant that can use tools to answer user queries. All prompts you receive will REQUIRE you
to make a tool call. Call tools via the structured tool_call API only, DO NOT write fake JSON or XML tool calls as text.
DO NOT output any additional "thinking" text or explanations. Always make tool calls via the tool_call API instead of 
saying you will do it. 
```

The prompts are as follows (each is a separate prompt, with no history) :

```text
What is the weather in Seattle?

What is 5 USD converted to AUD?

Write the string 'hello world' into the file named testing.txt. If the file already exists, overwrite it
```

The tools are described thusly; each prompt has all three tools added to it:

```c#
new FunctionDefinitionBuilder("getWeather", "Returns the weather for a specific city, in degrees celcius.")
.AddParameter("city", PropertyDefinition.DefineString("The name of the city to get the weather for."), true)
.Build()

new FunctionDefinitionBuilder("convertCurrency", "Converts an amount of money from one currency to another.")
.AddParameter("amount", PropertyDefinition.DefineNumber("The amount of money to convert, in the source currency."), true)
.AddParameter("from", PropertyDefinition.DefineString("The currency to convert from."), true)
.AddParameter("to", PropertyDefinition.DefineString("The currency to convert to."), true)
.Build()

new FunctionDefinitionBuilder("writeFile", "Writes a string to a named file.")
.AddParameter("filename", PropertyDefinition.DefineString("The name of the file to write to."), true)
.AddParameter("contents", PropertyDefinition.DefineString("The contents to write into the file."), true)
.AddParameter("overwrite", PropertyDefinition.DefineBoolean("Whether or not to overwrite an existing file."), true)
.Build()
```

Note the tools are never executed (and hence no reply is given to the model). The test is only whether they
correctly *asked* to run the tool, not whether they could do anything with the *result*.

# Thanks

Thanks to [aclinick](https://github.com/aclinick) for his expertise, contributions, and commiserations :).