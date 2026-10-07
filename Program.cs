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
        static async Task<int> Main(string[] args)
        {
            Console.WriteLine($"Initializing arch={RuntimeInformation.ProcessArchitecture}...");
            var sw = Stopwatch.StartNew();
            await FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance);
            var instance = FoundryLocalManager.Instance;
            var eps = await instance.DownloadAndRegisterEpsAsync();
            var catalog = await instance.GetCatalogAsync();
            var model = await catalog.GetModelAsync("qwen2.5-7b");
            await model.DownloadAsync();
            await model.LoadAsync();
            var client = await model.GetChatClientAsync();
            sw.Stop();
            Console.WriteLine($"Initialization took {sw.ElapsedMilliseconds:N0}ms");
            Console.WriteLine($"EPs are {string.Join(", ", eps.RegisteredEps)}.");

            List<ChatMessage> messages = [
                new("system", "You provide weather forecast information using a tool."),
                new("user", "What is the weather in Seattle?")
            ];

            FunctionDefinitionBuilder builder = new("getWeather", "Returns the weather for a specific city, in degrees celcius.");
            builder.AddParameter("city", PropertyDefinition.DefineString("The name of the city to get the weather for."), true);
            var tool = ToolDefinition.DefineFunction(builder.Build());

            sw = Stopwatch.StartNew();
            var response = await client.CompleteChatAsync(messages, [tool]);
            sw.Stop();
            Console.WriteLine($"Chat completion took {sw.ElapsedMilliseconds:N0}ms");

            var reply = response.Choices[0]!;
            Console.WriteLine($"Verbatim response:{Environment.NewLine}{reply.Message.Content}");

            if (reply.FinishReason == "tool_calls")
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"SUCCESS: Correct tool-call syntax.");
                Console.ResetColor();
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FAIL: Incorrect tool-call syntax.");
            Console.ResetColor();
            return 1;
        }
    }
}
