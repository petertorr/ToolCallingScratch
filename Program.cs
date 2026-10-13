using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

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

    record class IndividualTestResult(TestGrade Grade, TimeSpan TotalTime);
    class ToolCallingResults(ModelFilterResult filterResult)
    {
        public ModelFilterResult FilterResult { get; } = filterResult;
        public List<IndividualTestResult> TestResults { get; } = [];
        public List<IndividualTestResult> EligibleTestResults => TestResults.Where(x => x.Grade != TestGrade.Ineligible).ToList();
        public AverageTestGrade AverageGrade => (AverageTestGrade)(int)((float)TestResults.Sum(x => (int)x.Grade) / TestResults.Count + 0.8f); // +0.8f to nudge grade downwards.
        public TimeSpan AverageTime => TimeSpan.FromMilliseconds(TestResults.Sum(x => x.TotalTime.TotalMilliseconds) / TestResults.Count);
    }

    record class ModelTest(string UserPrompt, string ExpectedFunctionName, Dictionary<string, string> ExpectedArguments);

    enum ModelMatchKind
    {
        Matched,
        DidntMatch,
        NotRequested,
        ExplicitlyIgnored
    }

    record class ModelFilterResult(string Name, IModel Model, ModelMatchKind MatchKind, string? ReasonForOmission);

    record class ModelFilter(List<string> SpecificModels, int MaxSizeInMb, List<string> IgnoredModels, bool TryAllVariants, List<string> RegisteredEps, bool AllowCpu)
    {
        internal const int DefaultMaxFileSize = 6 * 1_024; // 6 gigs; reasonable default for machines with 16 GB of RAM.

        internal static ModelFilter Default { get; } = new([], DefaultMaxFileSize, [], false, [], false);

        internal List<ModelFilterResult> GetAllMatches(IEnumerable<IModel> models)
        {
            List<ModelFilterResult> results = [];
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
            if (!model.Info.SupportsToolCalling.HasValue || model.Info.SupportsToolCalling.Value == false)
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

    internal class Program
    {
        static readonly ChatMessage systemChatMessage = new("system",
            "You are a helpful assistant that can use tools to answer user queries. If you need to call a tool, use the correct JSON format. Do not just say you will use it.");

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
                .AddParameter("filename", PropertyDefinition.DefineString("The name of the file to write to or delete."), true)
                .AddParameter("contents", PropertyDefinition.DefineString("The contents to write into the file."), true)
                .AddParameter("overwrite", PropertyDefinition.DefineBoolean("Whether or not to overwrite an existing file."), true)
                .Build()
            ),
            ];

        static readonly ModelTest WeatherTest = new("What is the weather in Seattle?", "getWeather", new()
        {
            ["city"] = "Seattle"
        });

        static readonly ModelTest CurrencyTest = new("What is 5 USD converted to AUD?", "convertCurrency", new()
        {
            ["amount"] = "5",
            ["from"] = "USD",
            ["to"] = "AUD"
        });

        static readonly ModelTest FileWriteTest = new("Write the string 'hello world' into the file named testing.txt. If the file already exists, overwrite it", "writeFile", new()
        {
            ["filename"] = "testing.txt",
            ["contents"] = "hello world",
            ["overwrite"] = "True" // upper-case 'T' because we're ToString()-ing the JsonElement.
        });

        static readonly List<ModelTest> AllTests = [WeatherTest, CurrencyTest, FileWriteTest];

        const int maxErrorMessageLength = 500; // Truncate error messages longer than this.

        enum ProgramAction
        {
            List,
            Run,
            Help,
            Error
        }

        record class Options(string ExeName, ProgramAction Action, ModelFilter Filter, bool RequireToolUse, bool BruteForce, bool ShowVerbatimResponses, int TimeoutInSeconds)
        {
            internal const int DefaultTimeoutInSeconds = 60 * 1; // Time to wait for an answer. It's a trivial prompt...

            internal static Options Parse(string[] args, out List<string> ignoredArgs)
            {
                int index = 0;
                ignoredArgs = [];
                List<string> localIgnoredArgs = [];
                var exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
                bool allowCpu = false;
                bool requireToolUse = false;
                bool tryAllVariants = false;
                bool bruteForce = false;
                bool showVerbatimResponses = false;
                int timeout = DefaultTimeoutInSeconds;
                int maxSize = ModelFilter.DefaultMaxFileSize;
                List<string> requestedModels = [];
                List<string> ignoredModels = [];
                List<string> registeredEps = []; // will be filled out after loading.
                ProgramAction action = ProgramAction.Help;

                // Specifying nothing is the same as 'run' with no other args
                if (args.Length == 0)
                {
                    return new(exe, ProgramAction.Run, ModelFilter.Default, requireToolUse, bruteForce, showVerbatimResponses, timeout);
                }

                if (args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    return new(exe, ProgramAction.Help, ModelFilter.Default, requireToolUse, bruteForce, showVerbatimResponses, timeout);
                }
                if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    action = ProgramAction.List;
                }
                else if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase))
                {
                    action = ProgramAction.Run;
                }
                else
                {
                    localIgnoredArgs.AddRange(args[1..]);
                    return new(exe, action, ModelFilter.Default, requireToolUse, bruteForce, showVerbatimResponses, timeout);
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
                            WriteLine($"`r{args[index]} option not recognized.");
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
                return new(exe, action, 
                    new(requestedModels, maxSize, ignoredModels, tryAllVariants, registeredEps, allowCpu),
                    requireToolUse, bruteForce, showVerbatimResponses, timeout);

                bool ParseArgParam(string param, Action<string> callback, string description)
                {
                    if (index < args.Length - 1)
                    {
                        ++index;
                        callback(args[index]);
                        return true;
                    }

                    WriteLine($"`r{param} option needs {description}.");
                    localIgnoredArgs.Add(args[index]);
                    return false;
                }
            }
        }

        static async Task<int> Main(string[] args)
        {
            var options = Options.Parse(args, out var ignoredArgs);

            if (options.Action is ProgramAction.Help or ProgramAction.Error)
            {
                Usage(options.ExeName);
                return (int)ExitCode.Other;
            }

            if (ignoredArgs.Count > 0)
            {
                WriteLine($"`oIgnoring unknown arguments {string.Join(", ", ignoredArgs)}");
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
                    `wArchitecture: `x{RuntimeInformation.ProcessArchitecture}.
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
                    return (int)await ListAsync(options, catalog);
                }
                if (options.Action == ProgramAction.Run)
                {
                    return (int)await RunAsync(options, catalog);
                }

                throw (new NotImplementedException("Don't know what to do"));
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: {ex.Message}");
                Usage(options.ExeName);
                return (int)ExitCode.Other;
            }
            finally
            {
                WriteLine($"`d[Total time: {sw.Elapsed.TotalSeconds:N2} seconds]");
            }
        }

        static void Usage(string exe)
        {
            WriteLine($"""
                
                `w{exe}`x - Checks Foundry Local models for successful tool-calling ability.

                `wUsage:
                   `w{exe}`x `x     `x           `x- Same as '`y{exe} run`x'.
                   `w{exe}`x `ylist `c[options]  `x- lists models that match given filters.
                   `w{exe}`x `yrun `c[options]   `x- runs test for models that match the given filters.
                   `w{exe}`x `yhelp `x           `x- this text.

                `wOptions (all are optional):
                   `c-m `g<model>     `xName or alias of a model to select (substring match). Can be specified more than once.
                   `x   `x            `xIf choosing a specific name (not alias), you must include the '-v' option.
                   `c-s `g<fileSize>  `xMaximum model file size allowed, in MB. Default is {ModelFilter.DefaultMaxFileSize}.
                   `c-i `g<model>     `xName or alias of a model to ignore (substring match). Can be specified more than once.
                   `c-t `g<time>      `xTime, in seconds, to wait for the model to respond. Default is {Options.DefaultTimeoutInSeconds}.
                   `c-f `x            `xForce tool calling (vs. leaving it as "auto").
                   `c-c `x            `xEnable CPU-based models, if applicable for your hardware.
                   `c-v `x            `xInclude all variants for the selected model(s).
                   `c-b `x            `xBrute-force tool-call attempts using basic pattern matching. Useful for some models.
                   `c-r `x            `xShow verbatim responses from the models.

                `wGrades:
                   {GetGradeInColor(AverageTestGrade.A)} - Correct tool call; the only `wreal`x passing grade.
                   {GetGradeInColor(AverageTestGrade.B)} - No explicit tool call, but can be brute-forced.
                   {GetGradeInColor(AverageTestGrade.C)} - [Only used as an average; not an actual grade.]
                   {GetGradeInColor(AverageTestGrade.D)} - [Only used as an average; not an actual grade.]
                   {GetGradeInColor(AverageTestGrade.E)} - Attempt to make a tool call, but the name or parameters are incorrect.
                   {GetGradeInColor(AverageTestGrade.F)} - No attempt to make a tool call, and couldn't brute-force it either.
                                                        
                `wExit code is 0 for success, > 0 for an error.
                """);
        }

        static async Task<ExitCode> ListAsync(Options options, ICatalog catalog)
        {
            int total = 0;
            WriteLine($"`yListing models for this device that match your criteria.{Environment.NewLine}");
            await EnumerateAllCandidateModelsAsync(options, catalog, filteredResult =>
            {
                if (filteredResult.MatchKind == ModelMatchKind.Matched)
                {
                    var model = filteredResult.Model;
                    WriteLine($"`w{filteredResult.Model.Alias,30} ({GetDeviceTypeInColor(model)}) `y{model.Info.Runtime!.ExecutionProvider,-25} `d[{model.Info.Name}]");
                    ++total;
                }

                return Task.CompletedTask;
            });

            WriteLine($"`y{$"Total models: {total}",30}");
            return total > 0 ? ExitCode.Success : ExitCode.Fail;
        }

        static async Task<ToolCallingResults> RunOneModelVariantAsync(Options options, ModelFilterResult filterResult)
        {
            var combinedResult = new ToolCallingResults(filterResult);
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
                await TimeActionAsync(() => model.LoadAsync(), "Loading model");
                var client = await TimeActionAsync(() => model.GetChatClientAsync(), "Getting chat client");
                client.Settings.Temperature = 0f;
                client.Settings.ToolChoice = options.RequireToolUse ? ToolChoice.Required : ToolChoice.Auto;

                foreach (var test in AllTests)
                {
                    var execResult = await TimeActionAsync(() => ExecutePromptAsync(options, client, test));
                    combinedResult.TestResults.Add(new(execResult.Value, execResult.Elapsed));
                }
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: Cannot load model {filterResult.Name}:{Environment.NewLine}{GetExceptionMessage(ex)}");
                combinedResult.TestResults.Add(new(TestGrade.F, TimeSpan.FromSeconds(0)));
            }
            finally
            {
                await model.UnloadAsync();
            }

            WriteLine($"""
                `w
                
                Combined Results for {model.Info.Name}:
                ===============
                `wBest grade:    {GetGradeInColor(combinedResult.TestResults.Min(x => x.Grade))}.
                `wAverage grade: {GetGradeInColor(combinedResult.AverageGrade)}.
                `wBest time:     `x{combinedResult.TestResults.Min(x => x.TotalTime.TotalMilliseconds):N0}ms.
                `wAverage time:  `x{combinedResult.AverageTime.TotalMilliseconds:N0}ms.

                """);

            return combinedResult;
        }

        static async Task<ExitCode> RunAsync(Options options, ICatalog catalog)
        {
            List<ToolCallingResults> results = [];

            await EnumerateAllCandidateModelsAsync(options, catalog,
                async filterResult => results.Add((await RunOneModelVariantAsync(options, filterResult))));

            int total = 0;
            int ignored = 0;

            WriteLine($"`w{"",30} |  Avg  |    Avg    |        |");
            WriteLine($"`w{"Alias",30} | Grade | {"Time (ms)",9} | Device | EP [name] ");

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
                    + $"`d{model.Info.Runtime!.ExecutionProvider} [{model.Info.Name}]");
            }

            WriteLine($"""
                `wRan: {total - ignored,2}
                `xIgnored: {ignored,2};
                `yTotal: `{total,2};
                """);

            return ExitCode.Success;
        }

        static bool IsExpectedFunctionCall(FunctionCall call, ModelTest test)
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

        static async Task<TestGrade> ExecutePromptAsync(Options options, OpenAIChatClient client, ModelTest test)
        {
            TestGrade exitCode = TestGrade.F;
            List<ChatMessage> messages = [systemChatMessage];
            messages.Add(new("user", test.UserPrompt));

            try
            {
                var response = await TimeActionAsync(() => client.CompleteChatAsync(
                    messages, toolDefinitions,
                    new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutInSeconds)).Token),
                    $"Sending prompt '{test.UserPrompt}'");

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
                    TryBruteForceFunctionCall(message.Content!, toolDefinitions, out var toolCall)
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
            catch (TaskCanceledException)
            {
                WriteLine($"`rChat took longer than {options.TimeoutInSeconds} seconds to respond.");
                WriteLine($"GRADE: {GetGradeInColor(TestGrade.F)}");
                exitCode = TestGrade.F;
            }
            catch (Exception ex)
            {
                WriteLine($"`rRuntime error.{Environment.NewLine}{GetExceptionMessage(ex)}");
                WriteLine($"GRADE: {GetGradeInColor(TestGrade.F)}");
                exitCode = TestGrade.F;
            }

            return exitCode;
        }

        // Letter or underscore, followed by any number of letters, underscores, digits, or periods
        // (Normal identifiers don't allow periods, but for tools people may like to use "noun.verb"
        // so it is allowed here
        const string identifierRegEx = """
            [a-zA-Z_](?:[a-zA-Z_0-9\.])*
            """;

        // A quote, followed by zero or more non-quote chars OR escaped quotes (with no embedded newlines).
        const string quotedStringRegEx = """
            "(?:[^"\r\n]|(?:\"))*?"
            """;

        // 1 or more digits followed by an optional period and one or more digits.
        // JSON doesn't support a leading decimal point (like ".1234") we don't match that.
        const string numberRegEx = """
            \d+(?:\.\d+)?
            """;

        const string boolRegEx = """
            true|false
            """;

        const string nullRegEx = """
            null
            """;

        static readonly Regex propertyNameRegex = new($"(?<identifier>{identifierRegEx})|(?<string>{quotedStringRegEx})", RegexOptions.Multiline);

        static readonly Regex propertyValueRegex = new($"(?<string>{quotedStringRegEx})|(?<number>{numberRegEx})|(?<boolean>{boolRegEx})|(?<null>{nullRegEx})", RegexOptions.IgnoreCase);

        /// <summary>
        /// Tries to brute-force a function call from message content. It's based on a pedestrian 
        /// parsing of the text for appropriate names / values in the right order. Explicitly does
        /// not require JSON parsing, as many models return partial JSON, XML, or other formats.
        /// </summary>
        /// <param name="rawMessageContent">The raw content of the message.</param>
        /// <param name="tools">The set of registered tools to check for.</param>
        /// <param name="function">If successful, the detected function call.</param>
        /// <returns>True if a function call was detected; otherwise false</returns>
        /// <remarks>
        /// For each ToolDefinition in 'tools', the parser does the following:
        /// 
        /// 1) Check for last occurence of its name (to skip over any prose like "I need to call...").
        ///    * If there is none, go to next tool.
        ///
        /// 2) Parse the rest of the string into the following tokens (all other chars are ignored):
        ///    * Identifier (typical C-style identifier, but also allows embedded periods).
        ///    * Quoted String (including the empty string and strings with escaped quotes).
        ///    * Boolean literal.
        ///    * Numeric literal (including floats).
        ///    * null literal.
        ///
        /// 3) Attempts to match the remaining tokens against the arguments defined by
        ///    the tool, in the form of <property-name> followed by <property-value>.
        ///    * Any string that isn't a valid argument name is ignored
        ///    * Only simple properties are supported (not arrays or nested objects).
        ///
        /// Arguments can be in any order, but this function assumes ALL defined parameters are required.
        /// </remarks>
        static bool TryBruteForceFunctionCall(string rawMessageContent, IEnumerable<ToolDefinition> tools, [NotNullWhen(true)] out FunctionCall? function)
        {
            function = null;
            foreach (var tool in tools)
            {
                var functionName = tool.Function!.Name;
                var parameters = tool.Function!.Parameters.Properties!;
                var arguments = new Dictionary<string, string>();

                var quotedFunctionName = $@"""{functionName}""";
                int foundLength;
                int index = rawMessageContent.LastIndexOf(quotedFunctionName);
                if (index >= 0)
                {
                    foundLength = quotedFunctionName.Length;
                }
                else
                {
                    // Look for unquoted name, like a C-style function call
                    index = rawMessageContent.LastIndexOf(functionName);
                    if (index < 0)
                    {
                        continue;
                    }
                    foundLength = functionName.Length;
                }

                int offset = index + foundLength;
                while (offset < rawMessageContent.Length && arguments.Count < parameters.Count)
                {
                    // Search for the next property name
                    var match = propertyNameRegex.Match(rawMessageContent, offset);
                    offset = match.Index + match.Length;
                    if (!match.Success)
                    {
                        // No tokens; can't be a call.
                        break;
                    }

                    string propertyName;
                    // If it was an identifier, use it as-is, otherwise strip the quotes
                    if (match.Groups["identifier"].Captures.Count > 0)
                    {
                        propertyName = match.Value;
                    }
                    else
                    {
                        propertyName = match.Value[1..^1];
                    }

                    if (string.IsNullOrEmpty(propertyName))
                    {
                        // It was an empty string; can't be a call.
                        break;
                    }

                    // Does it match a known parameter?
                    if (!parameters.TryGetValue(propertyName, out var propertyDefinition))
                    {
                        // Probably "arguments" or another ignorable string.
                        // Keep looking.
                        continue;
                    }

                    // Now look for the property value
                    match = propertyValueRegex.Match(rawMessageContent, offset);
                    offset = match.Index + match.Length;
                    if (!match.Success)
                    {
                        // No property value; can't be a legit call
                        break;
                    }

                    // Check if we captured anything of the right group (string, number, etc).
                    var groupName = GetRegexGroupName(propertyDefinition);
                    var targetGroup = match.Groups[groupName];

                    // Note that strings can also match the null literal.
                    if (targetGroup.Captures.Count == 0 && propertyDefinition.Type == "string")
                    {
                        targetGroup = match.Groups["null"];
                    }

                    if (targetGroup.Captures.Count == 0)
                    {
                        // Wasn't the correct type; bail
                        break;
                    }

                    var propertyValue = targetGroup.Value;

                    // For bools and null, convert to lower-case (we match case-insensitive because some models
                    // insist on using 'True' instead of 'true'... and they might do something similar for null)
                    if (targetGroup.Name is "boolean" or "null")
                    {
                        propertyValue = propertyValue.ToLowerInvariant();
                    }

                    arguments.Add($@"""{propertyName}""", propertyValue);
                }

                if (arguments.Count != parameters.Count)
                {
                    // Not enough args; try the next tool
                    // Note this assumes all args are required (could be relaxed)
                    continue;
                }

                // If we got here, the call was a success
                function = new FunctionCall
                {
                    Name = functionName,
                    Arguments = $$"""
                        { {{string.Join(", ", arguments.Select(i => i.Key + " : " + i.Value))}} }
                        """
                };

                return true;
            }

            // Didn't find anything that matched.
            return false;
        }

        static string GetRegexGroupName(PropertyDefinition property) => property.Type switch
        {
            "integer" => "number",
            _ => property.Type!
        };

        static async Task EnumerateAllCandidateModelsAsync(Options options, ICatalog catalog, Func<ModelFilterResult, Task> callback)
        {
            var models = await TimeActionAsync(() => catalog.ListModelsAsync(), "Getting list of models");
            await EnumerateAllCandidateModelsAsync(options, models, callback);
        }

        static async Task EnumerateAllCandidateModelsAsync(Options options, IEnumerable<IModel> models, Func<ModelFilterResult, Task> callback)
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
                
                `w{new string('-', 50)}
                `wAlias: `x{model.Alias}, `wDevice: {GetDeviceTypeInColor(model)}, `wSize: `x{model.Info.FileSizeMb:N0} MB, `wFull Name: `x{model.Info.Name}, `wEP: `x{model.Info.Runtime!.ExecutionProvider}
                """);
        }

        static void WriteLine(string content) => Write(content + Environment.NewLine);

        static void Write(string content)
        {
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
            var t = await f();
            sw.Stop();
            WriteLine($"`d it took {sw.ElapsedMilliseconds:N0} ms");
            return t;
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
