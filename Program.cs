using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ToolCallingScratch.CrashDetection;

namespace ToolCallingScratch
{
    enum ExitCode
    {
        Success = 0,    // Everything OK. If checking a single model, it got an average grade of A or B. If checking multiple models, always returns Success.
        Fail = 1,       // Checking a single model and it got an average grade C or below.
        Exception = 2,  // A runtime exception not related to model performance.
        Other = 99      // Something else.
    }

    enum ProgramAction
    {
        List,
        Run,
        Load,
        Help,
        Error
    }

    internal class Program
    {
        static readonly ChatMessage systemChatMessage = new("system",
            """
            You are a helpful assistant that can use tools to answer user queries. All prompts you receive will REQUIRE you
            to make a tool call. Call tools via the structured tool_call API only, DO NOT write fake JSON or XML tool calls as text.
            DO NOT output any additional "thinking" text or explanations. Always make tool calls via the tool_call API instead of 
            saying you will do it. 
            """);

        static readonly List<ToolDefinition> toolDefinitions = [
            ToolDefinition.DefineFunction(
                new FunctionDefinitionBuilder("getWeather", "Returns the weather for a specific city, in degrees celcius.")
                .AddParameter("city", PropertyDefinition.DefineString("The name of the city to get the weather for."), true)
                .Build()
            ),

            ToolDefinition.DefineFunction(
                new FunctionDefinitionBuilder("convertCurrency", "Converts an amount of money from one currency to another.")
                .AddParameter("amount", PropertyDefinition.DefineNumber("The amount of money to convert, in the source currency."), true)
                .AddParameter("from", PropertyDefinition.DefineString("The currency to convert from."), true)
                .AddParameter("to", PropertyDefinition.DefineString("The currency to convert to."), true)
                .Build()
            ),

            ToolDefinition.DefineFunction(
                new FunctionDefinitionBuilder("writeFile", "Writes a string to a named file.")
                .AddParameter("filename", PropertyDefinition.DefineString("The name of the file to write to."), true)
                .AddParameter("contents", PropertyDefinition.DefineString("The contents to write into the file."), true)
                .AddParameter("overwrite", PropertyDefinition.DefineBoolean("Whether or not to overwrite an existing file."), true)
                .Build()
            ),
        ];

        static readonly List<ToolCallingTestMetadata> testMetadata =
        [
            new("What is the weather in Seattle?", "getWeather", new()
                {
                    ["city"] = "Seattle"
                }
            ),
            new("What is 5 USD converted to AUD?", "convertCurrency", new()
                {
                    ["amount"] = "5",
                    ["from"] = "USD",
                    ["to"] = "AUD"
                }
            ),
            new("Write the string 'hello world' into the file named testing.txt. If the file already exists, overwrite it", "writeFile", new()
                {
                    ["filename"] = "testing.txt",
                    ["contents"] = "hello world",
                    ["overwrite"] = "True" // upper-case 'T' because we're ToString()-ing the JsonElement.
                }
            )
        ];

        const int maxErrorMessageLength = 500; // Truncate error messages longer than this.

        static Options options = Options.DefaultBeforeParsing;

        static readonly string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;

        // Stash the current name so we can show it to the user in an error message without passing it around.
        internal static string? CurrentModelName { get; set; }

        // The 'unhandled native exception' crash only happens during mode loading.
        internal static bool IsLoadingModel { get; set; } = false;

        const string AppNameForFoundryLocal = "ToolCallingScratch";

        static async Task<int> Main(string[] args)
        {
            NativeExceptionHelper.SetupExceptionHandling();

            WriteLine($"`w{exeName} ({RuntimeInformation.ProcessArchitecture})", OutputModes.Status);

            options = Options.Parse(args, out var ignoredArgs);

            if (options.Action is ProgramAction.Help)
            {
                Usage();
                return (int)ExitCode.Other;
            }
            if (options.Action is ProgramAction.Error)
            {
                WriteLine($"Use `y{exeName} help`x for help.", OutputModes.ProgramOutput);
                return (int)ExitCode.Other;
            }

            WriteLine($"`oNote: This app caches a lot of data in {Environment.ExpandEnvironmentVariables(
                $@"%userprofile%\.{AppNameForFoundryLocal}\cache\models")}. Consider deleting the directory when you're done.", OutputModes.ImportantInfo);

            if (options.OutputModes == OutputModes.Default)
            {
                WriteLine($"`yShowing (almost) all output; use `c-h`x, `c-q`x, or `c-z`w to minimize output.", OutputModes.ProgramOutput);
            }
            else if ((options.OutputModes & OutputModes.Timings) == 0)
            {
                WriteLine($"`ySuppressing some output; app may appear to hang for several minutes at a time...", OutputModes.ImportantInfo);
            }
            if (ignoredArgs.Count > 0)
            {
                WriteLine($"`oIgnoring unknown arguments {string.Join(", ", ignoredArgs)}", OutputModes.Status);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await TimeActionAsync(() => FoundryLocalManager.CreateAsync(new() { AppName = AppNameForFoundryLocal }, NullLogger.Instance), "Creating FoundryLocal instance");
                using var instance = FoundryLocalManager.Instance;
                var eps = await TimeActionAsync(() => instance.DownloadAndRegisterEpsAsync(), "Registering EPs");
                options.Filter!.RegisteredEps.AddRange(eps.RegisteredEps);
                var catalog = await TimeActionAsync(() => instance.GetCatalogAsync(), "Getting model catalog");
                WriteLine($"""
                    `wRegistered EPs: `x{string.Join(", ", eps.RegisteredEps)}.
                    `wChosen model(s): `x{(options.Filter.SpecificModels.Count > 0 ? string.Join(", ", options.Filter.SpecificModels) : "`yall")}{(options.Filter.TryAllVariants ? " `y(with variants)" : "")}.
                    `wTimeout (seconds): `x{options.TimeoutInSeconds}.
                    `wForcing tool usage: `x{options.RequireToolUse}.
                    `wMax model size: `x{options.Filter.MaxSizeInMb}.
                    """);
                if (options.Filter.IgnoredModels.Count > 0)
                {
                    WriteLine($"`oIgnoring model(s): {string.Join(", ", options.Filter.IgnoredModels)}.");
                }
                if (eps.FailedEps.Length > 0)
                {
                    WriteLine($"`rFailed EPs: {string.Join(", ", eps.FailedEps)}.", OutputModes.Errors);
                }

                if (options.Action == ProgramAction.List)
                {
                    return (int)await ListAsync(catalog);
                }
                if (options.Action == ProgramAction.Run)
                {
                    return (int)await RunAsync(catalog, true);
                }
                if (options.Action == ProgramAction.Load)
                {
                    return (int)await RunAsync(catalog, false);
                }

                throw (new NotImplementedException("Don't know what to do"));
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}{Environment.NewLine}ERROR: `a{ex.Message}", OutputModes.Errors);
                Usage();
                return (int)ExitCode.Other;
            }
            finally
            {
                WriteLine($"`d[Total time: {sw.Elapsed.TotalSeconds:N2} seconds]", OutputModes.Status);
            }
        }

        static void Usage()
        {
            WriteLine($"""
                
                `w{exeName}`x - Checks Foundry Local models for successful tool-calling ability.

                `wUsage
                `w=====
                   `w{exeName}`x `x     `x          `x- Same as '`y{exeName} run`x'.
                   `w{exeName}`x `ylist `c[options] `x- lists models that match given filters.
                   `w{exeName}`x `yrun `c[options]  `x- runs test for models that match the given filters.
                   `w{exeName}`x `yload `c[options] `x- loads models that match given filters, but doesn't run tests.
                   `w{exeName}`x `yhelp `x          `x- this text.

                `wOptions (all are optional)

                   `c-m`b <model>     `xName or alias of a model to select (substring match). Can be specified more than once.
                   `x  `x             `xIf choosing a specific name (not alias), you must include the '-v' option.
                   `c-s`b <fileSize>  `xMaximum model file size allowed, in MB. Default is {ModelFilter.DefaultMaxFileSize}.
                   `c-i`b <model>     `xName or alias of a model to ignore (substring match). Can be specified more than once.
                   `c-k`b <model>     `xSkip past all models, including this one, before starting. Useful if a previous run crashed.
                   `c-c`x             `xEnable CPU-based models, if applicable for your hardware.
                   `c-v`x             `xInclude all variants for the selected model(s).
                   `c-a`x             `xTry all applicable models, even if they don't claim to support tool-calling.
                   `c-f`x             `xForce tool calling (vs. leaving it as "auto").
                   `c-b`x             `xBrute-force tool-call attempts using basic pattern matching. Useful for some models.
                   `c-t`b <time>      `xTime, in seconds, to wait for the model to respond. Default is {Options.DefaultTimeoutInSeconds}.
                
                   `c-h`x             `xHide most timing info.
                   `c-p`x             `xProgressive mode; shows results progressively. Implies `c-z`x (no other output).
                   `c-r`x             `xShow verbatim responses from the models (sometimes good for a laugh).
                   `c-q`x             `xQuiet mode; suppresses most app output except errors and results. May appear to hang while it's working.
                   `c-z`x             `xSuper quiet mode; only shows program output and fatal errors. May appear to hang while it's working.
                
                `wGrades
                `w======
                   {GetGradeInColor(AverageTestGrade.A)} - Correct tool call; the only `wreal`x passing grade.
                   {GetGradeInColor(AverageTestGrade.B)} - No explicit tool call, but can be brute-forced (requires `c-b`x option).
                   {GetGradeInColor(AverageTestGrade.C)} - [Only used as an average; not an actual grade.]
                   {GetGradeInColor(AverageTestGrade.D)} - [Only used as an average; not an actual grade.]
                   {GetGradeInColor(AverageTestGrade.E)} - Attempt to make a tool call, but the name or parameters are incorrect.
                   {GetGradeInColor(AverageTestGrade.F)} - No attempt to make a tool call, and couldn't brute-force it either.
                                                        
                `wExit code is 0 for success, > 0 for an error.
                """, OutputModes.ProgramOutput);
        }

        static async Task<ExitCode> ListAsync(ICatalog catalog)
        {
            int total = 0;
            WriteLine($"`yListing models for this device that match your criteria.{Environment.NewLine}");
            await EnumerateAllCandidateModelsAsync(catalog, filteredResult =>
            {
                if (filteredResult.MatchKind == ModelMatchKind.Matched)
                {
                    var model = filteredResult.Model;
                    WriteLine($"`w{filteredResult.Model.Alias,30} ({GetDeviceTypeInColor(model)}) "
                        + $"`y{model.Info.Runtime!.ExecutionProvider,-25} `d[{model.Info.Name}]", OutputModes.ProgramOutput);

                    ++total;
                }

                return Task.CompletedTask;
            });

            WriteLine($"`y{$"Total models: {total}",30}");
            return total > 0 ? ExitCode.Success : ExitCode.Fail;
        }

        static async Task<ExitCode> RunAsync(ICatalog catalog, bool runPrompts)
        {
            List<TestSuiteResults> results = [];

            if (runPrompts)
            {
                WriteLine($"`dSystem prompt: {systemChatMessage.Content}", OutputModes.VerbatimOutput);
            }

            if (options.ProgressiveOutput)
            {
                WriteHeader();
            }

            int total = 0;
            int ignored = 0;
            int noMatch = 0;

            await EnumerateAllCandidateModelsAsync(catalog, async filterResult =>
            {
                var result = await RunOneModelVariantAsync(filterResult, runPrompts);
                if (options.ProgressiveOutput)
                {
                    HandleOneResult(result);
                }
                else
                {
                    results.Add(result);
                }
            });

            if (!options.ProgressiveOutput)
            {
                WriteHeader();

                foreach (var result in results)
                {
                    HandleOneResult(result);
                }
            }

            var numSelected = total - ignored;
            var numTested = numSelected - noMatch;
            var numPassing = results.Count(x => x.AverageGrade is AverageTestGrade.A or AverageTestGrade.B);
            
            WriteLine($"""

                `ySummary:

                `x     Models ignored: {ignored,2}
                `xModels filtered out: {noMatch,2}
                `w      Models tested: {numTested,2}
                `g               Pass: {numPassing,2}
                `r               Fail: {numTested - numPassing,2}
                `w=======================
                `y              Total: {total,2}
                """, OutputModes.ImportantInfo);

            return ExitCode.Success;

            void WriteHeader()
            {
                WriteLine($"`w{"",30} |  Avg  |    Avg    |        |", OutputModes.ProgramOutput);
                WriteLine($"`w{"Alias",30} | Grade | {"Time (ms)",9} | Device | EP [name] ", OutputModes.ProgramOutput);
                WriteLine($"`w{new string(' ', 23)}{new string('-', 8)}+{new string('-', 7)}+{new string('-', 11)}+{new string('-', 8)}+{new string('-', 12)}", OutputModes.ProgramOutput);
            }

            void HandleOneResult(TestSuiteResults result)
            {
                total++;

                if (result.FilterResult.MatchKind is ModelMatchKind.NotRequested or ModelMatchKind.ExplicitlyIgnored)
                {
                    ignored++;
                    return;
                }

                if (result.FilterResult.MatchKind == ModelMatchKind.DidntMatch)
                {
                    noMatch++;
                    return;
                }

                var model = result.FilterResult.Model;
                WriteLine($"`w{model.Alias,30}`w |   {GetGradeInColor(result.AverageGrade)}`w   | "
                    + $"{result.AverageTime.TotalMilliseconds,9:N0} |   {GetDeviceTypeInColor(model)}`w  | "
                    + $"`d{model.Info.Runtime!.ExecutionProvider} [{model.Info.Name}]", OutputModes.ProgramOutput);
            }
        }

        static async Task<TestSuiteResults> RunOneModelVariantAsync(MatchInfo filterResult, bool runPrompts)
        {
            var combinedResult = new TestSuiteResults(filterResult);
            if (filterResult.MatchKind != ModelMatchKind.Matched)
            {
                if (filterResult.MatchKind is ModelMatchKind.DidntMatch or ModelMatchKind.ExplicitlyIgnored)
                {
                    WriteLine($"`cIgnoring {filterResult.Name}: {filterResult.ReasonForOmission}");
                }
                combinedResult.TestResults.Add(new(TestGrade.Ineligible, TimeSpan.FromSeconds(0)));
                return combinedResult;
            }

            var model = filterResult.Model;
            ShowModelInfo(model);
            try
            {
                await TimeActionAsync(() => model.DownloadAsync(), "Downloading model");
                CurrentModelName = model.Info.Name;
                IsLoadingModel = true;
                await TimeActionAsync(() => model.LoadAsync(), "Loading model");
                IsLoadingModel = false;

                if (!runPrompts)
                {
                    combinedResult.TestResults.Add(new(TestGrade.A, TimeSpan.FromSeconds(0)));
                    return combinedResult;
                }
                var client = await TimeActionAsync(() => model.GetChatClientAsync(), "Getting chat client");
                client.Settings.Temperature = 0f;
                client.Settings.ToolChoice = options.RequireToolUse ? ToolChoice.Required : ToolChoice.Auto;
                foreach (var test in testMetadata)
                {
                    var execResult = await TimeActionAsync(() => ExecutePromptAsync(client, test));
                    combinedResult.TestResults.Add(new(execResult.Value, execResult.Elapsed));
                }
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}{Environment.NewLine}ERROR: `aCannot load model {filterResult.Name}:{Environment.NewLine}{GetExceptionMessage(ex)}", OutputModes.Errors);
                combinedResult.TestResults.Add(new(TestGrade.F, TimeSpan.FromSeconds(0)));
            }
            finally
            {
                await model.UnloadAsync();
            }

            WriteLine($"""
                `w
                
                Combined Results for {model.Info.Name}:
                =================================
                `wBest grade:    {GetGradeInColor(combinedResult.TestResults.Min(x => x.Grade))}.
                `wAverage grade: {GetGradeInColor(combinedResult.AverageGrade)}.
                `wBest time:     `x{combinedResult.TestResults.Min(x => x.TotalTime.TotalMilliseconds):N0}ms.
                `wAverage time:  `x{combinedResult.AverageTime.TotalMilliseconds:N0}ms.

                """);

            return combinedResult;
        }

        static bool IsExpectedFunctionCall(FunctionCall call, ToolCallingTestMetadata test)
        {
            if (call.Name != test.ExpectedFunctionName)
            {
                return false;
            }

            var funcArgs = call.ParseArguments();

            if (funcArgs.Count < test.ExpectedArguments.Count)
            {
                return false;
            }

            foreach (var arg in test.ExpectedArguments)
            {
                if (!funcArgs.TryGetValue(arg.Key, out var value)
                    || !Equals(arg.Value.ToString(), value.ToString()))
                {
                    return false;
                }
            }

            return true;
        }

        static async Task<TestGrade> ExecutePromptAsync(OpenAIChatClient client, ToolCallingTestMetadata test)
        {
            TestGrade grade = TestGrade.F;
            List<ChatMessage> messages = [systemChatMessage];
            messages.Add(new("user", test.UserPrompt));

            try
            {
                bool didComplete = false;
                var cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutInSeconds)).Token;
                cancellationToken.Register(x =>
                {
                    if (!didComplete)
                    {
                        WriteLine($"`r[TIMEOUT EXCEEDED].");
                    }
                }, null, false);

                var response = await TimeActionAsync(() => client.CompleteChatAsync(
                    messages, toolDefinitions, cancellationToken),
                    $"Sending prompt '{test.UserPrompt}'");

                didComplete = true;
                var reply = response.Choices[0]!;
                var message = reply.Message;
                var content = message.Content!;

                WriteLine($"`dVerbatim response:{Environment.NewLine}---{Environment.NewLine}{message.Content}{Environment.NewLine}---",
                    OutputModes.VerbatimOutput);

                if (reply.FinishReason == "tool_calls")
                {
                    if (IsExpectedFunctionCall(reply.Message.ToolCalls![0].FunctionCall!, test))
                    {
                        grade = TestGrade.A;
                    }
                    else
                    {
                        grade = TestGrade.E;
                    }
                }
                // Try to brute-force it
                else if (options.BruteForce &&
                    BruteForceToolCallParser.TryBruteForceFunctionCall(message.Content!, toolDefinitions, out var toolCall)
                    && IsExpectedFunctionCall(toolCall, test))
                {
                    grade = TestGrade.B;
                }
                else
                {
                    grade = TestGrade.F;
                }
            }
            // Never happens right now; see https://github.com/microsoft/foundry-local/issues/1193
            catch (TaskCanceledException)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: `aCompletion for model '{CurrentModelName}' took longer than {options.TimeoutInSeconds} seconds to respond.", OutputModes.Errors);
                grade = TestGrade.F;
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: `aException while running completion for model '{CurrentModelName}'.{Environment.NewLine}{GetExceptionMessage(ex)}", OutputModes.Errors);
                grade = TestGrade.F;
            }

            WriteLine($"Test: `w{test.ExpectedFunctionName}`x. Grade: {GetGradeInColor(grade)}", OutputModes.IntermediateResults);
            return grade;
        }

        static async Task EnumerateAllCandidateModelsAsync(ICatalog catalog, Func<MatchInfo, Task> callback)
        {
            var models = await TimeActionAsync(() => catalog.ListModelsAsync(), "Getting list of models");
            await EnumerateAllCandidateModelsAsync(models, callback);
        }

        static async Task EnumerateAllCandidateModelsAsync(IEnumerable<IModel> models, Func<MatchInfo, Task> callback)
        {
            var filtered = options.Filter.GetAllMatches(models);

            foreach (var filteredResult in filtered)
            {
                await callback(filteredResult);
            }
        }

        static string GetDeviceTypeInColor(IModel model)
        {
            return model.Info.Runtime!.DeviceType switch
            {
                DeviceType.GPU => "`gGPU`x",
                DeviceType.NPU => "`mNPU`x",
                DeviceType.CPU => "`dCPU`x",
                _ => "`rInvalid`x"
            };
        }
        static string GetGradeInColor(AverageTestGrade grade) => GetGradeInColor((int)grade);
        static string GetGradeInColor(TestGrade grade) => GetGradeInColor((int)grade);
        static string GetGradeInColor(int value)
        {
            return value switch
            {
                0 => "`gA`x",
                1 => "`yB`x",
                2 => "`oC`x",
                3 => "`rD`x",
                4 => "`rE`x",
                5 => "`rF`x",
                _ => "`b-`x"
            };
        }

        static void ShowModelInfo(IModel model)
        {
            WriteLine($"""
                `yAlias: `x{model.Alias}, `wDevice: {GetDeviceTypeInColor(model)}, `wSize: `x{model.Info.FileSizeMb:N0} MB, `wFull Name: `x{model.Info.Name}, `wEP: `x{model.Info.Runtime!.ExecutionProvider}
                """);
        }

        internal static void WriteLine(string content) => WriteLine(content, OutputModes.Status);
        internal static void WriteLine(string content, OutputModes mode) => Write(content + Environment.NewLine, mode);

        internal static void Write(string content) => Write(content, OutputModes.Status);
        internal static void Write(string content, OutputModes mode)
        {
            if (!options.OutputModes.HasFlag(mode))
            {
                return;
            }

            int totalLength = content.Length;
            for (int i = 0; i < content.Length; ++i)
            {
                var c = content[i];
                if (c != '`' || i == (totalLength - 1))
                {
                    Console.Write(c);
                    continue;
                }

                // Check for markdown '```' and ignore it (models often return this)
                if (i < (totalLength - 2) && content[i + 1] == '`' && content[i + 2] == '`')
                {
                    Console.Write("```");
                    i += 2;
                    continue;
                }

                // must be ` followed by a char
                var color = content[++i] switch
                {
                    'r' => ConsoleColor.Red,
                    'g' => ConsoleColor.Green,
                    'b' => ConsoleColor.Blue,
                    'y' => ConsoleColor.Yellow,
                    'n' => ConsoleColor.DarkGreen,
                    'o' => ConsoleColor.DarkYellow,
                    'a' => ConsoleColor.DarkRed,
                    'm' => ConsoleColor.Magenta,
                    'c' => ConsoleColor.Cyan,
                    'd' => ConsoleColor.DarkGray,
                    'w' => ConsoleColor.White,
                    _ => ConsoleColor.Gray
                };

                Console.ForegroundColor = color;
            }

            Console.ResetColor();
        }

        // ONNX tends to put the entire stack track and / or a lot of context into the "Message".
        // It also doesn't always use Environment.Newline for newlines.
        static string GetExceptionMessage(Exception ex)
        {
            var message = ex.Message;
            var newline = Math.Min(message.IndexOf('\r'), message.IndexOf('\n'));
            if (newline > 0)
            {
                message = message[..newline];
            }
            if (message.Length > maxErrorMessageLength)
            {
                message = message[..(maxErrorMessageLength - 3)] + "...";
            }

            return message;
        }

        static async Task TimeActionAsync(Func<Task> f, string description)
        {
            await TimeActionAsync(async Task<bool> () => { await f(); return true; }, description);
        }

        static async Task<T> TimeActionAsync<T>(Func<Task<T>> f, string description)
        {
            Write($"`d{description}... ", OutputModes.Timings);

            var sw = Stopwatch.StartNew();
            try
            {
                var t = await f();
                return t;
            }
            finally
            {
                sw.Stop();
                WriteLine($"`d it took {sw.ElapsedMilliseconds:N0} ms", OutputModes.Timings);
            }
        }

        static async Task<TimeActionResult<T>> TimeActionAsync<T>(Func<Task<T>> f)
        {
            var sw = Stopwatch.StartNew();
            var t = await f();
            sw.Stop();
            return new(t, sw.Elapsed);
        }
    }

    [Flags]
    enum OutputModes
    {
        None = 0,
        ProgramOutput = 1 << 0,
        ImportantInfo= 1 << 1, // Shown for Quiet but not SuperQuiet
        FatalError = 1 << 2,
        Errors = 1 << 3,
        Timings = 1 << 4,
        VerbatimOutput = 1 << 5,
        Status = 1 << 6,
        IntermediateResults = 1 << 7,
        All = 0xFF,

        // Combos
        Default = All ^ VerbatimOutput,
        Quiet = ProgramOutput | ImportantInfo | Errors | FatalError,
        SuperQuiet = ProgramOutput | FatalError,
    }

    record class TimeActionResult<T>(T Value, TimeSpan Elapsed);
}
