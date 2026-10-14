using Betalgo.Ranul.OpenAI.Builders;
using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Betalgo.Ranul.OpenAI.ObjectModels.SharedModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace ToolCallingScratch
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            string modelName = args.FirstOrDefault() ?? "qwen2.5-1.5bb";
            Console.WriteLine("Initializing... might take a few minutes.");
            await FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance);
            using var instance = FoundryLocalManager.Instance;
            var eps = await instance.DownloadAndRegisterEpsAsync();
            var model = (await (await instance.GetCatalogAsync()).GetModelAsync(modelName))!;
            await (model.DownloadAsync());
            await model.LoadAsync();
            var client = await model.GetChatClientAsync();
            client.Settings.Temperature = 0f;

            List<ChatMessage> messages = [
                new("system", "You provide weather forecast information using a tool."),
            new("user", "What is the weather in Seattle?")
            ];

            var tool = ToolDefinition.DefineFunction(
                new FunctionDefinitionBuilder("getWeather", "Returns the weather for a specific city, in degrees celcius.")
                .AddParameter("city", PropertyDefinition.DefineString("The name of the city to get the weather for."), true)
                .Build());

            Console.WriteLine($"Registered EPs: {string.Join(", ", eps.RegisteredEps)}.");
            Console.WriteLine($"Failed EPs: {string.Join(", ", eps.FailedEps)}.");
            Console.WriteLine($"Model full name: {model.Id}");

            await RunOnePromptAsync(false, client, messages, tool);
            await RunOnePromptAsync(true, client, messages, tool);

            return 1;
        }

        static async Task RunOnePromptAsync(bool required, OpenAIChatClient client, List<ChatMessage> messages, ToolDefinition tool)
        {
            Console.WriteLine($"{Environment.NewLine}Mode: {(required ? "required" : "auto")}");
            client.Settings.ToolChoice = required ? ToolChoice.Required : ToolChoice.Auto;

            var reply = (await client.CompleteChatAsync(messages, [tool])).Choices[0];
            Console.WriteLine($"Verbatim response:{Environment.NewLine}{reply.Message.Content}");

            if (reply.FinishReason == "tool_calls")
            {
                var toolCall = reply.Message!.ToolCalls![0].FunctionCall;
                var funcArgs = toolCall!.ParseArguments();
                if (toolCall.Name == "getWeather" && funcArgs.TryGetValue("city", out var city)
                    && city.ToString() == "Seattle")
                {
                    Console.WriteLine($"SUCCESS: Used <tool_call> correctly.");
                }
                else
                {
                    Console.WriteLine($"FAIL: Used <tool_call> but with invalid data.");
                }

                return;
            }

            Console.WriteLine($"FAIL: Didn't use <tool_call> at all.");
        }
    }
}
