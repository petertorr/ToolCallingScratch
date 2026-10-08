using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
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

    internal class Program
    {
        static async Task Measure(Func<Task> f, string description)
        {
            await Measure(async Task<bool> () => { await f(); return true; }, description);
        }

        static async Task<T> Measure<T>(Func<Task<T>> f, string description)
        {
            ColorWrite($"`d{description}...");
            var sw = Stopwatch.StartNew();
            var t = await f();
            sw.Stop();
            ColorWriteLine($"`d it took {sw.ElapsedMilliseconds:N0} ms");
            return t;
        }

        static async Task<int> Main(string[] args)
        {
            try
            {
                string modelName = args[0];
                bool requireTools = false;
                if (args.Length > 1)
                {
                    requireTools = bool.Parse(args[1]);
                }

                await Measure(() => FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance), "Creating instance");
                using var instance = FoundryLocalManager.Instance;
                var eps = await Measure(() => instance.DownloadAndRegisterEpsAsync(), "Registering EPs");
                var catalog = await Measure(() => instance.GetCatalogAsync(), "Getting catalog");
                ColorWriteLine($"`bArchitecture: {RuntimeInformation.ProcessArchitecture}.");
                ColorWriteLine($"`bRegistered EPs: {string.Join(", ", eps.RegisteredEps)}.");
                if (eps.FailedEps.Length > 0)
                {
                    ColorWriteLine($"`rFailed EPs: {string.Join(", ", eps.FailedEps)}.");
                }

                if (modelName.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    return (int)await RunAll(catalog, requireTools);
                }

                var model = (await Measure(() => catalog.GetModelAsync(modelName), $"Getting model {modelName}"));
                return (int)await RunOne(model, requireTools);
            }
            catch (Exception ex)
            {
                if (ex is not IndexOutOfRangeException)
                {
                    ColorWriteLine($"`r{Environment.NewLine}ERROR: {ex.Message}");
                }

                ColorWriteLine($"""
                    `m
                    Usage: {Path.GetFileNameWithoutExtension(Environment.ProcessPath)} <modelAlias> [requireTools]

                        <modelAlias>    is either an alias (like 'qwen2.5-7b') or the special value 'all' to run all models.
                        [requireTools]  optional; whether to require the use of tools (default is 'false').
                    
                    Exit code is 0 for success, > 0 for an error.
                    """);

                return (int)ExitCode.Other;
            }
        }

        static async Task<ExitCode> RunOne(IModel? model, bool requireTools)
        {
            if (!IsCandidateModel(model))
            {
                return ExitCode.Other;
            }

            ShowModelInfo(model, requireTools);
            try
            {
                await Measure(() => model.DownloadAsync(), "Downloading model");
                await Measure(() => model.LoadAsync(), "Loading model");
                var client = await Measure(() => model.GetChatClientAsync(), "Getting chat client");
                client.Settings.Temperature = 0.001f;
                client.Settings.ToolChoice = requireTools ? ToolChoice.Required : ToolChoice.Auto;
                return await RunOnePrompt(client);
            }
            catch (Exception ex)
            {
                ColorWriteLine($"`r{Environment.NewLine}ERROR: Cannot load model:{Environment.NewLine}{GetExceptionMessage(ex)}");
                return ExitCode.Exception;
            }
            finally
            {
                await model.UnloadAsync();
            }
        }

        static readonly List<ChatMessage> messages =
                [
                    new("system", "You provide weather forecast information using a tool."),
                    new("user", "What is the weather in Seattle?")
                ];

        static readonly ToolDefinition toolDefinition = ToolDefinition.DefineFunction(
            new FunctionDefinitionBuilder("getWeather", "Returns the weather for a specific city, in degrees celcius.")
            .AddParameter("city", PropertyDefinition.DefineString("The name of the city to get the weather for."), true)
            .Build());

        const int chatTimeoutInSeconds = 60 * 3;

        static async Task<ExitCode> RunOnePrompt(OpenAIChatClient client)
        {
            ExitCode exitCode = ExitCode.Fail;
            try
            {

                var response = await Measure(() => client.CompleteChatAsync(messages, [toolDefinition], new CancellationTokenSource(TimeSpan.FromSeconds(chatTimeoutInSeconds)).Token), "Sending prompt");
                var reply = response.Choices[0]!;

                ColorWriteLine($"`aVerbatim response:{Environment.NewLine}---{Environment.NewLine}{reply.Message.Content}{Environment.NewLine}---");

                if (reply.FinishReason == "tool_calls")
                {
                    var toolCall = reply.Message!.ToolCalls![0].FunctionCall;
                    var funcArgs = toolCall!.ParseArguments();
                    if (toolCall.Name == "getWeather" && funcArgs.ContainsKey("city"))
                    {
                        ColorWriteLine($"`gSUCCESS: Correct tool-call syntax.");
                    }
                    else
                    {
                        ColorWriteLine($"`yPARTIAL: Correct tool-call syntax, but incorrect data.");
                        exitCode = ExitCode.Partial;
                    }
                }
                else
                {
                    ColorWriteLine($"`rFAIL: Incorrect tool-call syntax.");
                    exitCode = ExitCode.Fail;
                }
            }
            catch (TaskCanceledException)
            {
                ColorWriteLine($"`rFAIL: Chat took longer than {chatTimeoutInSeconds} seconds to respond.");
                exitCode = ExitCode.Timeout;
            }
            catch (Exception ex)
            {
                ColorWriteLine($"`rFAIL: Runtime error.{Environment.NewLine}{GetExceptionMessage(ex)}");
                exitCode = ExitCode.Exception;
            }

            return exitCode;
        }

        static bool IsCandidateModel(IModel? model)
        {
            if (model is null)
            {
                ColorWriteLine($"`oThe model you chose does not exist.");
                return false;
            }
            if (!model.Info.SupportsToolCalling.HasValue || model.Info.SupportsToolCalling.Value == false)
            {
                ColorWriteLine($"`cModel {model.Info.Name} doesn't support tool calling; ignoring.");
                return false;
            }
            if (model.Info.FileSizeMb > 8_192)
            {
                ColorWriteLine($"`cModel {model.Info.Name} is too large ({model.Info.FileSizeMb:N} MB); ignoring.");
                return false;
            }

            return true;
        }

        static async Task<ExitCode> RunAll(ICatalog catalog, bool requireTools)
        {
            var results = new List<string>[] { [], [], [], [], [], [] }; // one per exit code. 'badarg' and 'other' shouldn't happen...
            int count = 0;
            int ignored = 0;

            foreach (var model in await Measure(() => catalog.ListModelsAsync(), "Getting list of models"))
            {
                if (!IsCandidateModel(model))
                {
                    ignored++;
                    continue;
                }

                count++;
                results[(int)await RunOne(model, requireTools)].Add(model.Info.Alias);
            }

            ColorWriteLine($"""
                `w
                Results
                =======
                Success: {results[(int)ExitCode.Success].Count} {string.Join(", ", results[(int)ExitCode.Success])}
                Partial: {results[(int)ExitCode.Partial].Count} {string.Join(", ", results[(int)ExitCode.Partial])}
                Fail:    {results[(int)ExitCode.Fail].Count} {string.Join(", ", results[(int)ExitCode.Fail])}
                Timeout: {results[(int)ExitCode.Timeout].Count} {string.Join(", ", results[(int)ExitCode.Timeout])}
                Exception: {results[(int)ExitCode.Exception].Count} {string.Join(", ", results[(int)ExitCode.Exception])}
                Ignored: {ignored}
                Total: {count + ignored}
                """);

            return ExitCode.Success;
        }

        static void ColorWriteLine(string content) => ColorWrite(content + Environment.NewLine);

        static void ColorWrite(string content)
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
                'y' => ConsoleColor.Yellow,
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

        static void ShowModelInfo(IModel model, bool requireTools)
        {
            ColorWriteLine($"""
                `y{new string('-', 50)}
                Name: {model.Info.Name} ({model.Info.FileSizeMb:N} MB).
                EP: {model.Info.Runtime!.ExecutionProvider} running on the {model.Info.Runtime.DeviceType}.
                """);
            if (requireTools)
            {
                ColorWriteLine($"`oForcing the use of tools.");
            }
            Console.ResetColor();
        }

        const int maxErrorMessageLength = 300;
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
    }
}
