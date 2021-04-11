using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using MihuBot.Helpers;
using MihuBot.Helpers.TeamUp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class BirthdaysCommand : CommandBase
    {
        public override string Command => "birthdays";

        private readonly Logger _logger;
        private readonly TeamUpClient _teamUpClient;
        private readonly SynchronizedLocalJsonStore<List<BirthdayEntry>> _birthdayEntries = new("BirthdayEntries.json");

        public BirthdaysCommand(HttpClient httpClient, Logger logger, IConfiguration configuration)
        {
            _logger = logger;
            _teamUpClient = new TeamUpClient(
                httpClient,
                configuration["TeamUp:ApiKey"],
                configuration["TeamUp:CalendarKey"],
                configuration.GetValue<int>("TeamUp:SubCalendarId"));
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
                SocketTextChannel birthdayChannel = ctx.Discord.GetTextChannel(Channels.BirthdaysLog);

                var message = ctx.Message;

                string reply = TryGetNameAndBirthday(message.Content, out string name, out string birthday)
                    ? $"Name: {name}\nBirthday: {birthday}"
                    : "Could not find name/birthday";

                await birthdayChannel.SendMessageAsync($"{message.GetJumpUrl()}\n{reply}");
            }
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            if (!await ctx.RequirePermissionAsync("birthdays"))
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
                SimpleMessageModel[] messages = await LoadMessagesAsync(ctx,
                    maxAge: ctx.Arguments.Any(a => a.Equals("reload", StringComparison.OrdinalIgnoreCase))
                        ? TimeSpan.FromSeconds(5)
                        : TimeSpan.FromHours(6));

                var failed = new List<string>();
                var response = new StringBuilder();

                foreach (SimpleMessageModel message in messages.Where(m => !m.AuthorIsBot))
                {
                    if (TryGetNameAndBirthday(message.Content, out string name, out string birthday))
                    {
                        name = name.PadRight(24, ' ');
                        response.Append(name).Append(" - ").Append(birthday).Append('\n');
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
                        response.AppendLine(failure.NormalizeNewLines().Replace("\n", " <new-line> "));
                    }
                }

                await ctx.Channel.SendTextFileAsync("BirthdaysIntroductions.txt", response.ToString());
            }
            else if (action == "add")
            {
                Match match = Regex.Match(ctx.ArgumentLines.First(), @"^add (\d{1,20}) (\d\d\d\d[-\.]\d\d?[-\.]\d\d?) (.+?)$", RegexOptions.IgnoreCase);
                if (match.Success &&
                    ulong.TryParse(match.Groups[1].Value, out ulong messageId) &&
                    DateTime.TryParse(match.Groups[2].Value, out DateTime date))
                {
                    SimpleMessageModel[] messages = await LoadMessagesAsync(ctx, maxAge: TimeSpan.FromHours(6));

                    if (!messages.Any(m => m.Id == messageId))
                        messages = await LoadMessagesAsync(ctx, maxAge: TimeSpan.FromMinutes(15));

                    SimpleMessageModel message = messages.FirstOrDefault(m => m.Id == messageId);
                    if (message is null)
                    {
                        await ctx.ReplyAsync("Could not find a message with that ID");
                        return;
                    }

                    List<BirthdayEntry> entries = await _birthdayEntries.EnterAsync();
                    try
                    {
                        BirthdayEntry previousEntry = entries.FirstOrDefault(e => e.DiscordMessageId == messageId);
                        if (previousEntry != null)
                        {
                            await ctx.ReplyAsync("Birthday event already exists: " + previousEntry.EventLink);
                            return;
                        }

                        string name = match.Groups[3].Value.Trim();
                        Event createdEvent = await _teamUpClient.CreateYearlyWholeDayEventAsync($"♡ {name}'s Birthday ♡", date);

                        var entry = new BirthdayEntry()
                        {
                            Name = name,
                            Date = date,
                            DiscordMessageId = message.Id,
                            DiscordAuthorId = message.AuthorId,
                            TeamUpEventId = createdEvent.RecurringId
                        };
                        entries.Add(entry);

                        await ctx.ReplyAsync($"Created a recurring event for {name} on {date:MMMM d}: {entry.EventLink}");
                    }
                    catch (Exception ex)
                    {
                        await ctx.DebugAsync(ex);
                        await ctx.ReplyAsync("Something went wrong");
                    }
                    finally
                    {
                        _birthdayEntries.Exit();
                    }
                }
                else
                {
                    await ctx.ReplyAsync("Usage: `!birthdays add MessageId YYYY-MM-DD name`");
                }
            }
            else if (action == "bind")
            {
                Match match = Regex.Match(ctx.ArgumentLines.First(), @"^bind (\d{1,20}) (?:https:\/\/teamup\.com\/.*?\/events\/)?(.+?-rid-.*)$", RegexOptions.IgnoreCase);

                if (match.Success && ulong.TryParse(match.Groups[1].Value, out ulong messageId))
                {
                    string eventId = match.Groups[2].Value;

                    List<BirthdayEntry> entries = await _birthdayEntries.EnterAsync();
                    try
                    {
                        BirthdayEntry entry = entries.FirstOrDefault(e => e.TeamUpEventId == eventId);
                        if (entry != null)
                        {
                            await ctx.ReplyAsync($"Event is already bound to a message: {GetMessageLink(entry.DiscordMessageId)}");
                            return;
                        }

                        SimpleMessageModel[] messages = await LoadMessagesAsync(ctx, maxAge: TimeSpan.FromMinutes(30));
                        SimpleMessageModel message = messages.FirstOrDefault(m => m.Id == messageId);
                        if (message is null)
                        {
                            await ctx.ReplyAsync("Could not find a message with that ID");
                            return;
                        }

                        Event teamUpEvent = await _teamUpClient.TryGetEventAsync(eventId);
                        if (teamUpEvent is null)
                        {
                            await ctx.ReplyAsync("Could not find that event");
                            return;
                        }

                        entries.Add(new BirthdayEntry()
                        {
                            Name = GetNameFromTitle(teamUpEvent),
                            Date = teamUpEvent.StartDt.UtcDateTime.Date,
                            DiscordMessageId = message.Id,
                            DiscordAuthorId = message.AuthorId,
                            TeamUpEventId = teamUpEvent.RecurringId
                        });

                        await ctx.ReplyAsync($"Added binding for {teamUpEvent.Title} on {teamUpEvent.StartDt.ToISODate()} to {GetMessageLink(messageId)}");
                    }
                    finally
                    {
                        _birthdayEntries.Exit();
                    }
                }
                else
                {
                    await ctx.ReplyAsync("Usage: `!birthdays bind MessageId TeamUpId/Link`");
                }
            }
            else if (action == "binds")
            {
                List<BirthdayEntry> entries = await _birthdayEntries.EnterAsync();
                try
                {
                    if (entries.Any())
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (var entry in entries.OrderBy(entry => entry.Date))
                        {
                            sb.Append(entry.Date.ToISODate())
                                .Append(' ').Append(entry.Name)
                                .Append(" - ").Append(entry.EventLink)
                                .Append(" - ").AppendLine(GetMessageLink(entry.DiscordMessageId));
                        }
                        await ctx.Channel.SendTextFileAsync("Binds.txt", sb.ToString());
                    }
                }
                finally
                {
                    _birthdayEntries.Exit();
                }
            }
            else if (action == "remove")
            {
                Match match = Regex.Match(ctx.ArgumentLines.First(), @"^remove (?:https:\/\/teamup\.com\/.*?\/events\/)?(.+?)-rid-.*?$", RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    string eventId = match.Groups[1].Value;

                    List<BirthdayEntry> entries = await _birthdayEntries.EnterAsync();
                    try
                    {
                        BirthdayEntry entry = entries.FirstOrDefault(e => e.TeamUpEventId == eventId);
                        if (entry is null)
                        {
                            await ctx.ReplyAsync($"Could not find a matching entry for {eventId}");
                        }
                        else
                        {
                            entries.Remove(entry);
                            await ctx.ReplyAsync($"Removed an entry for {entry.Name} on {entry.Date:MMMM d}: {entry.EventLink}");
                        }
                    }
                    finally
                    {
                        _birthdayEntries.Exit();
                    }
                }
                else
                {
                    await ctx.ReplyAsync("Usage: `!birthdays remove TeamUpId/Link`");
                }
            }
            else if (action == "status")
            {
                int year = DateTime.UtcNow.Year;
                Event[] events = await _teamUpClient.SearchEventsAsync(new DateTime(year, 1, 1), new DateTime(year, 12, 31));

                List<BirthdayEntry> entries = await _birthdayEntries.EnterAsync();
                try
                {
                    // Multiple entries per Discord user
                    var multipleEventsPerUser = entries
                        .GroupBy(e => e.DiscordAuthorId)
                        .Where(g => g.Count() > 1)
                        .ToArray();

                    if (multipleEventsPerUser.Length > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var userEvents in multipleEventsPerUser)
                        {
                            sb.Append(userEvents.Key);

                            SocketUser user = ctx.Discord.GetUser(userEvents.Key);
                            if (user != null)
                                sb.Append(" (").Append(user.Username).Append(')');

                            sb.AppendLine(" has multiple events:");
                            foreach (var @event in userEvents)
                            {
                                sb.Append(@event.Date.ToISODate())
                                    .Append(' ').Append(@event.Name)
                                    .Append(" - ").Append(@event.EventLink)
                                    .Append(" - ").AppendLine(GetMessageLink(@event.DiscordMessageId));
                            }
                            sb.AppendLine();
                        }
                        await ctx.Channel.SendTextFileAsync("MultipleEventsPerUser.txt", sb.ToString());
                    }

                    // TeamUp event doesn't exist anymore
                    HashSet<string> eventIds = events.Select(e => e.RecurringId).ToHashSet();

                    BirthdayEntry[] orphanedEntries = entries
                        .Where(e => !eventIds.Contains(e.TeamUpEventId))
                        .ToArray();

                    if (orphanedEntries.Length > 0)
                    {
                        var sb = new StringBuilder();
                        foreach (var @event in orphanedEntries)
                        {
                            sb.Append("Discord user ").Append(@event.DiscordAuthorId);

                            SocketUser user = ctx.Discord.GetUser(@event.DiscordAuthorId);
                            if (user != null)
                                sb.Append(" (").Append(user.Username).Append(')');

                            sb.AppendLine();
                            sb.Append(@event.Date.ToISODate()).Append(' ').AppendLine(@event.Name);
                            sb.AppendLine(GetMessageLink(@event.DiscordMessageId));
                            sb.AppendLine();
                        }
                        await ctx.Channel.SendTextFileAsync("OrphanedEntries.txt", sb.ToString());
                    }

                    // Introductions without a matching event
                    SimpleMessageModel[] messages = await LoadMessagesAsync(ctx, maxAge: TimeSpan.FromMinutes(5));

                    HashSet<ulong> messageIds = entries.Select(e => e.DiscordMessageId).ToHashSet();

                    messages = messages
                        .Where(m => !messageIds.Contains(m.Id))
                        .Where(m => !m.AuthorIsBot)
                        .ToArray();

                    if (messages.Length > 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.Append(messages.Length).AppendLine(" introductions remaining").AppendLine();
                        foreach (SimpleMessageModel message in messages)
                        {
                            sb.AppendLine(GetMessageLink(message.Id));
                            if (TryGetNameAndBirthday(message.Content, out string name, out string birthday))
                            {
                                sb.Append("Name: ").AppendLine(name);
                                sb.Append("Birthday: ").AppendLine(birthday);
                            }
                            else
                            {
                                sb.AppendLine(message.Content.NormalizeNewLines().Replace("\n", " <new-line> "));
                            }
                            sb.AppendLine();
                        }
                        await ctx.Channel.SendTextFileAsync("Introductions.txt", sb.ToString());
                    }

                    if (multipleEventsPerUser.Length == 0 && orphanedEntries.Length == 0 && messages.Length == 0)
                    {
                        await ctx.ReplyAsync("All good");
                    }
                }
                finally
                {
                    _birthdayEntries.Exit();
                }
            }
            else if (action == "list")
            {
                Match match = Regex.Match(ctx.ArgumentLines.First(), @"^list (\d\d?[-\.]\d\d?)$");

                if (match.Success && DateTime.TryParse($"{DateTime.UtcNow.Year}-{match.Groups[1].Value}", out DateTime date))
                {
                    if (date < DateTime.UtcNow)
                        date = date.AddYears(1);
                }
                else date = DateTime.UtcNow;

                await SendBirthdaysListAsync(ctx.Channel, date);
            }
            else
            {
                await ctx.ReplyAsync("Usage: `!birthdays [action]`\n" +
                    "teamup / introductions / add / bind / binds / remove / status");
            }
        }

        public static string GetNameFromTitle(Event e)
        {
            string title = e.Title.Trim().Trim('\u2661').Trim();
            int apostrophe = title.IndexOf('\'');
            if (apostrophe != -1)
            {
                title = title.Substring(0, apostrophe);
            }

            return title;
        }

        private async Task SendBirthdaysListAsync(ISocketMessageChannel channel, DateTime date)
        {
            Event[] events = await _teamUpClient.SearchEventsAsync(date, date);
            string message;
            if (events.Any())
            {
                string response = string.Join('\n', events.Select(GetNameFromTitle));
                message = $"Birthdays for {date.ToISODate()}:\n```\n{response}\n```";
            }
            else
            {
                message = $"No birthdays on {date.ToISODate()}";
            }
            await channel.SendMessageAsync(message);
        }

        private DateTime _lastCacheRefresh = DateTime.MinValue;
        private static readonly string MessagesCachePath = $"{Constants.StateDirectory}/IntroductionsMessagesCache.json";

        private static bool TryGetNameAndBirthday(string content, out string name, out string birthday)
        {
            name = birthday = null;

            if (!content.Contains("name", StringComparison.OrdinalIgnoreCase) ||
                !content.Contains("birth", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] lines = content.SplitLines(removeEmpty: true);
            name = lines.First(l => l.Contains("name", StringComparison.OrdinalIgnoreCase));
            birthday = lines.First(l => l.Contains("birth", StringComparison.OrdinalIgnoreCase));

            name = name.Trim().Replace("*", "").Replace("`", "").Replace("_", "");
            birthday = birthday.Trim().Replace("*", "").Replace("`", "").Replace("_", "");

            if (name.StartsWith("name: ", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(6);
            }

            if (birthday.StartsWith("birthday: ", StringComparison.OrdinalIgnoreCase))
            {
                birthday = birthday.Substring(10);
            }

            return true;
        }

        private async Task<SimpleMessageModel[]> LoadMessagesAsync(CommandContext ctx, TimeSpan maxAge)
        {
            SimpleMessageModel[] messages;

            if (DateTime.UtcNow.Subtract(_lastCacheRefresh) < maxAge && File.Exists(MessagesCachePath))
            {
                string json = await File.ReadAllTextAsync(MessagesCachePath);
                messages = JsonConvert.DeserializeObject<SimpleMessageModel[]>(json);
            }
            else
            {
                SocketTextChannel channel = ctx.Discord.GetTextChannel(Channels.DDsIntroductions);

                messages = (await channel.DangerousGetAllMessagesAsync(_logger, $"Birthdays command ran by {ctx.Author.Username}"))
                    .Select(m => SimpleMessageModel.FromMessage(m))
                    .ToArray();

                await File.WriteAllTextAsync(MessagesCachePath, JsonConvert.SerializeObject(messages));
                _lastCacheRefresh = DateTime.UtcNow;
            }

            return messages;
        }

        private static string GetMessageLink(ulong messageId) => GetMessageLink(Guilds.DDs, Channels.DDsIntroductions, messageId);

        private class SimpleMessageModel
        {
            public ulong Id;
            public DateTime TimeStamp;
            public string Content;
            public ulong AuthorId;
            public string AuthorName;
            public bool AuthorIsBot;

            public static SimpleMessageModel FromMessage(IMessage message)
            {
                return new SimpleMessageModel()
                {
                    Id = message.Id,
                    TimeStamp = message.Timestamp.UtcDateTime,
                    Content = message.Content,
                    AuthorId = message.Author.Id,
                    AuthorName = message.Author.Username,
                    AuthorIsBot = message.Author.IsBot
                };
            }
        }

        private class BirthdayEntry
        {
            private const string CalendarPublicKey = "ks6sktk26s43g8c5hw";

            public string Name;
            public DateTime Date;
            public ulong DiscordMessageId;
            public ulong DiscordAuthorId;
            public string TeamUpEventId;

            [JsonIgnore]
            public string EventLink =>
                $"https://teamup.com/{CalendarPublicKey}/events/{TeamUpEventId}-rid-{new DateTimeOffset(Date.Date).ToUnixTimeSeconds()}";
        }
    }
}
