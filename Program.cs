using Betalgo.Ranul.OpenAI.ObjectModels.RequestModels;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text;

namespace ToolCallingScratch
{
    internal class Program
    {
        const string systemPrompt = "Keep your responses short.";
        const string userPrompt = "What color is the sky?";

        static async Task<int> Main(string[] args)
        {
            string modelName = args.FirstOrDefault() ?? "qwen3-0.6b";
            Console.WriteLine("Initializing... might take a few minutes.");
            await FoundryLocalManager.CreateAsync(new() { AppName = "ToolCallingScratch" }, NullLogger.Instance);
            using var instance = FoundryLocalManager.Instance;
            var eps = await instance.DownloadAndRegisterEpsAsync();
            var model = (await (await instance.GetCatalogAsync()).GetModelAsync(modelName))!;
            await model.DownloadAsync();
            await model.LoadAsync();
            Console.WriteLine($"Model full name: {model.Id}");
            Console.WriteLine($"Registered EPs: {string.Join(", ", eps.RegisteredEps)}");
            if (eps.FailedEps.Length > 0)
            {
                Console.WriteLine($"Failed EPs: {string.Join(", ", eps.FailedEps)}");
            }
            Console.Write("Use [o]ld API or [n] API? ");
            var c = Console.Read();
            Console.WriteLine();

            const int timeout = 3;
            bool finished = false;
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(timeout)).Token;
            ct.Register(async () =>
            {
                await Task.Delay(500); // race condition buffer
                Console.WriteLine($"{Environment.NewLine}{(finished ? "" : "TIMEOUT")}");
            });

            var sw = Stopwatch.StartNew();
            Console.WriteLine($"Thinking for {timeout} seconds...");
            try
            {
                string result = await (c switch
                {
                    int x when x is 'o' or 'O' => DoOldApi(model, ct),
                    _ => DoNewApi(model, ct)
                });

                sw.Stop();
                finished = true;
                Console.WriteLine($"System prompt: {systemPrompt}");
                Console.WriteLine($"User prompt: {userPrompt}");
                Console.WriteLine($"Reply: {result}");

                if (sw.Elapsed.TotalSeconds > timeout + 1) // +1 for leniency
                {
                    Console.WriteLine($"Fail: Ran for {sw.Elapsed.TotalSeconds:N2} seconds, was supposed to quit after {timeout}.");
                    return 1;
                }
                else
                {
                    Console.WriteLine($"Success: Ran for {sw.Elapsed.TotalSeconds:N2} seconds.");
                }
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                Console.WriteLine($"Success: Cancelled after {sw.Elapsed.TotalSeconds:N2} seconds.");
            }

            return 0;
        }

        static async Task<string> DoNewApi(IModel model, CancellationToken ct)
        {
            using var session = new ChatSession(model);
            using var request = new Request();
            request.AddItem(new MessageItem(MessageRole.System, systemPrompt));
            request.AddItem(new MessageItem(MessageRole.User, userPrompt));
            var response = (await session.ProcessRequestAsync(request, ct));

            StringBuilder result = new();
            foreach (var item in response.OfType<MessageItem>())
            {
                foreach (var part in item.Parts.OfType<TextItem>())
                {
                    result.AppendLine($"{part.ItemType}: {part.Text}");
                }
            }

            return result.ToString();
        }

        static async Task<string> DoOldApi(IModel model, CancellationToken ct)
        {
            List<ChatMessage> messages = [
                new ("system", systemPrompt),
                new ("user", userPrompt)
            ];
            var client = await model.GetChatClientAsync();
            var response = (await client.CompleteChatAsync(messages, ct));
            return response.Choices[0].Message.Content;
        }
    }
}
