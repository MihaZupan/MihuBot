using Discord;
using Discord.WebSocket;
using MihuBot.Helpers;
using MihuBot.Helpers.TeamUp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class BirthdaysCommand : CommandBase
    {
        public override string Command => "birthdays";

        private TeamUpClient _teamUpClient;

        public override Task InitAsync(ServiceCollection services)
        {
            _teamUpClient = new TeamUpClient(
                services.Http,
                Secrets.TeamUp.APIKey,
                Secrets.TeamUp.CalendarKey,
                Secrets.TeamUp.SubCalendarId);

            return Task.CompletedTask;
        }

        public override Task HandleAsync(MessageContext ctx)
        {
            if (ctx.Guild.Id == Guilds.DDs && ctx.Channel.Id == Channels.DDsIntroductions && !ctx.Author.IsBot)
            {
                return HandleAsyncCore();
            }

            return Task.CompletedTask;

            async Task HandleAsyncCore()
            {
                SocketTextChannel birthdayChannel = ctx.Discord.GetTextChannel(Guilds.Mihu, Channels.BirthdaysLog);

                var message = ctx.Message;

                string reply;

                if (message.Content.Contains("name", StringComparison.OrdinalIgnoreCase) &&
                    message.Content.Contains("birth", StringComparison.OrdinalIgnoreCase))
                {
                    string[] lines = message.Content.SplitLines(removeEmpty: true);
                    string nameLine = lines.First(l => l.Contains("name", StringComparison.OrdinalIgnoreCase));
                    string birthdayLine = lines.First(l => l.Contains("birth", StringComparison.OrdinalIgnoreCase));

                    reply = $"{nameLine}\n{birthdayLine}";
                }
                else
                {
                    reply = "Could not find name/birthday";
                }

                await birthdayChannel.SendMessageAsync($"{message.GetJumpUrl()}\n{reply}");
            }
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!ctx.Author.IsAdminFor(Guilds.DDs))
                return;

            string action = ctx.Arguments.FirstOrDefault()?.ToLowerInvariant();

            if (action == "teamup")
            {
                int year = DateTime.UtcNow.Year;
                Event[] events = await _teamUpClient.SearchEventsAsync(new DateTime(year, 1, 1), new DateTime(year, 12, 31));

                string response = string.Join('\n', events.Select(e => $"{e.StartDt.ToISODate()} {GetNameFromTitle(e)}"));
                await ctx.Channel.SendTextFileAsync("BirthdaysTeamup.txt", response);
            }
            else if (action == "introductions")
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

                        nameLine = nameLine.Trim(' ', '\t', '`', '*', '_');
                        birthdayLine = birthdayLine.Trim(' ', '\t', '`', '*', '_');

                        if (nameLine.StartsWith("name: ", StringComparison.OrdinalIgnoreCase))
                        {
                            nameLine = nameLine.Substring(6);
                        }

                        if (birthdayLine.StartsWith("birthday: ", StringComparison.OrdinalIgnoreCase))
                        {
                            birthdayLine = birthdayLine.Substring(10);
                        }

                        nameLine = nameLine.PadRight(24, ' ');

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
            else if (action == "add")
            {
                Match match = Regex.Match(ctx.ArgumentLines.First(), @"^add (\d\d\d\d-\d\d?-\d\d?) (.+?)$", RegexOptions.IgnoreCase);
                if (match.Success && DateTime.TryParse(match.Groups[1].Value, out DateTime date))
                {
                    string name = match.Groups[2].Value.Trim();
                    Event createdEvent = await _teamUpClient.CreateYearlyWholeDayEventAsync($"♡ {name}'s Birthday ♡", date);

                    string eventLink = $"https://teamup.com/{Secrets.TeamUp.CalendarPublicKey}/events/{createdEvent.Id}";

                    await ctx.ReplyAsync($"Created a recurring event for {name} on {date:MMMM d}: {eventLink}");
                }
                else
                {
                    await ctx.ReplyAsync("Use format add YYYY-MM-DD name");
                }
            }
            else
            {
                await ctx.ReplyAsync("Specify source type: teamup / introductions");
            }
        }

        public string GetNameFromTitle(Event e)
        {
            string title = e.Title.Trim().Trim('\u2661').Trim();
            int apostrophe = title.IndexOf('\'');
            if (apostrophe != -1)
            {
                title = title.Substring(0, apostrophe);
            }

            return title;
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
    }
}
