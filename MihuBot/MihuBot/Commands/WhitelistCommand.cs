﻿using Discord;
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

                if (args.Length < 3 || args.Length > 16 || args.Any(c => !ValidCharacters.Contains(c)))
                {
                    await ctx.ReplyAsync("Enter a valid username: `!whitelist username`", mention: true);
                    return;
                }

                if (ctx.IsFromAdmin && args.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    string entryList = string.Join('\n', entries.Select(pair => FormatLine(pair.Key, pair.Value)));

                    await ctx.ReplyAsync($"```\n{entryList}\n```");
                    return;

                    string FormatLine(ulong userId, string username)
                    {
                        string discordUsername = ctx.Client.GetUser(userId).Username;

                        if (discordUsername.Length > 20)
                            discordUsername = discordUsername.Substring(0, 20);

                        return (discordUsername + ":").PadRight(22) + username;
                    }
                }

                if (!ctx.Author.IsDreamlingsSubscriber())
                {
                    await ctx.ReplyAsync("Not a sub to the Dreamlings gang", mention: true);
                    return;
                }

                if (entries.TryGetValue(ctx.AuthorId, out string existing))
                {
                    if (existing.Equals(args, StringComparison.OrdinalIgnoreCase))
                    {
                        await ctx.ReplyAsync("You're already on the whitelist", mention: true);
                        return;
                    }

                    entries.Remove(ctx.AuthorId);
                    await McCommand.RunMinecraftCommandAsync("whitelist remove " + existing);
                    await McCommand.RunMinecraftCommandAsync("kick " + existing);
                }
                else if (entries.Values.Any(v => v.Equals(args, StringComparison.OrdinalIgnoreCase)))
                {
                    ulong takenBy = entries.First(pair => pair.Value.Equals(args, StringComparison.OrdinalIgnoreCase)).Key;
                    await ctx.ReplyAsync("That username is already taken by " + MentionUtils.MentionUser(takenBy), mention: true);
                    return;
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