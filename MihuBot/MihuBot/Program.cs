using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
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

        private static readonly Emote YesW          = Emote.Parse("<:yesW:569000155513225220>");
        private static readonly Emote PudeesJammies = Emote.Parse("<:pudeesJammies:686340394866573338>");
        private static readonly Emote DarlBoop      = Emote.Parse("<:darlBoop:712494064087597106>");
        private static readonly Emote DarlHearts    = Emote.Parse("<a:darlHearts:712496083334463528>");
        private static readonly Emote DarlHug       = Emote.Parse("<:darlHug:712494106466844723>");
        private static readonly Emote DarlKiss      = Emote.Parse("<:darlKiss:712494206308057248>");
        private static readonly Emote DarlBASS      = Emote.Parse("<a:darlBASS:560235665040867328>");
        private static readonly Emote CreepyFace    = Emote.Parse("<:creepyface:708818227446284369>");
        private static readonly Emote MonkaHmm      = Emote.Parse("<:monkaHmm:712494625390198856>");
        private static readonly Emote Monkers       = Emote.Parse("<:MONKERS:715472497499176981>");
        private static readonly Emote MonkaEZ       = Emote.Parse("<:EZ:712494500731158549>");
        private static readonly Emote DarlPoke      = Emote.Parse("<:darlPoke:591174254372978689>");
        private static readonly Emote DarlZoom      = Emote.Parse("<a:darlZoom:574377475115581440>");

        private static readonly Emote[] JamesEmotes = new Emote[]
        {
            Emote.Parse("<:james:685588058757791744>"),
            Emote.Parse("<:james:685588122939031569>"),
            Emote.Parse("<:james:694013377655209984>"),
            Emote.Parse("<:james:694013479622803476>"),
            Emote.Parse("<:james:694013490058362921>"),
            Emote.Parse("<:james:694013499377975356>"),
            Emote.Parse("<:james:694013521033297981>"),
            Emote.Parse("<:james:694013527660167229>"),
            Emote.Parse("<:james:694013534878826526>")
        };

        private static readonly string[] BananaMessages = new string[]
        {
            "banana", "ba na na", "b a n a n a"
        };

        private static readonly string[] TypingResponseWords = new string[]
        {
            "cock", "penis"
        };

        private static readonly HashSet<ulong> GuildIDs = new HashSet<ulong>()
        {
            350658308878630914ul, // DD
            566925785563136020ul, // Mihu
            715374946846769202ul, // Paul's Peepo People
            //572822995102597120ul, // Ramen House
        };
        private static readonly HashSet<ulong> Admins = new HashSet<ulong>()
        {
            162569877087977480ul, // Mihu
            340562834658295821ul, // Caroline
            91680709588045824ul,  // James
            145719024544645120ul, // Jaeger
            236455327535464458ul, // Jordan
            238754130233917440ul, // Doomi
            237788815626862593ul, // Curt
            397254656025427968ul, // Christian
            244637014547234816ul, // Liv
            267771172962304000ul, // PaulK
            399032007138476032ul, // Maric
        };

        private const ulong MihaID = 162569877087977480ul;
        private const ulong GradravinID = 235218831247671297ul;
        private const ulong JamesID = 91680709588045824ul;
        private const ulong ChristianID = 397254656025427968ul;
        private const ulong CurtIsID = 237788815626862593ul;
        private const ulong JordanID = 236455327535464458ul;

        private const ulong MihuBotID = 710370560596770856ul;

        private static readonly TaskCompletionSource<object> BotStopTCS =
            new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        static async Task Main(string[] args)
        {
            Directory.CreateDirectory(LogsRoot);
            Directory.CreateDirectory(FilesRoot);

            LogWriter = new StreamWriter(LogsRoot + DateTime.UtcNow.Ticks + ".txt");

            Client = new DiscordSocketClient(new DiscordSocketConfig() { MessageCacheSize = 1024 * 8 });

            Client.MessageReceived += Client_MessageReceived;
            Client.ReactionAdded += Client_ReactionAdded;
            Client.MessageUpdated += Client_MessageUpdated;

            //await Client.LoginAsync(0, "***REMOVED***");
            await Client.LoginAsync(TokenType.Bot, "***REMOVED***");
            await Client.StartAsync();

            await Client.SetGameAsync("Beeping and booping", type: ActivityType.Listening);

            if (args.Length > 0 && args[0].StartsWith("Update-"))
            {
                string[] parts = args[0].Split('-');
                if (parts.Length == 4 &&
                    ulong.TryParse(parts[1], out ulong guildId) &&
                    ulong.TryParse(parts[2], out ulong channelId) &&
                    ulong.TryParse(parts[3], out ulong userId))
                {
                    try
                    {
                        var channel = Client.GetGuild(guildId).GetChannel(channelId) as ISocketMessageChannel;
                        await channel.SendMessageAsync($"{MentionUtils.MentionUser(userId)} I am back {DarlBoop}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                }
            }

            await BotStopTCS.Task;
            Environment.ExitCode = 1;
        }

        private static async Task Client_MessageUpdated(Cacheable<IMessage, ulong> _, SocketMessage message, ISocketMessageChannel channel)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            try
            {
                if (!(userMessage.Channel is SocketGuildChannel guildChannel) || !GuildIDs.Contains(guildChannel.Guild.Id))
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
                if (!(reaction.Channel is SocketGuildChannel guildChannel) || !GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                if (reaction.Emote.Name.Equals("yesw", StringComparison.OrdinalIgnoreCase))
                {
                    if (reaction.User.IsSpecified && reaction.User.Value.Id == MihuBotID)
                        return;

                    var userMessage = reaction.Message;

                    if (userMessage.IsSpecified)
                    {
                        await userMessage.Value.AddReactionAsync(YesW);
                    }
                }
                else if (reaction.Emote is Emote reactionEmote)
                {
                    if (reactionEmote.Id == 685587814330794040ul) // James emote
                    {
                        var userMessage = reaction.Message;

                        if (userMessage.IsSpecified)
                        {
                            foreach (var emote in JamesEmotes)
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
                if (!(userMessage.Channel is SocketGuildChannel guildChannel) || !GuildIDs.Contains(guildChannel.Guild.Id))
                    return;

                SocketGuild guild = guildChannel.Guild;

                bool isAdmin = Admins.Contains(message.Author.Id);
                bool isMentioned = message.MentionedUsers.Any(u => u.Id == MihuBotID);
                string content = message.Content.Trim();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    await LogAsync(message.Channel.Name + "_" + message.Author.Username + ": " + content);
                }

                if (message.Author.Id != MihuBotID && message.Attachments.Any())
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
                                    var channel = Client.GetGuild(566925785563136020ul).GetTextChannel(715997637978882110ul);
                                    await channel.SendMessageAsync($"Space available: {(int)(drive.AvailableFreeSpace / 1024 / 1024)} MB");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }
                    })).ToArray());
                }

                if (message.Author.Id == GradravinID && RngChance(1000))
                {
                    await userMessage.AddReactionAsync(DarlBoop);
                }

                if (message.Author.Id == JamesID && RngChance(50))
                {
                    await userMessage.AddReactionAsync(CreepyFace);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(750);
                        await userMessage.RemoveReactionAsync(CreepyFace, MihuBotID);
                    });
                }

                if (message.Author.Id != MihuBotID)
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

                if (isAdmin && content.StartsWith("@husband", StringComparison.OrdinalIgnoreCase) &&
                    (content.Length == 8 || (content.Length == 9 && (content[8] | 0x20) == 'o')) && message.Author.Id != JordanID)
                {
                    await message.ReplyAsync(MentionUtils.MentionUser(JordanID));
                }

                if (content.Contains("yesw", StringComparison.OrdinalIgnoreCase) && message.Author.Id != MihuBotID)
                {
                    await userMessage.AddReactionAsync(YesW);
                }
                
                if (BananaMessages.Any(b => content.Contains(b, StringComparison.OrdinalIgnoreCase)))
                {
                    await userMessage.AddReactionAsync(PudeesJammies);
                }

                if (isMentioned)
                {
                    if (content.Contains("youtu", StringComparison.OrdinalIgnoreCase))
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
                    else if (isAdmin && content.Contains('\n') && content.AsSpan(0, 40).Contains("msg ", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendCustomMessage(content, message);
                    }
                }
                
                if (content.StartsWith('/') || content.StartsWith('!'))
                {
                    string[] parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string command = parts[0].Substring(1).ToLowerInvariant();

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
                        await message.ReplyAsync("I listen to:\n" + string.Join(", ", guild.Users.Where(u => Admins.Contains(u.Id)).Select(a => a.Username)));
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
                            reply = $"{message.Author.Username} thinks {(targetIsAuthor ? "they have" : $"{target} has")} a nice butt! {DarlBASS}";
                        }
                        else if (command == "slap")
                        {
                            reply = $"{message.Author.Username} just {(targetIsAuthor ? "performed a self-slap maneuver" : $"slapped {target}")}! {MonkaHmm}";
                        }
                        else if (command == "kick")
                        {
                            reply = $"{message.Author.Username} just {(targetIsAuthor ? "tripped" : $"kicked {target}")}! {DarlZoom}";
                        }
                        else if (command == "fist")
                        {
                            if (message.Author.Id == CurtIsID && rngUser.Id == MihaID)
                            {
                                reply = $"{Monkers}";
                            }
                            else
                            {
                                reply = $"{message.Author.Username} just ||{(targetIsAuthor ? "did unimaginable things" : $"fis.. punched {target}")}!|| {Monkers}";
                            }
                        }
                        else if (command == "love")
                        {
                            reply = $"{message.Author.Username} wants {target} to know they are loved! {DarlHearts}";
                        }
                        else if (command == "hug")
                        {
                            reply = $"{message.Author.Username} is {(targetIsAuthor ? "getting hugged" : $"sending hugs to {target}")}! {DarlHug}";
                        }
                        else if (command == "kiss")
                        {
                            reply = $"{message.Author.Username} just kissed {target}! {DarlKiss}";
                        }
                        else if (command == "stab")
                        {
                            reply = $"{message.Author.Username} just stabbed {target}! {DarlPoke}";
                        }
                        else if (command == "boop")
                        {
                            reply = $"{target} {DarlBoop}";
                        }
                        else throw new InvalidOperationException("Unknown commmand");

                        await message.ReplyAsync(reply);
                    }
                    else if (!isAdmin && (command == "fist" || command == "stab"))
                    {
                        await message.ReplyAsync($"No {MonkaEZ}", mention: true);
                    }
                    else if (isAdmin && isMentioned && command == "update")
                    {
                        _ = Task.Run(async () => await StartUpdateAsync(message));
                    }
                    else if (message.Author.Id == MihaID && isMentioned && command == "stop")
                    {
                        await message.ReplyAsync("Stopping ...");
                        BotStopTCS.TrySetResult(null);
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

                if (TypingResponseWords.Contains(content.AsSpan(space + 1, next - space - 1), StringComparison.OrdinalIgnoreCase))
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

            if (!ulong.TryParse(headers[2], out ulong guildId) || !GuildIDs.Contains(guildId))
            {
                string guilds = string.Join('\n', GuildIDs.Select(id => id + ": " + Client.GetGuild(id).Name));
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



        private static int _updating = 0;

        private static async Task StartUpdateAsync(SocketMessage message)
        {
            Debug.Assert(Admins.Contains(message.Author.Id));
            Debug.Assert(message.Content.Contains("update", StringComparison.OrdinalIgnoreCase));

            if (Interlocked.Exchange(ref _updating, 1) != 0)
                return;

            await message.ReplyAsync("Updating ...");
            await Client.StopAsync();

            using Process updateProcess = new Process();
            updateProcess.StartInfo.FileName = "/bin/bash";
            updateProcess.StartInfo.Arguments = $"/home/miha/MihuBot/MihuBot/update.sh \"Update-{message.Guild().Id}-{message.Channel.Id}-{message.Author.Id}\"";
            updateProcess.StartInfo.UseShellExecute = false;
            updateProcess.StartInfo.WorkingDirectory = $"\"{Environment.CurrentDirectory}\"";
            updateProcess.Start();

            BotStopTCS.TrySetResult(null);
        }
    }
}
