using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class BirthdaysCommand : CommandBase
    {
        public override string Command => "birthdays";

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.IsFromAdmin)
                return;

            string source = ctx.Arguments.FirstOrDefault()?.ToLowerInvariant();

            if (source == "teamup")
            {
                var teamupEvents = await GetTeamupBirthdaysAsync(ctx);
                string response = string.Join('\n', teamupEvents.Select(e => $"{e.Date.ToISODate()} {e.Name}"));
                await ctx.Channel.SendTextFileAsync("BirthdaysTeamup.txt", response);
            }
            else if (source == "introductions")
            {
                SimpleMessageModel[] messages = await TryLoadMessagesFromCache();

                if (messages is null || ctx.Arguments.Any(a => a.Equals("reload", StringComparison.OrdinalIgnoreCase)))
                {
                    SocketTextChannel channel = ctx.Discord.GetTextChannel(Guilds.DDs, Channels.DDsIntroductions);

                    messages = (await channel.DangerousGetAllMessagesAsync($"Birthdays command ran by {ctx.Author.Username}"))
                        .Select(m => SimpleMessageModel.FromMessage(m))
                        .ToArray();

                    await SaveMessagesToCache(messages);
                }

                var failed = new List<string>();
                var response = new StringBuilder();

                foreach (SimpleMessageModel message in messages)
                {
                    if (message.Content.Contains("name", StringComparison.OrdinalIgnoreCase) &&
                        message.Content.Contains("birth", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] lines = message.Content.SplitLines(removeEmpty: true);
                        string nameLine = lines.First(l => l.Contains("name", StringComparison.OrdinalIgnoreCase));
                        string birthdayLine = lines.First(l => l.Contains("birth", StringComparison.OrdinalIgnoreCase));

                        response.Append(nameLine).Append(" - ").Append(birthdayLine).Append('\n');
                    }
                    else
                    {
                        failed.Append(message.Content);
                    }
                }

                if (failed.Any())
                {
                    response.Append("\n\nFailed:\n");
                    foreach (string failure in failed)
                    {
                        response.Append(failure.NormalizeNewLines().Replace("\n", " <new-line> ")).Append('\n');
                    }
                }

                await ctx.Channel.SendTextFileAsync("BirthdaysIntroductions.txt", response.ToString());
            }
            else
            {
                await ctx.ReplyAsync("Specify source type: teamup / introductions");
            }
        }

        private async Task<(string Name, DateTime Date)[]> GetTeamupBirthdaysAsync(CommandContext ctx)
        {
            int year = DateTime.UtcNow.Year;
            string uri = $"https://teamup.com/ks6sktk26s43g8c5hw/events?startDate={year}-01-01&endDate={year + 1}-01-01";
            string json = await ctx.Services.Http.GetStringAsync(uri);

            EventModel[] events = JsonConvert.DeserializeObject<TeamUpResponse>(json).Events;

            return events
                .Select(e => (e.Name(), e.StartDt.Date))
                .ToArray();
        }

        private const string MessagesCachePath = "IntroductionsMessagesCache.json";

        private static async Task<SimpleMessageModel[]> TryLoadMessagesFromCache()
        {
            if (!File.Exists(MessagesCachePath))
                return null;

            string json = await File.ReadAllTextAsync(MessagesCachePath);
            return JsonConvert.DeserializeObject<SimpleMessageModel[]>(json);
        }

        private static async Task SaveMessagesToCache(SimpleMessageModel[] messages)
        {
            await File.WriteAllTextAsync(MessagesCachePath, JsonConvert.SerializeObject(messages));
        }

        private class SimpleMessageModel
        {
            public ulong Id;
            public DateTime TimeStamp;
            public string Content;
            public ulong AuthorId;
            public string AuthorName;

            public static SimpleMessageModel FromMessage(IMessage message)
            {
                return new SimpleMessageModel()
                {
                    Id = message.Id,
                    TimeStamp = message.Timestamp.UtcDateTime,
                    Content = message.Content,
                    AuthorId = message.Author.Id,
                    AuthorName = message.Author.Username
                };
            }
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
        private class TeamUpResponse
        {
            public EventModel[] Events;
        }

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
        private class EventModel
        {
            public string Id;
            public string Title;
            public DateTime StartDt;

            public string Name()
            {
                string title = Title.Trim().Trim('\u2661').Trim();
                int apostrophe = title.IndexOf('\'');
                if (apostrophe != -1)
                {
                    title = title.Substring(0, apostrophe);
                }

                return title;
            }
        }
    }
}
