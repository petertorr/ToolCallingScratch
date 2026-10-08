using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;

namespace ToolCallingScratch
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            string modelName = args.FirstOrDefault() ?? "qwen2.5-7b";
            IModel? model = null;
            try
            {
                Console.WriteLine("Initializing... might take a few minutes.");
                await FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance);
                using var instance = FoundryLocalManager.Instance;
                var eps = await instance.DownloadAndRegisterEpsAsync();
                model = (await (await instance.GetCatalogAsync()).GetModelAsync(modelName))!;
                await (model.DownloadAsync());
                await model.LoadAsync();
                var client = await model.GetChatClientAsync();
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
                Console.WriteLine($"Failed EPs: {string.Join(", ", eps.FailedEps)}.");
                Console.WriteLine($"Model full name: {model.Id}");

                var reply = (await client.CompleteChatAsync(messages, [tool])).Choices[0];
                Console.WriteLine($"Verbatim response:{Environment.NewLine}---{Environment.NewLine}{reply.Message.Content}{Environment.NewLine}---");

                if (reply.FinishReason == "tool_calls")
                {
                    var toolCall = reply.Message!.ToolCalls![0].FunctionCall;
                    var funcArgs = toolCall!.ParseArguments();
                    if (toolCall.Name == "getWeather" && funcArgs.ContainsKey("city"))
                    {
                        Console.WriteLine($"SUCCESS: Correct tool-call syntax.");
                    }
                    else
                    {
                        Console.WriteLine($"PARTIAL: Correct tool-call syntax; incorrect data.");
                    }
                    return 0;
                }

                Console.WriteLine($"FAIL: Incorrect tool-call syntax.");
                return 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL: Runtime error.");
                Console.WriteLine(ex.ToString());
                if (model is not null)
                {
                    await model.UnloadAsync();
                }
                return 2;
            }
        }
    }
}
