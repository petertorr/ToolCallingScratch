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
            Console.WriteLine("Initializing... might take a few minutes.");
            string modelName = args.FirstOrDefault() ?? "phi-4-mini-reasoning";
            const int timeout = 3;
            await FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance);
            using var instance = FoundryLocalManager.Instance;
            var eps = await instance.DownloadAndRegisterEpsAsync();
            var model = (await (await instance.GetCatalogAsync()).GetModelAsync(modelName))!;
            await model.DownloadAsync();
            await model.LoadAsync();
            var client = await model.GetChatClientAsync();
            List<ChatMessage> messages = [new("system", "You are a sycophantic AI."), new("user", "Do you like me?")];

            Console.WriteLine($"Registered EPs: {string.Join(", ", eps.RegisteredEps)}.");
            Console.WriteLine($"Failed EPs: {string.Join(", ", eps.FailedEps)}.");
            Console.WriteLine($"Model full name: {model.Id}");

            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            ct.Token.Register(() => Console.WriteLine($"{Environment.NewLine}TIMEOUT"));

            var sw = Stopwatch.StartNew();
            var reply = (await client.CompleteChatAsync(messages, null, ct.Token)).Choices[0];
            sw.Stop();
            Console.WriteLine($"Ran for {sw.Elapsed.TotalSeconds} seconds, was supposed to quit after {timeout}");
            return 1;
        }
    }
}
