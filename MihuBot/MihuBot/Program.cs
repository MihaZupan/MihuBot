using Discord;
using Discord.WebSocket;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot
{
    class Program
    {
        private static DiscordSocketClient Client;
        private static readonly HttpClient HttpClient = new HttpClient();

        private static readonly string LogsRoot = "logs/";
        private static readonly string FilesRoot = LogsRoot + "files/";

        private static int _fileCounter = 0;
        private static readonly SemaphoreSlim LogSemaphore = new SemaphoreSlim(1, 1);
        private static StreamWriter LogWriter;

        private static async Task LogAsync(string content)
        {
            await LogSemaphore.WaitAsync();
            await LogWriter.WriteLineAsync(DateTime.UtcNow.Ticks + "_" + content);
            await LogWriter.FlushAsync();
            LogSemaphore.Release();
        }

        private static readonly TaskCompletionSource<object> BotStopTCS =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        private static SocketTextChannel DebugTextChannel => Client.GetGuild(566925785563136020ul).GetTextChannel(719903263297896538ul);
        internal static async Task DebugAsync(string message)
        {
            lock (Console.Out)
                Console.WriteLine("DEBUG: " + message);

            try
            {
                await DebugTextChannel.SendMessageAsync(message);
            }
            catch { }
        }

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "start")
            {
                await StartUpdateAsync(null);
                return;
            }

            Directory.CreateDirectory(LogsRoot);
            Directory.CreateDirectory(FilesRoot);

            LogWriter = new StreamWriter(LogsRoot + DateTime.UtcNow.Ticks + ".txt");

            Client = new DiscordSocketClient(
                new DiscordSocketConfig() {
                    MessageCacheSize = 1024 * 16,
                    ConnectionTimeout = 30_000
            });

            TaskCompletionSource<object> onConnectedTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Client.Connected += () => { onConnectedTcs.TrySetResult(null); return Task.CompletedTask; };

            TaskCompletionSource<object> onReadyTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            Client.Ready += () => { onReadyTcs.TrySetResult(null); return Task.CompletedTask; };

            Client.MessageReceived += Client_MessageReceived;
            Client.ReactionAdded += Client_ReactionAdded;
            Client.MessageUpdated += Client_MessageUpdated;

            Client.JoinedGuild += Client_JoinedGuild;

            await Client.LoginAsync(TokenType.Bot, Secrets.AuthToken);
            await Client.StartAsync();

            await onConnectedTcs.Task;
            await onReadyTcs.Task;

            await DebugAsync("Beep boop. I'm back!");

            await Client.SetGameAsync("Quality content", type: ActivityType.Streaming);

            await BotStopTCS.Task;

            try
            {
                await Client.StopAsync();
            }
            catch { }
        }

        private static async Task Client_JoinedGuild(SocketGuild guild)
        {
            await DebugAsync($"Added to {guild.Name} ({guild.Id})");
        }

        private static async Task Client_MessageUpdated(Cacheable<IMessage, ulong> _, SocketMessage message, ISocketMessageChannel channel)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            try
            {
                if (!(userMessage.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    await LogAsync(message.Channel.Name + "_Updated_" + message.Author.Username + ": " + message.Content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static async Task Client_ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            try
            {
                if (!(reaction.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                if (reaction.Emote.Name.Equals("yesw", StringComparison.OrdinalIgnoreCase))
                {
                    if (reaction.User.IsSpecified && reaction.User.Value.Id == KnownUsers.MihuBot)
                        return;

                    var userMessage = reaction.Message;

                    if (userMessage.IsSpecified)
                    {
                        await userMessage.Value.AddReactionAsync(Emotes.YesW);
                    }
                }
                else if (reaction.Emote is Emote reactionEmote)
                {
                    if (reactionEmote.Id == 685587814330794040ul) // James emote
                    {
                        var userMessage = reaction.Message;

                        if (userMessage.IsSpecified)
                        {
                            foreach (var emote in Emotes.JamesEmotes)
                            {
                                await userMessage.Value.AddReactionAsync(emote);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static async Task Client_MessageReceived(SocketMessage message)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            try
            {
                if (!(userMessage.Channel is SocketGuildChannel guildChannel) || !Constants.GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                SocketGuild guild = guildChannel.Guild;

                bool isAdmin = Constants.Admins.Contains(message.Author.Id) || (Constants.GuildMods.TryGetValue(guild.Id, out var guildMods) && guildMods.Contains(message.Author.Id));
                bool isMentioned = message.MentionedUsers.Any(u => u.Id == KnownUsers.MihuBot);
                string content = message.Content.Trim();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    await LogAsync(message.Channel.Name + "_" + message.Author.Username + ": " + content);
                }

                if (message.Author.Id != KnownUsers.MihuBot && message.Attachments.Any())
                {
                    await Task.WhenAll(message.Attachments.Select(a => Task.Run(async () => {
                        try
                        {
                            var response = await HttpClient.GetAsync(a.Url, HttpCompletionOption.ResponseHeadersRead);

                            using FileStream fs = File.OpenWrite(FilesRoot + a.Id + "_" + a.Filename);
                            using Stream stream = await response.Content.ReadAsStreamAsync();
                            await stream.CopyToAsync(fs);

                            if (Interlocked.Increment(ref _fileCounter) % 10 == 0)
                            {
                                var drive = DriveInfo.GetDrives().Where(d => d.TotalSize > 16 * 1024 * 1024 * 1024L /* 16 GB */).Single();
                                if (drive.AvailableFreeSpace < 16 * 1024 * 1024 * 1024L)
                                {
                                    await DebugAsync($"Space available: {(int)(drive.AvailableFreeSpace / 1024 / 1024)} MB");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    })).ToArray());
                }

                if (message.Author.Id == KnownUsers.Gradravin && RngChance(1000))
                {
                    await userMessage.AddReactionAsync(Emotes.DarlBoop);
                }

                if (message.Author.Id == KnownUsers.James && RngChance(50))
                {
                    await userMessage.AddReactionAsync(Emotes.CreepyFace);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(750);
                        await userMessage.RemoveReactionAsync(Emotes.CreepyFace, KnownUsers.MihuBot);
                    });
                }

                if (message.Author.Id != KnownUsers.MihuBot)
                {
                    if (message.Content.Equals("uwu", StringComparison.OrdinalIgnoreCase))
                    {
                        await message.ReplyAsync("OwO");
                    }
                    else if (message.Content.Equals("owo", StringComparison.OrdinalIgnoreCase))
                    {
                        await message.ReplyAsync("UwU");
                    }
                    else if (message.Content.Equals("beep", StringComparison.OrdinalIgnoreCase))
                    {
                        await message.ReplyAsync("Boop");
                    }
                    else if (message.Content.Equals("boop", StringComparison.OrdinalIgnoreCase))
                    {
                        await message.ReplyAsync("Beep");
                    }
                }

                if (content.StartsWith("@husband", StringComparison.OrdinalIgnoreCase) &&
                    (content.Length == 8 || (content.Length == 9 && (content[8] | 0x20) == 'o')))
                {
                    ulong husbandId = message.Author.Id switch
                    {
                        KnownUsers.Miha     => KnownUsers.Jordan,
                        KnownUsers.Jordan   => KnownUsers.Miha,

                        _ => 0
                    };

                    if (husbandId == 0)
                    {
                        await message.ReplyAsync($"{Emotes.DarlF}");
                    }
                    else
                    {
                        await message.ReplyAsync(MentionUtils.MentionUser(husbandId));
                    }
                }

                if (content.Contains("yesw", StringComparison.OrdinalIgnoreCase) && message.Author.Id != KnownUsers.MihuBot)
                {
                    await userMessage.AddReactionAsync(Emotes.YesW);
                }
                
                if (Constants.BananaMessages.Any(b => content.Contains(b, StringComparison.OrdinalIgnoreCase)))
                {
                    await userMessage.AddReactionAsync(Emotes.PudeesJammies);
                }

                if (isMentioned)
                {
                    if (isAdmin && content.Contains('\n') && content.AsSpan(0, 40).Contains("msg ", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendCustomMessage(content, message);
                    }
                    else if (content.Contains(" play ", StringComparison.OrdinalIgnoreCase))
                    {
                        await OnPlayCommand(message);
                    }
                    else if (content.Contains("youtu", StringComparison.OrdinalIgnoreCase))
                    {
                        if (YoutubeHelper.TryParseVideoId(content, out string videoId))
                        {
                            _ = Task.Run(async () => await YoutubeHelper.SendVideoAsync(videoId, message.Channel));
                        }
                        else if (YoutubeHelper.TryParsePlaylistId(content, out string playlistId))
                        {
                            _ = Task.Run(async () => await YoutubeHelper.SendPlaylistAsync(playlistId, message.Channel));
                        }
                    }
                }
                
                if (content.StartsWith('/') || content.StartsWith('!'))
                {
                    string[] parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string command = parts[0].Substring(1).ToLowerInvariant();
                    string arguments = content.Substring(parts[0].Length).Trim();

                    if (command == "roll")
                    {
                        BigInteger sides = 6;

                        if (parts.Length > 1 && BigInteger.TryParse(parts[1].Trim('d', 'D'), out BigInteger customSides))
                            sides = customSides;

                        string response;

                        if (sides <= 0)
                        {
                            response = "No";
                        }
                        else if (sides <= 1_000_000_000)
                        {
                            response = (Rng((int)sides) + 1).ToString();
                        }
                        else
                        {
                            double log10 = BigInteger.Log10(sides);
                            if (log10 >= 64 * 1024)
                            {
                                response = "42";
                            }
                            else
                            {
                                byte[] bytes = new byte[(int)(log10 * 4)];
                                new Random().NextBytes(bytes);
                                BigInteger number = new BigInteger(bytes, true);

                                response = (number % sides).ToString();
                            }
                        }

                        await message.ReplyAsync(response, mention: true);
                    }
                    else if (command == "flip")
                    {
                        if (parts.Length > 1 && int.TryParse(parts[1], out int count) && count > 0)
                        {
                            count = Math.Min(1_000_000, count);
                            int heads = FlipCoins(count);
                            await message.ReplyAsync($"Heads: {heads}, Tails {count - heads}", mention: true);
                        }
                        else
                        {
                            await message.ReplyAsync(RngBool() ? "Heads" : "Tails", mention: true);
                        }
                    }
                    else if (command == "dl")
                    {
                        SocketMessage msg = message.Channel
                            .GetCachedMessages(10)
                            .OrderByDescending(m => m.Timestamp)
                            .FirstOrDefault(m => m.Content.Contains("youtu", StringComparison.OrdinalIgnoreCase) && YoutubeHelper.TryParseVideoId(m.Content, out _));

                        if (msg != null && YoutubeHelper.TryParseVideoId(msg.Content, out string videoId))
                        {
                            _ = Task.Run(async () => await YoutubeHelper.SendVideoAsync(videoId, message.Channel));
                        }
                    }
                    else if (command == "admins")
                    {
                        await message.ReplyAsync("I listen to:\n" + string.Join(", ", guild.Users.Where(u => Constants.Admins.Contains(u.Id)).Select(a => a.Username)));
                    }
                    else if (command == "butt" || command == "slap" || command == "kick"
                        || command == "love" || command == "hug" || command == "kiss" || command == "boop"
                        || (isAdmin && (command == "fist" || command == "stab")))
                    {
                        bool at = isAdmin && parts.Length > 1 && parts[^1].Equals("at", StringComparison.OrdinalIgnoreCase);

                        IUser rngUser = null;

                        if (parts.Length > 1)
                        {
                            if (message.MentionedUsers.SingleOrDefault() != null && parts[1].StartsWith("<@") && parts[1].EndsWith('>'))
                            {
                                rngUser = message.MentionedUsers.Single();
                                at |= isAdmin;
                            }
                            else if (ulong.TryParse(parts[1], out ulong userId))
                            {
                                rngUser = await message.Channel.GetUserAsync(userId);
                            }
                        }

                        if (rngUser is null)
                            rngUser = await GetRandomChannelUserAsync(message.Channel);

                        string target = at ? MentionUtils.MentionUser(rngUser.Id) : rngUser.Username;

                        bool targetIsAuthor = rngUser.Id == message.Author.Id;

                        string reply;

                        if (command == "butt")
                        {
                            reply = $"{message.Author.Username} thinks {(targetIsAuthor ? "they have" : $"{target} has")} a nice butt! {Emotes.DarlBASS}";
                        }
                        else if (command == "slap")
                        {
                            reply = $"{message.Author.Username} just {(targetIsAuthor ? "performed a self-slap maneuver" : $"slapped {target}")}! {Emotes.MonkaHmm}";
                        }
                        else if (command == "kick")
                        {
                            reply = $"{message.Author.Username} just {(targetIsAuthor ? "tripped" : $"kicked {target}")}! {Emotes.DarlZoom}";
                        }
                        else if (command == "fist")
                        {
                            if (message.Author.Id == KnownUsers.CurtIs && rngUser.Id == KnownUsers.Miha)
                            {
                                reply = $"{Emotes.Monkers}";
                            }
                            else
                            {
                                reply = $"{message.Author.Username} just ||{(targetIsAuthor ? "did unimaginable things" : $"fis.. punched {target}")}!|| {Emotes.Monkers}";
                            }
                        }
                        else if (command == "love")
                        {
                            reply = $"{message.Author.Username} wants {target} to know they are loved! {Emotes.DarlHearts}";
                        }
                        else if (command == "hug")
                        {
                            reply = $"{message.Author.Username} is {(targetIsAuthor ? "getting hugged" : $"sending hugs to {target}")}! {Emotes.DarlHug}";
                        }
                        else if (command == "kiss")
                        {
                            reply = $"{message.Author.Username} just kissed {target}! {Emotes.DarlKiss}";
                        }
                        else if (command == "stab")
                        {
                            reply = $"{message.Author.Username} just stabbed {target}! Emotes.{Emotes.DarlPoke}";
                        }
                        else if (command == "boop")
                        {
                            reply = $"{target} {Emotes.DarlBoop}";
                        }
                        else throw new InvalidOperationException("Unknown commmand");

                        await message.ReplyAsync(reply);
                    }
                    else if (!isAdmin && (command == "fist" || command == "stab"))
                    {
                        await message.ReplyAsync($"No {Emotes.MonkaEZ}", mention: true);
                    }
                    else if (isAdmin && isMentioned && command == "update")
                    {
                        _ = Task.Run(async () => await StartUpdateAsync(message));
                    }
                    else if (message.Author.Id == KnownUsers.Miha && isMentioned && command == "stop")
                    {
                        await message.ReplyAsync("Stopping ...");
                        BotStopTCS.TrySetResult(null);
                    }
                    else if (isAdmin && command == "setplaying")
                    {
                        await Client.SetGameAsync(arguments, type: ActivityType.Playing);
                    }
                    else if (isAdmin && command == "setlistening")
                    {
                        await Client.SetGameAsync(arguments, type: ActivityType.Listening);
                    }
                    else if (isAdmin && command == "setwatching")
                    {
                        await Client.SetGameAsync(arguments, type: ActivityType.Watching);
                    }
                    else if (isAdmin && command == "setstreaming")
                    {
                        var split = arguments.Split(';');
                        string name = split[0];
                        string streamUrl = split.Length > 1 ? split[1] : null;
                        await Client.SetGameAsync(name, streamUrl, type: ActivityType.Streaming);
                    }
                }
                else
                {
                    await ParseWords(content, message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private static Task ParseWords(string content, SocketMessage message)
        {
            int space = -1;
            do
            {
                int next = content.IndexOf(' ', space + 1);
                if (next == -1)
                    next = content.Length;

                if (Constants.TypingResponseWords.Contains(content.AsSpan(space + 1, next - space - 1), StringComparison.OrdinalIgnoreCase))
                {
                    return message.Channel.TriggerTypingAsync();
                }

                space = next;
            }
            while (space + 1 < content.Length);

            return Task.CompletedTask;
        }

        private static async Task SendCustomMessage(string commandMessage, SocketMessage message)
        {
            string[] lines = commandMessage.Split('\n');
            string[] headers = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (headers.Length < 4)
            {
                await message.ReplyAsync("Missing command arguments");
                return;
            }

            if (!headers[1].Equals("msg", StringComparison.OrdinalIgnoreCase))
                return;

            if (!ulong.TryParse(headers[2], out ulong guildId) || !Constants.GuildIDs.Contains(guildId))
            {
                string guilds = string.Join('\n', Constants.GuildIDs.Select(id => id + ": " + Client.GetGuild(id).Name));
                await message.ReplyAsync("Invalid Guild ID. Try:\n```\n" + guilds + "\n```");
                return;
            }

            if (!ulong.TryParse(headers[3], out ulong channelId))
            {
                await message.ReplyAsync("Invalid channel ID format");
                return;
            }

            SocketGuild guild = Client.GetGuild(guildId);

            if (!(guild.TextChannels.FirstOrDefault(c => c.Id == channelId) is SocketTextChannel channel))
            {
                string channels = string.Join('\n', guild.Channels.Select(c => c.Id + ":\t" + c.Name));
                if (channels.Length > 500)
                {
                    channels = string.Concat(channels.AsSpan(0, channels.AsSpan(0, 496).LastIndexOf('\n')), " ...");
                }

                await message.ReplyAsync("Unknown channel. Try:\n```\n" + channels + "\n```");
                return;
            }

            try
            {
                if (lines[1].AsSpan().TrimStart().StartsWith('{') && lines[^1].AsSpan().TrimEnd().EndsWith('}'))
                {
                    int first = commandMessage.IndexOf('{');
                    int last = commandMessage.LastIndexOf('}');
                    await EmbedHelper.SendEmbedAsync(commandMessage.Substring(first, last - first + 1), channel);
                }
                else
                {
                    await channel.SendMessageAsync(commandMessage.AsSpan(lines[0].Length + 1).Trim(stackalloc char[] { ' ', '\t', '\r', '\n' }).ToString());
                }
            }
            catch (Exception ex)
            {
                await message.ReplyAsync(ex.Message, mention: true);
                throw;
            }
        }

        private static readonly Random _rng = new Random();

        private static bool RngChance(int oneInX)
        {
            return Rng(oneInX) == 0;
        }

        private static int Rng(int mod)
        {
            Span<byte> buffer = stackalloc byte[16];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
            var number = new BigInteger(buffer, isUnsigned: true);

            return (int)(number % mod);
        }

        private static bool RngBool()
        {
            Span<byte> buffer = stackalloc byte[1];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
            return (buffer[0] & 1) == 0;
        }

        private static async Task<IUser> GetRandomChannelUserAsync(ISocketMessageChannel channel)
        {
            Stopwatch timer = Stopwatch.StartNew();
            var userLists = await channel.GetUsersAsync(CacheMode.AllowDownload).ToArrayAsync();
            var users = userLists.SelectMany(i => i).ToArray();
            var rngUser = users[Rng(users.Length)];
            timer.Stop();

            Console.WriteLine($"Fetching users took {timer.ElapsedMilliseconds} ms");

            return rngUser;
        }

        private static int FlipCoins(int count)
        {
            const int StackallocSize = 4096;
            const int SizeAsUlong = StackallocSize / 8;

            int remaining = count;
            int heads = 0;

            Span<byte> memory = stackalloc byte[StackallocSize];

            while (remaining >= 64)
            {
                System.Security.Cryptography.RandomNumberGenerator.Fill(memory);

                Span<ulong> memoryAsLongs = MemoryMarshal.CreateSpan(
                    ref Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(memory)),
                    Math.Min(SizeAsUlong, remaining >> 6));

                foreach (ulong e in memoryAsLongs)
                    heads += BitOperations.PopCount(e);

                remaining -= memoryAsLongs.Length << 6;
            }

            System.Security.Cryptography.RandomNumberGenerator.Fill(memory.Slice(0, remaining));
            foreach (byte b in memory.Slice(0, remaining))
                heads += b & 1;

            return heads;
        }

        private static async Task OnPlayCommand(SocketMessage message)
        {
            var guild = message.Guild();
            var vc = guild.VoiceChannels.FirstOrDefault(vc => vc.Users.Any(u => u.Id == message.Author.Id));

            AudioClient audioClient = null;

            try
            {
                audioClient = await AudioClient.TryGetOrJoinAsync(guild, vc);
            }
            catch (Exception ex)
            {
                await DebugAsync(ex.ToString());
            }

            if (audioClient is null)
            {
                if (vc is null)
                {
                    await message.ReplyAsync("Join a VC first", mention: true);
                }
                else
                {
                    await message.ReplyAsync("Could not join channel", mention: true);
                }

                return;
            }

            await audioClient.TryQueueContentAsync(message);
        }


        private static int _updating = 0;

        private static async Task StartUpdateAsync(SocketMessage message)
        {
            if (message != null)
            {
                Debug.Assert(Constants.Admins.Contains(message.Author.Id));
                Debug.Assert(message.Content.Contains("update", StringComparison.OrdinalIgnoreCase));

                if (Interlocked.Exchange(ref _updating, 1) != 0)
                    return;

                await Task.WhenAll(
                    message.Guild().Id == Guilds.Mihu ? Task.CompletedTask : message.ReplyAsync("Updating ..."),
                    DebugAsync("Updating ..."));

                await Client.StopAsync();
            }

            using Process updateProcess = new Process();
            updateProcess.StartInfo.FileName = "/bin/bash";
            updateProcess.StartInfo.Arguments = "update.sh";
            updateProcess.StartInfo.UseShellExecute = false;
            updateProcess.StartInfo.WorkingDirectory = Environment.CurrentDirectory;
            updateProcess.Start();

            BotStopTCS.TrySetResult(null);
        }
    }
}
