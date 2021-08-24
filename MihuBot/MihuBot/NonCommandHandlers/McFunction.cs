using Discord;
using MihuBot.Commands;
using MihuBot.Helpers;
using System.Text;

namespace MihuBot.NonCommandHandlers
{
    public sealed class McFunction : INonCommandHandler
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _configuration;

        public McFunction(HttpClient httpClient, IConfiguration configuration)
        {
            _http = httpClient;
            _configuration = configuration;
        }

        public Task HandleAsync(MessageContext ctx)
        {
            if (ctx.AuthorId != KnownUsers.Miha || !ctx.Message.Attachments.Any())
                return Task.CompletedTask;

            Attachment mcFunction = ctx.Message.Attachments.FirstOrDefault(a => a.Filename.EndsWith(".mcfunction", StringComparison.OrdinalIgnoreCase));
            if (mcFunction is null)
                return Task.CompletedTask;

            return HandleAsyncCore();

            async Task HandleAsyncCore()
            {
                string functionsFile = await _http.GetStringAsync(mcFunction.Url);
                string[] functions = functionsFile
                    .Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => f.Trim().Length > 0)
                    .Select(f => "execute positioned as MihuBot run " + f)
                    .ToArray();

                await ctx.ReplyAsync($"Running {functions.Length} commands");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder();

                        await McCommand.RunMinecraftCommandAsync("gamerule sendCommandFeedback false", dreamlings: true, _configuration);

                        for (int i = 0; i < functions.Length; i += 100)
                        {
                            Task<string>[] tasks = functions
                                .AsMemory(i, Math.Min(100, functions.Length - i))
                                .ToArray()
                                .Select(f => McCommand.RunMinecraftCommandAsync(f, dreamlings: true, _configuration))
                                .ToArray();

                            await Task.WhenAll(tasks);

                            foreach (var task in tasks)
                                sb.AppendLine(task.Result);
                        }

                        await McCommand.RunMinecraftCommandAsync("gamerule sendCommandFeedback true", dreamlings: true, _configuration);

                        var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
                        await ctx.Channel.SendFileAsync(ms, "responses.txt");
                    }
                    catch { }
                });
            }
        }
    }
}
