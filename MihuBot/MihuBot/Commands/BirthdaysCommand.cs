using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
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
                int year = DateTime.UtcNow.Year;
                string uri = $"https://teamup.com/ks6sktk26s43g8c5hw/events?startDate={year}-01-01&endDate={year + 1}-01-01";
                string json = await ctx.Services.Http.GetStringAsync(uri);

                EventModel[] events = JsonConvert.DeserializeObject<TeamUpResponse>(json).Events;

                string response = string.Join('\n', events.Select(e => $"{e.Name()} {e.StartDt.ToShortDateString()}"));
                await ctx.Channel.SendFileAsync("BirthdaysTeamup.txt", response);
            }
            else if (source == "introductions")
            {
                SocketTextChannel channel = ctx.Discord.GetTextChannel(Guilds.DDs, Channels.DDsIntroductions);

                var messagesSource = channel.GetMessagesAsync(limit: int.MaxValue, options: new RequestOptions()
                {
                    AuditLogReason = $"Birthdays command ran by {ctx.Author.Username}"
                });

                IMessage[] messages = (await messagesSource.ToArrayAsync())
                    .SelectMany(i => i)
                    .Where(i => !string.IsNullOrWhiteSpace(i.Content))
                    .ToArray();

                var failed = new List<string>();
                var response = new StringBuilder();

                foreach (IMessage message in messages)
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

                await ctx.Channel.SendFileAsync("BirthdaysIntroductions.txt", response.ToString());
            }
            else
            {
                await ctx.ReplyAsync("Specify source type: teamup / introductions");
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
