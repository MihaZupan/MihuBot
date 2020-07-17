using Discord;
using MihuBot.Helpers;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class WhitelistCommand : CommandBase
    {
        public override string Command => "whitelist";

        private readonly Whitelist _whitelist = new Whitelist("whitelist.json");

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            var entries = await _whitelist.EnterAsync();

            try
            {
                const string ValidCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

                string args = ctx.ArgumentString;

                if (ctx.IsFromAdmin && args.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    Memory<string> entryList = entries.Select(pair => FormatLine(pair.Key, pair.Value)).ToArray();

                    while (!entryList.IsEmpty)
                    {
                        var part = entryList.Slice(0, Math.Min(entryList.Length, 30));
                        entryList = entryList.Slice(part.Length);

                        await ctx.ReplyAsync($"```\n{string.Join('\n', part.ToArray())}\n```");
                    }
                    return;

                    string FormatLine(ulong userId, string username)
                    {
                        string discordUsername = ctx.Client.GetUser(userId).Username;

                        return username.PadRight(17, ' ') + discordUsername.Substring(0, Math.Min(discordUsername.Length, 20));
                    }
                }

                if (args.Length < 3 || args.Length > 16 || args.Any(c => !ValidCharacters.Contains(c)))
                {
                    await ctx.ReplyAsync("Enter a valid username: `!whitelist username`", mention: true);
                    return;
                }

                if (!ctx.Author.IsDreamlingsSubscriber())
                {
                    ulong guild = ctx.Guild.Id;
                    string info = guild == Guilds.DDs ? MentionUtils.MentionChannel(733694462189895693ul)
                        //: guild == Guilds.LiverGang ? MentionUtils.MentionChannel(733694462189895693ul)
                        //: guild == Guilds.DresDreamers ? MentionUtils.MentionChannel(733694462189895693ul)
                        : $"{Emotes.PauseChamp}";

                    await ctx.ReplyAsync($"Sorry, it looks like you're not subscribed to at least one of the Dreamling Gang owners. You can find more information here: {info}", mention: true);
                    await ctx.DebugAsync($"{ctx.Author.Username} tried to add `{args}` to the whitelist in {MentionUtils.MentionChannel(ctx.Channel.Id)} but appears not to be a subscriber");
                    return;
                }

                string existing = null;

                if (entries.Values.Any(v => v.Equals(args, StringComparison.OrdinalIgnoreCase)))
                {
                    ulong takenBy = entries.First(pair => pair.Value.Equals(args, StringComparison.OrdinalIgnoreCase)).Key;
                    if (takenBy == ctx.AuthorId)
                    {
                        await ctx.ReplyAsync("You're already on the whitelist", mention: true);
                    }
                    else
                    {
                        await ctx.ReplyAsync("That username is already taken by " + MentionUtils.MentionUser(takenBy), mention: true);
                    }
                    return;
                }
                else if (entries.TryGetValue(ctx.AuthorId, out existing))
                {
                    entries.Remove(ctx.AuthorId);
                    await McCommand.RunMinecraftCommandAsync("whitelist remove " + existing);
                    await McCommand.RunMinecraftCommandAsync("kick " + existing);
                }
                else existing = null;

                await McCommand.RunMinecraftCommandAsync("whitelist add " + args);

                entries[ctx.AuthorId] = args;

                await ctx.ReplyAsync($"Added {args} to the whitelist" + (existing is null ? "" : $" and removed {existing}"), mention: true);
            }
            catch (Exception ex)
            {
                await ctx.ReplyAsync("Something went wrong :/");
                await ctx.DebugAsync(ex.ToString());
            }
            finally
            {
                _whitelist.Exit();
            }
        }


        private class Whitelist
        {
            private readonly string _whitelistJsonPath;
            private readonly Dictionary<ulong, string> _entries;
            private readonly SemaphoreSlim _asyncLock;

            public Whitelist(string whitelistJsonPath)
            {
                _whitelistJsonPath = whitelistJsonPath;

                _entries = File.Exists(_whitelistJsonPath)
                    ? JsonConvert.DeserializeObject<Dictionary<ulong, string>>(File.ReadAllText(_whitelistJsonPath))
                    : new Dictionary<ulong, string>();

                _asyncLock = new SemaphoreSlim(1, 1);
            }

            public async Task<Dictionary<ulong, string>> EnterAsync()
            {
                await _asyncLock.WaitAsync();
                return _entries;
            }

            public void Exit()
            {
                File.WriteAllText(_whitelistJsonPath, JsonConvert.SerializeObject(_entries, Formatting.Indented));
                _asyncLock.Release();
            }
        }
    }
}
