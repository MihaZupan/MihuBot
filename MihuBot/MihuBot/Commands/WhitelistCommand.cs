using Discord;
using MihuBot.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Commands
{
    public sealed class WhitelistCommand : CommandBase
    {
        public override string Command => "whitelist";

        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(3);

        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, string>> _whitelist =
            new SynchronizedLocalJsonStore<Dictionary<ulong, string>>("whitelist.json");

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            var entries = await _whitelist.EnterAsync();

            try
            {
                const string ValidCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

                string args = ctx.ArgumentString;

                if (ctx.IsFromAdmin)
                {
                    if (args.Equals("list", StringComparison.OrdinalIgnoreCase))
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
                            string discordUsername = ctx.Discord.GetUser(userId).Username;

                            return username.PadRight(17, ' ') + discordUsername.Substring(0, Math.Min(discordUsername.Length, 20));
                        }
                    }
                    else if (ctx.Arguments.Length > 1 && ctx.Arguments[0].Equals("remove", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ulong.TryParse(ctx.Arguments[1], out ulong id) && entries.TryGetValue(id, out string username))
                        {
                            entries.Remove(id);
                            await McCommand.RunMinecraftCommandAsync("whitelist remove " + username);
                            await McCommand.RunMinecraftCommandAsync("kick " + username);

                            await ctx.ReplyAsync($"Removed {ctx.Discord.GetUser(id).Username} ({username}) from the whitelist", mention: true);
                        }
                        else
                        {
                            await ctx.ReplyAsync("Can't find a matching whitelist entry", mention: true);
                        }
                        return;
                    }
                }

                if (args.Length < 3 || args.Length > 16 || args.Any(c => !ValidCharacters.Contains(c)))
                {
                    await ctx.ReplyAsync("Enter a valid username: `!whitelist username`", mention: true);
                    return;
                }

                if (!ctx.Author.IsDreamlingsSubscriber(ctx.Discord))
                {
                    ulong guild = ctx.Guild.Id;
                    string info = guild == Guilds.DDs ? MentionUtils.MentionChannel(733694462189895693ul)
                        : guild == Guilds.LiverGang ? MentionUtils.MentionChannel(736348370880299068ul)
                        : guild == Guilds.DresDreamers ? MentionUtils.MentionChannel(733787275313283133ul)
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
    }
}
