using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToolCallingScratch
{
    internal class Program
    {
        static async Task Measure(Func<Task> f, string description)
        {
            await Measure(async Task<bool> () => { await f(); return true; }, description);
        }

        static async Task<T> Measure<T>(Func<Task<T>> f, string description)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{description}...");
            var sw = Stopwatch.StartNew();
            var t = await f();
            sw.Stop();
            Console.WriteLine($" it took {sw.ElapsedMilliseconds:N0} ms");
            Console.ResetColor();
            return t;
        }

        static async Task<int> Main(string[] args)
        {
            string modelName = args.FirstOrDefault() ?? "qwen2.5-7b";
            IModel? model = null;
            try
            {
                await Measure(() => FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance), "Creating instance");
                using var instance = FoundryLocalManager.Instance;

                var eps = await Measure(() => instance.DownloadAndRegisterEpsAsync(), "Registering EPs");

                var catalog = await Measure(() => instance.GetCatalogAsync(), "Getting catalog");
                model = (await Measure(() => catalog.GetModelAsync(modelName), $"Getting model {modelName}"))!;
                await Measure(() => model.DownloadAsync(), "Downloading model");
                await Measure(() => model.LoadAsync(), "Loading model");

                var client = await Measure(() => model.GetChatClientAsync(), "Getting chat client");
                client.Settings.Temperature = 0.001f;
                client.Settings.ToolChoice = ToolChoice.Auto; // DO NOT set to "Required"; nothing works then!

                List<ChatMessage> messages = [
                    new("system", "You provide weather forecast information using a tool."),
                    new("user", "What is the weather in Seattle?")
                ];

                FunctionDefinitionBuilder builder = new("getWeather", "Returns the weather for a specific city, in degrees celcius.");
                builder.AddParameter("city", PropertyDefinition.DefineString("The name of the city to get the weather for."), true);
                var tool = ToolDefinition.DefineFunction(builder.Build());

                Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}.");
                Console.WriteLine($"Registered EPs: {string.Join(", ", eps.RegisteredEps)}.");
                if (eps.FailedEps.Length > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Failed EPs: {string.Join(", ", eps.FailedEps)}.");
                    Console.ResetColor();
                }

                Console.WriteLine($"Model full name: {model.Id}");

                var response = await Measure(() => client.CompleteChatAsync(messages, [tool]), "Sending prompt");

                var reply = response.Choices[0]!;
                Console.WriteLine($"Verbatim response:{Environment.NewLine}---{Environment.NewLine}{reply.Message.Content}{Environment.NewLine}---");

                if (reply.FinishReason == "tool_calls")
                {
                    var toolCall = reply.Message!.ToolCalls![0].FunctionCall;
                    var funcArgs = toolCall!.ParseArguments();
                    if (toolCall.Name == "getWeather" && funcArgs.ContainsKey("city"))
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"SUCCESS: Correct tool-call syntax.");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"PARTIAL: Correct tool-call syntax, but incorrect data.");
                    }
                    Console.ResetColor();
                    return 0;
                }

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Incorrect tool-call syntax.");
                Console.ResetColor();
                return 1;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL: Runtime error.");
                Console.WriteLine(ex.ToString());
                Console.ResetColor();
                if (model is not null)
                {
                    await model.UnloadAsync();
                }
                return 2;
            }
        }
    }
}
