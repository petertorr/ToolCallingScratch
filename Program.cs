using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    enum TestGrade
    {
        A = 0,  // Perfect. Requested a tool call with correct payload.
        B = 1,  // Acceptable. Output wasn't a proper tool call, but payload can be used with brute-force parsing hacks.
        // C, unused for single score
        // D, unused for single score
        E = 4,   // Unacceptable. Requested a tool call but the payload is unusable (e.g. bad function name or missing args). 
        F = 5,   // Unacceptable. No discernable attempt to make a tool call, or threw an exception, or timed out.
        Ineligible = 99
    }

    enum AverageTestGrade
    {
        A, B, C, D, E, F,
        Ineligible = 99
    }

    class TestSuiteResults(ModelFilter.MatchInfo filterResult)
    {
        public record class IndividualTestResult(TestGrade Grade, TimeSpan TotalTime);

        public ModelFilter.MatchInfo FilterResult { get; } = filterResult;
        public List<IndividualTestResult> TestResults { get; } = [];
        public List<IndividualTestResult> EligibleTestResults => TestResults.Where(x => x.Grade != TestGrade.Ineligible).ToList();
        public AverageTestGrade AverageGrade => (AverageTestGrade)(int)((float)TestResults.Sum(x => (int)x.Grade) / TestResults.Count + 0.8f); // +0.8f to nudge grade downwards.
        public TimeSpan AverageTime => TimeSpan.FromMilliseconds(TestResults.Sum(x => x.TotalTime.TotalMilliseconds) / TestResults.Count);
    }

    record class ToolCallingTestMetadata(string UserPrompt, string ExpectedFunctionName, Dictionary<string, string> ExpectedArguments);

    internal enum ModelMatchKind
    {
        Matched,
        DidntMatch,
        NotRequested,
        ExplicitlyIgnored
    }

    record class ModelFilter(List<string> SpecificModels, int MaxSizeInMb, List<string> IgnoredModels, bool TryAllVariants, bool IgnoreToolsSupport, List<string> RegisteredEps, bool AllowCpu)
    {
        internal record class MatchInfo(string Name, IModel Model, ModelMatchKind MatchKind, string? ReasonForOmission);
        internal const int DefaultMaxFileSize = 6 * 1_024; // 6 gigs; reasonable default for machines with 16 GB of RAM.

        internal static ModelFilter Default { get; } = new([], DefaultMaxFileSize, [], false, false, [], false);

        internal List<MatchInfo> GetAllMatches(IEnumerable<IModel> models)
        {
            List<MatchInfo> results = [];
            var optionsNoVariants = this with { TryAllVariants = false };

            foreach (var model in models)
            {
                if (TryAllVariants)
                {
                    foreach (var variant in model.Variants)
                    {
                        results.AddRange(optionsNoVariants.GetAllMatches([variant]));
                    }
                }
                else
                {
                    var matchKind = GetMatchResult(model, out var reason);
                    results.Add(new(model.Info.Name, model, matchKind, reason));
                }
            }

            return results;
        }

        internal ModelMatchKind GetMatchResult(IModel model, [NotNullWhen(false)] out string? reasonForOmission)
        {
            reasonForOmission = null;

            if (SpecificModels.Count > 0 && !SpecificModels.Any(name =>
                    model.Alias.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    model.Info.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                reasonForOmission = $"`oModel doesn't match a requested name or alias.";
                return ModelMatchKind.NotRequested;
            }
            if (IgnoredModels.Any(name =>
                model.Alias.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                model.Info.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                reasonForOmission = $"`oModel is explicitly ignored.";
                return ModelMatchKind.ExplicitlyIgnored;
            }
            if (!IgnoreToolsSupport && (!model.Info.SupportsToolCalling.HasValue || model.Info.SupportsToolCalling.Value == false))
            {
                reasonForOmission = $"`oModel doesn't support tool-calling.";
                return ModelMatchKind.DidntMatch;
            }
            if (model.Info.FileSizeMb > MaxSizeInMb)
            {
                reasonForOmission = $"`oModel is too large ({model.Info.FileSizeMb:N0} MB).";
                return ModelMatchKind.DidntMatch;
            }
            if (model.Info.Runtime!.DeviceType is DeviceType.CPU && !AllowCpu)
            {
                reasonForOmission = $"`oModel only runs on the CPU.";
                return ModelMatchKind.DidntMatch;
            }
            if (RegisteredEps.Count > 0 && !RegisteredEps.Contains(model.Info.Runtime!.ExecutionProvider))
            {
                reasonForOmission = $"`oModel is not supported by any registered EP.";
                return ModelMatchKind.DidntMatch;
            }

            return ModelMatchKind.Matched;
        }
    }

    enum ProgramAction
    {
        List,
        Run,
        Load,
        Help,
        Error
    }

    record class Options(ProgramAction Action, ModelFilter Filter, bool RequireToolUse, bool BruteForce, bool ShowVerbatimResponses,
        bool QuietMode, int TimeoutInSeconds)
    {
        internal const int DefaultTimeoutInSeconds = 60 * 1; // Time to wait for an answer. It's a trivial prompt...

        static Options MakeDefault(ProgramAction action) => new(action, ModelFilter.Default, false,
            false, false, false, DefaultTimeoutInSeconds);

        internal static Options DefaultBeforeParsing { get; } = MakeDefault(ProgramAction.Help);

        internal static Options Parse(string[] args, out List<string> ignoredArgs)
        {
            int index = 0;
            ignoredArgs = [];
            List<string> localIgnoredArgs = [];
            bool allowCpu = false;
            bool requireToolUse = false;
            bool tryAllVariants = false;
            bool bruteForce = false;
            bool showVerbatimResponses = false;
            bool quietMode = false;
            bool ignoreToolSupport = false;
            int timeout = DefaultTimeoutInSeconds;
            int maxSize = ModelFilter.DefaultMaxFileSize;
            List<string> requestedModels = [];
            List<string> ignoredModels = [];
            List<string> registeredEps = []; // will be filled out after loading.
            ProgramAction action = ProgramAction.Help;

            // Specifying nothing is the same as 'run' with no other args
            if (args.Length == 0)
            {
                return MakeDefault(ProgramAction.Run);
            }
            if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                action = ProgramAction.List;
            }
            else if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
            {
                action = ProgramAction.Run;
            }
            else if (args[0].Equals("load", StringComparison.OrdinalIgnoreCase))
            {
                action = ProgramAction.Load;
            }
            else
            {
                Program.WriteLine($"{Environment.NewLine}`wApp mode '`o{args[0]}`w' not recognized; showing help.", true);
                return MakeDefault(ProgramAction.Help);
            }

            bool parseSuccess = true;

            // Start at 1, not 0, since we got the action from args[0]
            for (index = 1; index < args.Length; ++index)
            {
                switch (args[index].ToLowerInvariant())
                {
                    case "-v":
                        tryAllVariants = true;
                        break;

                    case "-c":
                        allowCpu = true;
                        break;

                    case "-f":
                        requireToolUse = true;
                        break;

                    case "-b":
                        bruteForce = true;
                        break;

                    case "-r":
                        showVerbatimResponses = true;
                        break;

                    case "-a":
                        ignoreToolSupport = true;
                        break;

                    case "-q":
                        quietMode = true;
                        break;

                    case "-t":
                        parseSuccess = ParseArgParam("-t", s => timeout = int.Parse(s), "a time in seconds");
                        break;

                    case "-m":
                        parseSuccess = ParseArgParam("-m", requestedModels.Add, "a model alias");
                        break;

                    case "-s":
                        parseSuccess = ParseArgParam("-s", s => maxSize = int.Parse(s), "a file size in MB");
                        break;

                    case "-i":
                        parseSuccess = ParseArgParam("-i", ignoredModels.Add, "a model name or alias to ignore");
                        break;

                    default:
                        localIgnoredArgs.Add(args[index]);
                        break;
                }

                if (!parseSuccess)
                {
                    action = ProgramAction.Error;
                    break;
                }
            }

            ignoredArgs = localIgnoredArgs;
            return new(action, new(requestedModels, maxSize, ignoredModels, tryAllVariants, ignoreToolSupport, registeredEps, allowCpu),
                requireToolUse,
                bruteForce, showVerbatimResponses, quietMode, timeout);

            bool ParseArgParam(string param, Action<string> callback, string description)
            {
                if (index < args.Length - 1)
                {
                    ++index;
                    callback(args[index]);
                    return true;
                }

                Program.WriteLine($"`r{param} option needs {description}.", true);
                localIgnoredArgs.Add(args[index]);
                return false;
            }
        }
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

        static readonly ToolCallingTestMetadata WeatherTest = new("What is the weather in Seattle?", "getWeather", new()
        {
            ["city"] = "Seattle"
        });

        static readonly ToolCallingTestMetadata CurrencyTest = new("What is 5 USD converted to AUD?", "convertCurrency", new()
        {
            ["amount"] = "5",
            ["from"] = "USD",
            ["to"] = "AUD"
        });

        static readonly ToolCallingTestMetadata FileWriteTest = new("Write the string 'hello world' into the file named testing.txt. If the file already exists, overwrite it", "writeFile", new()
        {
            ["filename"] = "testing.txt",
            ["contents"] = "hello world",
            ["overwrite"] = "True" // upper-case 'T' because we're ToString()-ing the JsonElement.
        });

        static readonly List<ToolCallingTestMetadata> AllTests = [WeatherTest, CurrencyTest, FileWriteTest];

        const int maxErrorMessageLength = 500; // Truncate error messages longer than this.

        static Options options = Options.DefaultBeforeParsing;

        static readonly string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;

        static async Task<int> Main(string[] args)
        {
            NativeExceptionHelper.SetupExceptionHandling();

            WriteLine($"`w{exeName} ({RuntimeInformation.ProcessArchitecture})", true);

            options = Options.Parse(args, out var ignoredArgs);

            if (options.Action is ProgramAction.Help)
            {
                Usage();
                return (int)ExitCode.Other;
            }
            if (options.Action is ProgramAction.Error)
            {
                WriteLine($"Use `y{exeName} help`x for help.", true);
                return (int)ExitCode.Other;
            }

            if (options.QuietMode)
            {
                WriteLine($"`yQuiet Mode; app may appear to hang for several minutes...", true);
            }

            if (ignoredArgs.Count > 0)
            {
                WriteLine($"`oIgnoring unknown arguments {string.Join(", ", ignoredArgs)}", true);
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await TimeActionAsync(() => FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance), "Creating FoundryLocal instance");
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
                    WriteLine($"`rFailed EPs: {string.Join(", ", eps.FailedEps)}.");
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
                WriteLine($"`r{Environment.NewLine}ERROR: {ex.Message}", true);
                Usage();
                return (int)ExitCode.Other;
            }
            finally
            {
                WriteLine($"`d[Total time: {sw.Elapsed.TotalSeconds:N2} seconds]", true);
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
                   `c-c`x             `xEnable CPU-based models, if applicable for your hardware.
                   `c-v`x             `xInclude all variants for the selected model(s).
                   `c-a`x             `xTry all applicable models, even if they don't claim to support tool-calling.
                
                   `c-f`x             `xForce tool calling (vs. leaving it as "auto").
                   `c-b`x             `xBrute-force tool-call attempts using basic pattern matching. Useful for some models.
                   `c-t`b <time>      `xTime, in seconds, to wait for the model to respond. Default is {Options.DefaultTimeoutInSeconds}.
                
                   `c-r`x             `xShow verbatim responses from the models (sometimes good for a laugh).
                   `c-q`x             `xQuiet mode; suppresses most of the app's output. May appear to hang while it's working.
                
                `wGrades
                `w======
                   {GetGradeInColor(AverageTestGrade.A)} - Correct tool call; the only `wreal`x passing grade.
                   {GetGradeInColor(AverageTestGrade.B)} - No explicit tool call, but can be brute-forced (requires `c-b`x option).
                   {GetGradeInColor(AverageTestGrade.C)} - [Only used as an average; not an actual grade.]
                   {GetGradeInColor(AverageTestGrade.D)} - [Only used as an average; not an actual grade.]
                   {GetGradeInColor(AverageTestGrade.E)} - Attempt to make a tool call, but the name or parameters are incorrect.
                   {GetGradeInColor(AverageTestGrade.F)} - No attempt to make a tool call, and couldn't brute-force it either.
                                                        
                `wExit code is 0 for success, > 0 for an error.
                """, true);
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
                    WriteLine($"`w{filteredResult.Model.Alias,30} ({GetDeviceTypeInColor(model)}) `y{model.Info.Runtime!.ExecutionProvider,-25} `d[{model.Info.Name}]", true);
                    ++total;
                }

                return Task.CompletedTask;
            });

            WriteLine($"`y{$"Total models: {total}",30}");
            return total > 0 ? ExitCode.Success : ExitCode.Fail;
        }

        static async Task<TestSuiteResults> RunOneModelVariantAsync(ModelFilter.MatchInfo filterResult, bool runPrompts)
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
                NativeExceptionHelper.CurrentModelName = model.Info.Name;
                NativeExceptionHelper.IsLoadingModel = true;
                await TimeActionAsync(() => model.LoadAsync(), "Loading model");
                NativeExceptionHelper.IsLoadingModel = false;

                if (!runPrompts)
                {
                    combinedResult.TestResults.Add(new(TestGrade.A, TimeSpan.FromSeconds(0)));
                    return combinedResult;
                }
                var client = await TimeActionAsync(() => model.GetChatClientAsync(), "Getting chat client");
                client.Settings.Temperature = 0f;
                client.Settings.ToolChoice = options.RequireToolUse ? ToolChoice.Required : ToolChoice.Auto;
                foreach (var test in AllTests)
                {
                    var execResult = await TimeActionAsync(() => ExecutePromptAsync(client, test));
                    combinedResult.TestResults.Add(new(execResult.Value, execResult.Elapsed));
                }
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: Cannot load model {filterResult.Name}:{Environment.NewLine}{GetExceptionMessage(ex)}", true);
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

        static async Task<ExitCode> RunAsync(ICatalog catalog, bool runPrompts)
        {
            List<TestSuiteResults> results = [];

            if (options.ShowVerbatimResponses && runPrompts)
            {
                WriteLine($"`dSystem prompt: {systemChatMessage.Content}");
            }

            await EnumerateAllCandidateModelsAsync(catalog, async filterResult => results.Add((await RunOneModelVariantAsync(filterResult, runPrompts))));

            int total = 0;
            int ignored = 0;

            WriteLine($"`w{"",30} |  Avg  |    Avg    |        |", true);
            WriteLine($"`w{"Alias",30} | Grade | {"Time (ms)",9} | Device | EP [name] ", true);
            WriteLine($"`w{new string(' ', 23)}{new string('-', 8)}+{new string('-', 7)}+{new string('-', 11)}+{new string('-', 8)}+{new string('-', 12)}", true);
            foreach (var result in results)
            {
                if (result.FilterResult.MatchKind is ModelMatchKind.NotRequested or ModelMatchKind.ExplicitlyIgnored)
                {
                    continue;
                }

                total++;
                if (result.FilterResult.MatchKind == ModelMatchKind.DidntMatch)
                {
                    ignored++;
                    continue;
                }

                var model = result.FilterResult.Model;
                WriteLine($"`w{model.Alias,30}`w |   {GetGradeInColor(result.AverageGrade)}`w   | "
                    + $"{result.AverageTime.TotalMilliseconds,9:N0} |   {GetDeviceTypeInColor(model)}`w  | "
                    + $"`d{model.Info.Runtime!.ExecutionProvider} [{model.Info.Name}]", true);
            }

            var numTested = total - ignored;
            var numPassing = results.Where(x => x.AverageGrade is AverageTestGrade.A or AverageTestGrade.B).Count();
            WriteLine($"""

                `ySummary:

                `w Models tested: {numTested,2}
                `g          Pass: {numPassing,2}
                `r          Fail: {numTested - numPassing,2}
                `xModels ignored: {ignored,2}
                `w==================
                `y         Total: {total,2}
                """);

            return ExitCode.Success;
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
            TestGrade exitCode = TestGrade.F;
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

                if (options.ShowVerbatimResponses)
                {
                    WriteLine($"`aVerbatim response:{Environment.NewLine}---{Environment.NewLine}{message.Content}{Environment.NewLine}---");
                }

                if (reply.FinishReason == "tool_calls")
                {
                    if (IsExpectedFunctionCall(reply.Message.ToolCalls![0].FunctionCall!, test))
                    {
                        WriteLine($"GRADE: {GetGradeInColor(TestGrade.A)}");
                        exitCode = TestGrade.A;
                    }
                    else
                    {
                        WriteLine($"GRADE: {GetGradeInColor(TestGrade.E)}");
                        exitCode = TestGrade.E;
                    }
                }
                // Try to brute-force it
                else if (options.BruteForce &&
                    BruteForceToolCallParser.TryBruteForceFunctionCall(message.Content!, toolDefinitions, out var toolCall)
                    && IsExpectedFunctionCall(toolCall, test))
                {
                    WriteLine($"GRADE: {GetGradeInColor(TestGrade.B)}");
                    exitCode = TestGrade.B;
                }
                else
                {
                    WriteLine($"GRADE: {GetGradeInColor(TestGrade.F)}");
                    exitCode = TestGrade.F;
                }
            }
            // Never happens right now; see https://github.com/microsoft/foundry-local/issues/1193
            catch (TaskCanceledException)
            {
                WriteLine($"`rERROR: Completion for model '{NativeExceptionHelper.CurrentModelName}' took longer than {options.TimeoutInSeconds} seconds to respond.");
                WriteLine($"GRADE: {GetGradeInColor(TestGrade.F)}");
                exitCode = TestGrade.F;
            }
            catch (Exception ex)
            {
                WriteLine($"`rERROR: Exception while running completion for model '{NativeExceptionHelper.CurrentModelName}'.{Environment.NewLine}{GetExceptionMessage(ex)}", true);
                WriteLine($"GRADE: {GetGradeInColor(TestGrade.F)}");
                exitCode = TestGrade.F;
            }

            return exitCode;
        }

        static async Task EnumerateAllCandidateModelsAsync(ICatalog catalog, Func<ModelFilter.MatchInfo, Task> callback)
        {
            var models = await TimeActionAsync(() => catalog.ListModelsAsync(), "Getting list of models");
            await EnumerateAllCandidateModelsAsync(models, callback);
        }

        static async Task EnumerateAllCandidateModelsAsync(IEnumerable<IModel> models, Func<ModelFilter.MatchInfo, Task> callback)
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

        internal static void WriteLine(string content) => WriteLine(content, false);
        internal static void WriteLine(string content, bool bypassQuietMode) => Write(content + Environment.NewLine, bypassQuietMode);

        internal static void Write(string content) => Write(content, false);
        internal static void Write(string content, bool bypassQuietMode)
        {
            if (options.QuietMode && !bypassQuietMode)
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
            Write($"`d{description}...");
            var sw = Stopwatch.StartNew();
            try
            {
                var t = await f();
                return t;
            }
            finally
            {
                sw.Stop();
                WriteLine($"`d it took {sw.ElapsedMilliseconds:N0} ms");
            }
        }

        static async Task<(T Value, TimeSpan Elapsed)> TimeActionAsync<T>(Func<Task<T>> f)
        {
            var sw = Stopwatch.StartNew();
            var t = await f();
            sw.Stop();
            return (t, sw.Elapsed);
        }
    }
}
