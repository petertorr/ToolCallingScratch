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

    enum ToolCallingGrade
    {
        A = 0,  // Perfect. Requested a tool call with correct payload.
        B = 1,  // Acceptable. Output wasn't a proper tool call, but payload can be used with brute-force parsing hacks.
        // C, unused for single score
        // D, unused for single score
        E = 4,   // Unacceptable. Requested a tool call but the payload is unusable (e.g. bad function name or missing args). 
        F = 5,   // Unacceptable. No discernable attempt to make a tool call, or threw an exception, or timed out.
        Ineligible = 99
    }

    enum AverageToolCallingGrade
    {
        A, B, C, D, E, F,
        Ineligible = 99
    }

    record class ModelCounts(int Acceptable, int Ignored)
    {
        public int Total => Acceptable + Ignored;
    }

    record class ToolCallingIndividualTestResult(ToolCallingGrade Grade, TimeSpan TotalTime);
    class ToolCallingResult(ModelFilterResult filterResult)
    {
        public ModelFilterResult FilterResult { get; } = filterResult;
        public List<ToolCallingIndividualTestResult> TestResults { get; } = [];
        public List<ToolCallingIndividualTestResult> EligibleTestResults => TestResults.Where(x => x.Grade != ToolCallingGrade.Ineligible).ToList();
        public AverageToolCallingGrade AverageGrade => (AverageToolCallingGrade)(int)((float)TestResults.Sum(x => (int)x.Grade) / TestResults.Count + 0.8f); // +0.8f to nudge grade downwards.
        public TimeSpan AverageTime => TimeSpan.FromMilliseconds(TestResults.Sum(x => x.TotalTime.TotalMilliseconds) / TestResults.Count);
    }

    record class ModelTest(string UserPrompt, string ExpectedFunctionName, Dictionary<string, string> ExpectedArguments);

    enum ModelMatchKind
    {
        Matched,
        DidntMatch,
        NotRequested // either explicitly ignored or not the asked-for model.
    }

    record class ModelFilterResult(string Name, IModel Model, ModelMatchKind MatchKind, string? ReasonForOmission);

    record class ModelFilter(string? Alias, int MaxSizeInMb, List<string> IgnoredModels, bool TryAllVariants, List<string> RegisteredEps, bool AllowCpu)
    {
        internal static ModelFilter Default { get; } = new(null, int.MaxValue, [], false, [], false);

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

        internal ModelMatchKind GetMatchResult([NotNullWhen(true)] IModel model, [NotNullWhen(false)] out string? reasonForOmission)
        {
            reasonForOmission = null;
            if (Alias is not null && !model.Alias.Contains(Alias, StringComparison.OrdinalIgnoreCase))
            {
                reasonForOmission = $"Model {model.Info.Alias} doesn't match the requested alias.";
                return ModelMatchKind.NotRequested;
            }
            if (IgnoredModels.Contains(model.Alias, StringComparer.OrdinalIgnoreCase) ||
                IgnoredModels.Contains(model.Info.Name, StringComparer.OrdinalIgnoreCase))
            {
                reasonForOmission = $"Model {model.Info.Name} is explicitly ignored.";
                return ModelMatchKind.NotRequested;
            }
            if (!model.Info.SupportsToolCalling.HasValue || model.Info.SupportsToolCalling.Value == false)
            {
                reasonForOmission = $"Model {model.Info.Name} doesn't support tool-calling.";
                return ModelMatchKind.DidntMatch;
            }
            if (model.Info.FileSizeMb > MaxSizeInMb)
            {
                reasonForOmission = $"Model {model.Info.Name} is too large ({model.Info.FileSizeMb:N0} MB).";
                return ModelMatchKind.DidntMatch;
            }
            if (model.Info.Runtime!.DeviceType is DeviceType.CPU && !AllowCpu)
            {
                reasonForOmission = $"Model {model.Info.Name} only runs on the CPU.";
                return ModelMatchKind.DidntMatch;
            }
            if (RegisteredEps.Count > 0 && !RegisteredEps.Contains(model.Info.Runtime!.ExecutionProvider))
            {
                reasonForOmission = $"Model {model.Info.Name} is not supported by any registered EP.";
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
            Help
        }

        record class Options(string ExeName, ProgramAction Action, ModelFilter Filter, bool ForceToolUse, int TimeoutInSeconds)
        {
            internal const int DefaultTimeoutInSeconds = 60 * 1; // Time to wait for an answer. It's a trivial prompt...
            internal const int DefaultMaxFileSize = 6 * 1_024; // 6 gigs; reasonable default for machines with 16 GB of RAM.

            internal static Options Parse(string[] args, out List<string> ignoredArgs)
            {
                int index = 0;
                ignoredArgs = [];
                List<string> localIgnoredArgs = [];
                var exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath)!;
                bool allowCpu = false;
                bool forceToolUse = false;
                bool tryAllVariants = false;
                int timeout = DefaultTimeoutInSeconds;
                string? alias = null;
                int maxSize = DefaultMaxFileSize;
                List<string> ignoredModels = [];
                List<string> registeredEps = []; // will be filled out after loading.
                ProgramAction action = ProgramAction.Help;

                if (args.Length == 0)
                {
                    return new(exe, action, ModelFilter.Default, forceToolUse, timeout);
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
                    return new(exe, action, ModelFilter.Default, forceToolUse, timeout);
                }

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
                            forceToolUse = true;
                            break;

                        case "-t":
                            ParseArgParam("-t", s => timeout = int.Parse(s), "a time in seconds");
                            break;

                        case "-a":
                            ParseArgParam("-a", s => alias = s, "a model alias");
                            break;

                        case "-m":
                            ParseArgParam("-t", s => maxSize = int.Parse(s), "a file size in MB");
                            break;

                        case "-i":
                            ignoredModels.Add(args[index]);
                            break;

                        default:
                            WriteLine($"`r{args[index]} option not recognized.");
                            localIgnoredArgs.Add(args[index]);
                            break;
                    }
                }

                ignoredArgs = localIgnoredArgs;
                return new(exe, action, new(alias, maxSize, ignoredModels, tryAllVariants, registeredEps, allowCpu), forceToolUse, timeout);

                void ParseArgParam(string param, Action<string> callback, string description)
                {
                    if (index < args.Length - 1)
                    {
                        ++index;
                        callback(args[index]);
                    }
                    else
                    {
                        WriteLine($"`r-{param} option needs {description}.");
                        localIgnoredArgs.Add(args[index]);
                    }
                }
            }
        }

        static async Task<int> Main(string[] args)
        {
            var options = Options.Parse(args, out var ignoredArgs);
            if (ignoredArgs.Count > 0)
            {
                WriteLine($"`oIgnoring unknown arguments {string.Join(", ", ignoredArgs)}");
            }

            if (options.Action == ProgramAction.Help)
            {
                Usage(options.ExeName);
                return (int)ExitCode.Other;
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
                    `wArchitecture: {RuntimeInformation.ProcessArchitecture}.
                    Registered EPs: {string.Join(", ", eps.RegisteredEps)}.
                    Chosen model(s): {(options.Filter.Alias is not null ? options.Filter.Alias : "all")}{(options.Filter.TryAllVariants ? " (with variants)" : "")}.
                    Timeout (seconds): {options.TimeoutInSeconds}.
                    Forcing tool usage: {options.ForceToolUse}.
                    Max model size: {options.Filter.MaxSizeInMb}.
                    """);
                if (options.Filter.IgnoredModels.Count > 0)
                {
                    WriteLine($"`yIgnoring model(s): {string.Join(", ", options.Filter.IgnoredModels)}.");
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
                `w
                {exe} - Checks Foundry Local models for successful tool-calling ability.

                Usage:
                   1) {exe} list [options]  - lists models that match given filters.
                   2) {exe} run [options]   - runs test for models that match the given filters.
                   3) {exe} help            - this text.

                Options:
                   -a <alias>     A model alias like 'qwen3-4b' to select (partial match). By default, all models are selected.
                   -m <fileSize>  Maximum model file size allowed, in MB. Default is {Options.DefaultMaxFileSize}.
                   -i <model>     Name or alias of a model to ignore. Can be specified multiple times.
                   -t <time>      Time, in seconds, to wait for the model to respond. Default is {Options.DefaultTimeoutInSeconds}.
                   -f             Force tool calling (vs. leaving it as "auto").
                   -c             Enable CPU-based models, if applicable for your hardware.
                   -v             Include all variants for the selected model(s).
                                        
                Exit code is 0 for success, > 0 for an error.
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
                    Write($"`w{filteredResult.Model.Alias,30}");
                    Write(GetDeviceTypeInColor(model));
                    WriteLine($"`d {model.Info.Runtime!.ExecutionProvider,-25} [{model.Info.Name}]");
                    ++total;
                }

                return Task.CompletedTask;
            });

            WriteLine($"`y{$"Total models: {total}",30}");
            return total > 0 ? ExitCode.Success : ExitCode.Fail;
        }

        static async Task<ToolCallingResult> RunOneModelVariantAsync(Options options, ModelFilterResult filterResult)
        {
            var combinedResult = new ToolCallingResult(filterResult);
            if (filterResult.MatchKind != ModelMatchKind.Matched)
            {
                if (filterResult.MatchKind == ModelMatchKind.DidntMatch)
                {
                    WriteLine($"`cIgnoring {filterResult.Name}: {filterResult.ReasonForOmission}");
                }
                combinedResult.TestResults.Add(new(ToolCallingGrade.Ineligible, TimeSpan.FromSeconds(0)));
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
                client.Settings.ToolChoice = options.ForceToolUse ? ToolChoice.Required : ToolChoice.Auto;

                foreach (var test in AllTests)
                {
                    var execResult = await TimeActionAsync(() => ExecutePromptAsync(options, client, test));
                    combinedResult.TestResults.Add(new(execResult.Value, execResult.Elapsed));
                }
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: Cannot load model {filterResult.Name}:{Environment.NewLine}{GetExceptionMessage(ex)}");
                combinedResult.TestResults.Add(new(ToolCallingGrade.F, TimeSpan.FromSeconds(0)));
            }
            finally
            {
                await model.UnloadAsync();
            }

            WriteLine($"""
                `w
                
                Combined Results for {model.Info.Name}:
                ===============
                Best grade:    {combinedResult.TestResults.Min(x => x.Grade)}.
                Average grade: {combinedResult.AverageGrade}.
                Best time:     {combinedResult.TestResults.Min(x => x.TotalTime.TotalMilliseconds):N0}ms.
                Average time:  {combinedResult.AverageTime.TotalMilliseconds:N0}ms.

                """);

            return combinedResult;
        }

        static async Task<ExitCode> RunAsync(Options options, ICatalog catalog)
        {
            List<ToolCallingResult> results = [];

            await EnumerateAllCandidateModelsAsync(options, catalog,
                async filterResult => results.Add((await RunOneModelVariantAsync(options, filterResult))));

            int total = 0;
            int ignored = 0;

            WriteLine($"`w{"",30} |  Avg  |    Avg    |        |");
            WriteLine($"`w{"Alias",30} | Grade | {"Time (ms)",9} | Device | EP [name] ");

            foreach (var result in results)
            {
                if (result.FilterResult.MatchKind == ModelMatchKind.NotRequested)
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
                Write($"`w{model.Alias,30}");
                Write($"`w |   ");
                Write(GetGradeInColor(result.AverageGrade));
                Write($"`w   | ");
                Write($"`w{result.AverageTime.TotalMilliseconds,9:N0}");
                Write($"`w | ");
                Write(GetDeviceTypeInColor(model));
                Write($"`w | ");
                WriteLine($"`d{model.Info.Runtime!.ExecutionProvider} [{model.Info.Name}]");
            }

            WriteLine($"""
                `wRan: {total - ignored};
                Ignored: {ignored};
                Total: {total};
                """);

            return ExitCode.Success;
        }

        static bool IsCorrectFunctionCall(FunctionCall call, ModelTest test)
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

        static async Task<ToolCallingGrade> ExecutePromptAsync(Options options, OpenAIChatClient client, ModelTest test)
        {
            ToolCallingGrade exitCode = ToolCallingGrade.F;
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

                WriteLine($"`aVerbatim response:{Environment.NewLine}---{Environment.NewLine}{message.Content}{Environment.NewLine}---");

                if (reply.FinishReason == "tool_calls")
                {
                    if (IsCorrectFunctionCall(reply.Message.ToolCalls![0].FunctionCall!, test))
                    {
                        WriteLine($"`gGRADE: A. Correct tool-call syntax with correct data.");
                        exitCode = ToolCallingGrade.A;
                    }
                    else
                    {
                        WriteLine($"`rGRADE: E. Correct tool-call syntax, but incorrect data.");
                        exitCode = ToolCallingGrade.E;
                    }
                }
                // Try to brute-force it
                else if (TryBruteForceFunctionCall(message.Content!, toolDefinitions, out var toolCall)
                    && IsCorrectFunctionCall(toolCall, test))
                {
                    WriteLine($"`oGRADE: B. Incorrect tool-call syntax, but it was brute-forced.");
                    exitCode = ToolCallingGrade.B;
                }
                else
                {
                    WriteLine($"`rGRADE: F. Did not detect a tool-call, and could not brute-force it.");
                    exitCode = ToolCallingGrade.F;
                }
            }
            catch (TaskCanceledException)
            {
                WriteLine($"`rGRADE F: Chat took longer than {options.TimeoutInSeconds} seconds to respond.");
                exitCode = ToolCallingGrade.F;
            }
            catch (Exception ex)
            {
                WriteLine($"`rGRADE F: Runtime error.{Environment.NewLine}{GetExceptionMessage(ex)}");
                exitCode = ToolCallingGrade.F;
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

        // Tries to brute-force a function call from message content. For each ToolDefinition in 'tools',
        // it does the following:
        // 
        // 1) Check for last occurence of its name (to skip over any prose like "I need to call...").
        //    * If there is none, go to next tool.
        //
        // 2) Parse the rest of the string into the following tokens (all other chars are ignored):
        //    * Identifier (typical C-style identifier, but also allows embedded periods).
        //    * Quoted String (including the empty string and strings with escaped quotes).
        //    * Boolean literal.
        //    * Numeric literal (including floats).
        //    * null literal.
        //
        // 3) Attempts to match the remaining tokens against the arguments defined by
        //    the tool, in the form of <property-name> followed by <property-value>.
        //    * Any string that isn't a valid argument name is ignored
        //    * Only simple properties are supported (not arrays or nested objects).
        //
        // Arguments can be in any order, but this function assumes ALL defined parameters are required.
        // TODO: Consider updating it to support 'optional' parameters.
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
                DeviceType.GPU => "`g (GPU)",
                DeviceType.NPU => "`m (NPU)",
                DeviceType.CPU => "`d (CPU)",
                _ => "`r (Invalid)"
            };
        }

        static string GetGradeInColor(AverageToolCallingGrade grade) => GetGradeInColor((int)grade);
        static string GetGradeInColor(ToolCallingGrade grade) => GetGradeInColor((int)grade);
        static string GetGradeInColor(int value)
        {
            return value switch
            {
                0 => "`gA",
                1 => "`yB",
                2 => "`oC",
                3 => "`rD",
                4 => "`rE",
                5 => "`rF",
                _ => "`b-"
            };
        }

        static void ShowModelInfo(IModel model)
        {
            Write($"""
                `y{new string('-', 50)}
                Alias: {model.Alias}, Full Name: {model.Info.Name}, Size: {model.Info.FileSizeMb:N0} MB.
                EP: {model.Info.Runtime!.ExecutionProvider} 
                """);
            WriteLine($"{GetDeviceTypeInColor(model)}.");
        }

        static void WriteLine(string content) => Write(content + Environment.NewLine);

        static void Write(string content)
        {
            char color = ' ';

            // Content can begin with "`x" where x is a color.
            if (content is ['`', char c, .. var rest])
            {
                color = c;
                content = rest;
            }

            Console.ForegroundColor = color switch
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

            Console.Write(content);
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
