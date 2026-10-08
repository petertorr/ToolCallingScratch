using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ToolCallingScratch
{
    enum ExitCode
    {
        Success = 0,
        Partial = 1,
        Fail = 2,
        Timeout = 3,
        Exception = 4,
        Other = 99
    }

    record class ModelCounts(int Acceptable, int Ignored)
    {
        public int Total => Acceptable + Ignored;
    }

    internal class Program
    {
        static readonly List<ChatMessage> messages =
                [
                    new("system", "You provide weather forecast information using a tool."),
                    new("user", "What is the weather in Seattle?")
                ];
        static readonly ToolDefinition toolDefinition = ToolDefinition.DefineFunction(
            new FunctionDefinitionBuilder("getWeather", "Returns the weather for a specific city, in degrees celcius.")
            .AddParameter("city", PropertyDefinition.DefineString("The name of the city to get the weather for."), true)
            .Build());
        
        const int chatTimeoutInSeconds = 60 * 1; // Time to wait for an answer. It's a trivial prompt...
        const int maxErrorMessageLength = 500; // Truncate error messages longer than this.
        const int DefaultMaxFileSize = 6 * 1_024; // 6 gigs; reasonable default for machines with 16 GB of RAM.

        static int maxFileSizeInMb = DefaultMaxFileSize; // Max model size we want to support.
        static bool requireTools = false; // Whether to require tools or not.

        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 ||
                args[0].Equals("-h", StringComparison.OrdinalIgnoreCase) ||
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
                    }
                }

                await TimeActionAsync(() => FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance), "Creating instance");
                using var instance = FoundryLocalManager.Instance;
                var eps = await TimeActionAsync(() => instance.DownloadAndRegisterEpsAsync(), "Registering EPs");
                var catalog = await TimeActionAsync(() => instance.GetCatalogAsync(), "Getting catalog");
                WriteLine($"""
                    `wArchitecture: {RuntimeInformation.ProcessArchitecture}.
                    Registered EPs: {string.Join(", ", eps.RegisteredEps)}.
                    Max file size: {maxFileSizeInMb:N} MB.;
                    Forcing tool usage: {requireTools}.;
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
                    return (int)await RunAllModelsAsync(catalog);
                }

                var model = (await TimeActionAsync(() => catalog.GetModelAsync(modelName), $"Getting model {modelName}"));
                return (int)await RunOneModelAsync(model);
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
            WriteLine($"""
                `w
                {Path.GetFileNameWithoutExtension(Environment.ProcessPath)} - Checks Foundry Local models for successful tool-calling ability.

                Usage:
                   <modelAlias> [maxFileSize [requireTools]]
                   all [maxFileSize [requireTools]]
                   list

                   * <modelAlias>  a model alias like 'qwen3-4b'; do not use the full name (like 'qwen3-4b-cuda-gpu').
                   * maxFileSize   optional; maximum model file size to check, in MB. Default is {DefaultMaxFileSize}.
                   * requireTools  optional; whether to require the use of tools (default is 'false').
                   * all           checks all available models.
                   * list          lists all models that "should" support tool-calling, according to their metadata.
                                        
                Exit code is 0 for success, > 0 for an error.
                """);
        }

        static async Task<ExitCode> ListAllModelsAsync(ICatalog catalog)
        {
            var result = await EnumerateAllCandidateModelsAsync(catalog, model =>
            {
                Write($"`c{model.Alias}");
                WriteLine(GetDeviceTypeInColor(model));
                return Task.CompletedTask;
            }, false);

            return result.Acceptable > 0 ? ExitCode.Success : ExitCode.Other;
        }

        static async Task<ExitCode> RunOneModelAsync(IModel? model)
        {
            if (!IsCandidateModel(model, true))
            {
                return ExitCode.Other;
            }

            ShowModelInfo(model);
            try
            {
                await TimeActionAsync(() => model.DownloadAsync(), "Downloading model");
                await TimeActionAsync(() => model.LoadAsync(), "Loading model");
                var client = await TimeActionAsync(() => model.GetChatClientAsync(), "Getting chat client");
                client.Settings.Temperature = 0.001f;
                client.Settings.ToolChoice = requireTools ? ToolChoice.Required : ToolChoice.Auto;
                return await ExecutePromptAsync(client);
            }
            catch (Exception ex)
            {
                WriteLine($"`r{Environment.NewLine}ERROR: Cannot load model:{Environment.NewLine}{GetExceptionMessage(ex)}");
                return ExitCode.Exception;
            }
            finally
            {
                await model.UnloadAsync();
            }
        }

        static async Task<ExitCode> RunAllModelsAsync(ICatalog catalog)
        {
            var results = new List<string>[] { [], [], [], [], [], [] }; // one per exit code. 'badarg' and 'other' shouldn't happen...

            var totals = await EnumerateAllCandidateModelsAsync(catalog,
                async model => results[(int)await RunOneModelAsync(model)].Add(model.Info.Alias),
                true);

            WriteLine($"""
                `w
                Results
                =======
                  Success ({results[(int)ExitCode.Success].Count,2}): {string.Join(", ", results[(int)ExitCode.Success])}
                  Partial ({results[(int)ExitCode.Partial].Count,2}): {string.Join(", ", results[(int)ExitCode.Partial])}
                     Fail ({results[(int)ExitCode.Fail].Count,2}): {string.Join(", ", results[(int)ExitCode.Fail])}
                  Timeout ({results[(int)ExitCode.Timeout].Count,2}): {string.Join(", ", results[(int)ExitCode.Timeout])}
                Exception ({results[(int)ExitCode.Exception].Count,2}): {string.Join(", ", results[(int)ExitCode.Exception])}
                  Ignored  {totals.Ignored}
                    Total  {totals.Total}
                """);

            return ExitCode.Success;
        }

        static async Task<ExitCode> ExecutePromptAsync(OpenAIChatClient client)
        {
            ExitCode exitCode = ExitCode.Fail;
            try
            {

                var response = await TimeActionAsync(() => client.CompleteChatAsync(messages, [toolDefinition], new CancellationTokenSource(TimeSpan.FromSeconds(chatTimeoutInSeconds)).Token), "Sending prompt");
                var reply = response.Choices[0]!;

                WriteLine($"`aVerbatim response:{Environment.NewLine}---{Environment.NewLine}{reply.Message.Content}{Environment.NewLine}---");

                if (reply.FinishReason == "tool_calls")
                {
                    var toolCall = reply.Message!.ToolCalls![0].FunctionCall;
                    var funcArgs = toolCall!.ParseArguments();
                    if (toolCall.Name == "getWeather" && funcArgs.ContainsKey("city"))
                    {
                        WriteLine($"`gSUCCESS: Correct tool-call syntax.");
                        exitCode = ExitCode.Success;
                    }
                    else
                    {
                        WriteLine($"`yPARTIAL: Correct tool-call syntax, but incorrect data.");
                        exitCode = ExitCode.Partial;
                    }
                }
                else
                {
                    WriteLine($"`rFAIL: Incorrect tool-call syntax.");
                    exitCode = ExitCode.Fail;
                }
            }
            catch (TaskCanceledException)
            {
                WriteLine($"`rFAIL: Chat took longer than {chatTimeoutInSeconds} seconds to respond.");
                exitCode = ExitCode.Timeout;
            }
            catch (Exception ex)
            {
                WriteLine($"`rFAIL: Runtime error.{Environment.NewLine}{GetExceptionMessage(ex)}");
                exitCode = ExitCode.Exception;
            }

            return exitCode;
        }

        static async Task<ModelCounts> EnumerateAllCandidateModelsAsync(ICatalog catalog, Func<IModel, Task> callback, bool showInfo)
        {
            int acceptable = 0;
            int ignored = 0;

            foreach (var model in await TimeActionAsync(() => catalog.ListModelsAsync(), "Getting list of models"))
            {
                if (!IsCandidateModel(model, showInfo))
                {
                    ignored++;
                    continue;
                }

                acceptable++;
                await callback(model);
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
                    WriteLine($"`cModel {model.Info.Name} is too large ({model.Info.FileSizeMb:N} MB); ignoring.");
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
                Alias: {model.Alias}, Full Name: {model.Info.Name}, Size: {model.Info.FileSizeMb:N} MB.
                EP: {model.Info.Runtime!.ExecutionProvider}
                """);
            WriteLine(GetDeviceTypeInColor(model) + ".");
        }

        static void WriteLine(string content) => Write(content + Environment.NewLine);

        static void Write(string content)
        {
            char color = ' ';
            // Can begin with "`x" where x is a color.
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
    }
}
