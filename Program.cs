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
        A, B, C, D, E, F
    }

    record class ModelCounts(int Acceptable, int Ignored)
    {
        public int Total => Acceptable + Ignored;
    }

    record class ToolCallingIndividualResult(ToolCallingGrade Grade, TimeSpan TotalTime);
    class ToolCallingResult(IModel model)
    {
        public IModel Model { get; } = model;
        public List<ToolCallingIndividualResult> Results { get; } = [];

        public AverageToolCallingGrade AverageGrade => (AverageToolCallingGrade)(int)((float)Results.Sum(x => (int)x.Grade) / Results.Count + 0.8f); // +0.8f to nudge grade downwards.
        public TimeSpan AverageTime => TimeSpan.FromMilliseconds(Results.Sum(x => x.TotalTime.TotalMilliseconds) / Results.Count);
    }

    record class ModelTest(string UserPrompt, string ExpectedFunctionName, Dictionary<string, string> ExpectedArguments);

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

        const int chatTimeoutInSeconds = 60 * 1; // Time to wait for an answer. It's a trivial prompt...
        const int maxErrorMessageLength = 500; // Truncate error messages longer than this.
        const int DefaultMaxFileSize = 6 * 1_024; // 6 gigs; reasonable default for machines with 16 GB of RAM.

        static int maxFileSizeInMb = DefaultMaxFileSize; // Max model size we want to support.
        static bool requireTools = false; // Whether to require tools or not.
        static bool allowCpu = false; // Whether to attempt CPU-based execution (could be VERY slow).

        static string[] registeredEps = [];

        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 ||
                args[0].Equals("/h", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("/?", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("-?", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Usage();
                return (int)ExitCode.Other;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                string modelName = args[0];

                if (args.Length > 1)
                {
                    maxFileSizeInMb = int.Parse(args[1]);

                    if (args.Length > 2)
                    {
                        requireTools = bool.Parse(args[2]);
                        if (args.Length > 3)
                        {
                            allowCpu = bool.Parse(args[3]);
                        }
                    }
                }

                await TimeActionAsync(() => FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance), "Creating instance");
                using var instance = FoundryLocalManager.Instance;
                var eps = await TimeActionAsync(() => instance.DownloadAndRegisterEpsAsync(), "Registering EPs");
                registeredEps = eps.RegisteredEps;
                var catalog = await TimeActionAsync(() => instance.GetCatalogAsync(), "Getting catalog");
                WriteLine($"""
                    `wArchitecture: {RuntimeInformation.ProcessArchitecture}.
                    Registered EPs: {string.Join(", ", eps.RegisteredEps)}.
                    Max file size: {maxFileSizeInMb:N} MB.
                    Forcing tool usage: {requireTools}.
                    """);

                if (eps.FailedEps.Length > 0)
                {
                    WriteLine($"`rFailed EPs: {string.Join(", ", eps.FailedEps)}.");
                }
                if (modelName.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    return (int)await ListAllModelsAsync(catalog);
                }
                if (modelName.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    return (int)await RunAllModelsAsync(catalog, false);
                }
                if (modelName.Equals("allvariants", StringComparison.OrdinalIgnoreCase))
                {
                    return (int)await RunAllModelsAsync(catalog, true);
                }

                var model = (await TimeActionAsync(() => catalog.GetModelAsync(modelName), $"Getting model {modelName}"));
                if (model is null)
                {
                    WriteLine($"`r{modelName} is not a valid model.");
                    return (int)ExitCode.Other;
                }

                return (int)((await RunOneModelAsync(model)).AverageGrade switch
                {
                    AverageToolCallingGrade.A => ExitCode.Success,
                    AverageToolCallingGrade.B => ExitCode.Success,
                    _ => ExitCode.Fail
                });
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: {ex.Message}");
                Usage();
                return (int)ExitCode.Other;
            }
            finally
            {
                WriteLine($"`d[Total time: {sw.Elapsed.TotalSeconds:N2} seconds]");
            }
        }

        static void Usage()
        {
            var exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath);

            WriteLine($"""
                `w
                {exe} - Checks Foundry Local models for successful tool-calling ability.

                Usage:
                   1) {exe} <modelAlias> [<maxFileSize> [<requireTools> [<enableCpu>]]]
                   2) {exe} all [<maxFileSize> [<requireTools> [<enableCpu>]]]
                   3) {exe} allvariants [<maxFileSize> [<requireTools> [<enableCpu>]]]
                   4) {exe} list

                Arguments:
                   * <modelAlias>    a model alias like 'qwen3-4b'; do not use the full name (like 'qwen3-4b-cuda-gpu').
                   * <maxFileSize>   optional; maximum model file size to check, in MB. Default is {DefaultMaxFileSize}.
                   * <requireTools>  optional; whether to require the use of tools (default is 'false').
                   * <enabeCpu>      optional; whether to enable running CPU-based models (s.l.o.w.) (default is 'false').
                   * all             checks all available models, using the "best" variant for the device.
                   * allvariants     checks all available models, inculding variants that aren't the "best" for the device.
                   * list            lists all models that "should" support tool-calling, according to their metadata.
                                        
                Exit code is 0 for success, > 0 for an error.
                """);
        }

        static async Task<ExitCode> ListAllModelsAsync(ICatalog catalog)
        {
            int total = 0;
            WriteLine($"`yLising all applicable models for this device, including variants.{Environment.NewLine}");
            var result = await EnumerateAllCandidateModelsAsync(catalog, true, model =>
            {
                Write($"`w{model.Alias,30}");
                Write(GetDeviceTypeInColor(model));
                WriteLine($"`d {model.Info.Runtime!.ExecutionProvider,-25} [{model.Info.Name}]");
                ++total;
                return Task.CompletedTask;
            }, false);

            WriteLine($"`y{$"Total models: {total}",30}");
            return result.Acceptable > 0 ? ExitCode.Success : ExitCode.Other;
        }

        static async Task<ToolCallingResult> RunOneModelAsync(IModel model)
        {
            var combinedResult = new ToolCallingResult(model);
            if (!IsCandidateModel(model, true))
            {
                combinedResult.Results.Add(new(ToolCallingGrade.Ineligible, TimeSpan.FromSeconds(0)));
                return combinedResult;
            }

            ShowModelInfo(model);
            try
            {
                await TimeActionAsync(() => model.DownloadAsync(), "Downloading model");
                await TimeActionAsync(() => model.LoadAsync(), "Loading model");
                var client = await TimeActionAsync(() => model.GetChatClientAsync(), "Getting chat client");
                client.Settings.Temperature = 0f;
                client.Settings.ToolChoice = requireTools ? ToolChoice.Required : ToolChoice.Auto;

                foreach (var test in AllTests)
                {
                    var execResult = await TimeActionAsync(() => ExecutePromptAsync(client, test));
                    combinedResult.Results.Add(new(execResult.Value, execResult.Elapsed));
                }
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: Cannot load model:{Environment.NewLine}{GetExceptionMessage(ex)}");
                combinedResult.Results.Add(new(ToolCallingGrade.F, TimeSpan.FromSeconds(0)));
            }
            finally
            {
                await model.UnloadAsync();
            }

            WriteLine($"""
                `y
                
                Combined Result:
                ================
                Average grade: {combinedResult.AverageGrade}
                Average time:  {combinedResult.AverageTime.TotalMilliseconds:N0}ms
                """);

            return combinedResult;
        }

        static async Task<ExitCode> RunAllModelsAsync(ICatalog catalog, bool includeVariants)
        {
            var results = new List<string>[] { [], [], [], [], [], [] }; // one per grade, A-F.

            var totals = await EnumerateAllCandidateModelsAsync(catalog, includeVariants,
                async model => results[(int)(await RunOneModelAsync(model)).AverageGrade].Add(model.Info.Alias),
                true);

            WriteLine($"""
                `w
                Results
                =======
                      A: {GetLine(ToolCallingGrade.A)}
                      B: {GetLine(ToolCallingGrade.B)}
                         --- failing grades ---
                      E: {GetLine(ToolCallingGrade.E)}
                      F: {GetLine(ToolCallingGrade.F)}

                Ignored: {totals.Ignored,2}
                -------------
                  Total: {totals.Total,2}
                """);

            return ExitCode.Success;

            string GetLine(ToolCallingGrade grade) => $"{results[(int)grade].Count,2} {string.Join(", ", results[(int)grade])}";
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

        static async Task<ToolCallingGrade> ExecutePromptAsync(OpenAIChatClient client, ModelTest test)
        {
            ToolCallingGrade exitCode = ToolCallingGrade.F;
            List<ChatMessage> messages = [systemChatMessage];
            messages.Add(new("user", test.UserPrompt));

            try
            {
                var response = await TimeActionAsync(() => client.CompleteChatAsync(messages, toolDefinitions, new CancellationTokenSource(TimeSpan.FromSeconds(chatTimeoutInSeconds)).Token), $"Sending prompt '{test.UserPrompt}'");
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
                    WriteLine($"`rGRADE: F. No detected attempt at calling a tool with the correct name / arguments.");
                    exitCode = ToolCallingGrade.F;
                }
            }
            catch (TaskCanceledException)
            {
                WriteLine($"`rGRADE F: Chat took longer than {chatTimeoutInSeconds} seconds to respond.");
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
        //    * Quoted String (including the empty string and strings with escaped quotes)
        //    * Boolean literal
        //    * Numeric literal (including floats)
        //    * null literal
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
                    // insist on using 'True' instead of 'true'... and they might do something similar for null
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
        static async Task<ModelCounts> EnumerateAllCandidateModelsAsync(ICatalog catalog, bool includeVariants, Func<IModel, Task> callback, bool showInfo)
        {
            var models = await TimeActionAsync(() => catalog.ListModelsAsync(), "Getting list of models");
            return await EnumerateAllCandidateModelsAsync(models, includeVariants, callback, showInfo);
        }

        static async Task<ModelCounts> EnumerateAllCandidateModelsAsync(IEnumerable<IModel> models, bool includeVariants, Func<IModel, Task> callback, bool showInfo)
        {
            int acceptable = 0;
            int ignored = 0;

            foreach (var model in models)
            {
                if (includeVariants)
                {
                    var result = await EnumerateAllCandidateModelsAsync(model.Variants, false, callback, showInfo);
                    acceptable += result.Acceptable;
                    ignored += result.Ignored;
                }
                else
                {
                    if (!IsCandidateModel(model, showInfo))
                    {
                        ignored++;
                        continue;
                    }

                    acceptable++;
                    await callback(model);
                }
            }

            return new(acceptable, ignored);
        }

        static bool IsCandidateModel([NotNullWhen(true)] IModel? model, bool showInfo)
        {
            if (model is null)
            {
                if (showInfo)
                {
                    WriteLine($"`oThe model you chose does not exist.");
                }
                return false;
            }
            if (!model.Info.SupportsToolCalling.HasValue || model.Info.SupportsToolCalling.Value == false)
            {
                if (showInfo)
                {
                    WriteLine($"`cModel {model.Info.Name} doesn't claim to support tool calling; ignoring.");
                }
                return false;
            }
            if (model.Info.FileSizeMb > maxFileSizeInMb)
            {
                if (showInfo)
                {
                    WriteLine($"`cModel {model.Info.Name} is too large ({model.Info.FileSizeMb:N0} MB); ignoring. (Increase minimum file size to include it.)");
                }
                return false;
            }
            if (model.Info.Runtime!.DeviceType is DeviceType.CPU && !allowCpu)
            {
                if (showInfo)
                {
                    WriteLine($"`cModel {model.Info.Name} only runs on the CPU; ignoring. (Enable CPU usage to include it; will be slow.)");
                }
                return false;
            }
            if (!registeredEps.Contains(model.Info.Runtime!.ExecutionProvider))
            {
                if (showInfo)
                {
                    WriteLine($"`cModel {model.Info.Name} not supported by any registered EP; ignoring.");
                }
                return false;
            }

            return true;
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
