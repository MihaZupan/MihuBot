namespace MihuBot.Commands;

public sealed class WhitelistCommand : CommandBase
{
    public override string Command => "whitelist";

    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(5);
    protected override int CooldownToleranceCount => 3;

    private readonly SynchronizedLocalJsonStore<Dictionary<ulong, string>> _whitelist = new("whitelist-bush-nation.json");
    private readonly MinecraftRCON _rcon;

    public WhitelistCommand(MinecraftRCON rcon)
    {
        _rcon = rcon ?? throw new ArgumentNullException(nameof(rcon));
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        if (ctx.Guild.Id != Guilds.BushNation && !ctx.IsFromAdmin)
        {
            return;
        }

        var entries = await _whitelist.EnterAsync();
        try
        {
            const string ValidCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

            string args = ctx.ArgumentString;

            if (args.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                if (!await ctx.RequirePermissionAsync("whitelist.list"))
                    return;

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
                    string discordUsername = ctx.Discord.GetUser(userId)?.GetName() ?? userId.ToString();

                    return username.PadRight(17, ' ') + discordUsername.Substring(0, Math.Min(discordUsername.Length, 20));
                }
            }
            else if (ctx.Arguments.Length > 1 && ctx.Arguments[0].Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                if (!await ctx.RequirePermissionAsync("whitelist.remove"))
                    return;

                if (ulong.TryParse(ctx.Arguments[1], out ulong id) && entries.TryGetValue(id, out string username))
                {
                    await _rcon.SendCommandAsync("whitelist remove " + username);
                    await _rcon.SendCommandAsync("kick " + username);

                    entries.Remove(id);

                    await ctx.ReplyAsync($"Removed {ctx.Discord.GetUser(id)?.GetName() ?? id.ToString()} ({username}) from the whitelist", mention: true);
                }
                else
                {
                    await ctx.ReplyAsync("Can't find a matching whitelist entry", mention: true);
                }
                return;
            }

            if (args.Length < 3 || args.Length > 16 || args.Any(c => !ValidCharacters.Contains(c)))
            {
                await ctx.ReplyAsync("Enter a valid username: `!whitelist username`", mention: true);
                return;
            }

            if (!ctx.IsFromAdmin &&
                !ctx.Author.Roles.Any(r => r.Id == 963676838079655956ul) && // Twitch Subscriber
                !ctx.Author.Roles.Any(r => r.Id == 440284191439978510ul))   // Senators
            {
                await ctx.ReplyAsync("You must have the Twitch Subscriber role (make sure your Twitch account is connected on Discord)", mention: true);
                await ctx.DebugAsync($"{ctx.Author.GetName()} tried to add `{args}` to the whitelist in {MentionUtils.MentionChannel(ctx.Channel.Id)} but appears not to have the required roles");
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
                    await ctx.ReplyAsync("That username is already taken by " + MentionUtils.MentionUser(takenBy), mention: true, suppressMentions: true);
                }
                return;
            }
            else if (entries.TryGetValue(ctx.AuthorId, out existing))
            {
                await _rcon.SendCommandAsync("whitelist remove " + existing);
                await _rcon.SendCommandAsync("kick " + existing);

                entries.Remove(ctx.AuthorId);
            }
            else existing = null;

            string response = await _rcon.SendCommandAsync("whitelist add " + args);

            if (response.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                await ctx.ReplyAsync("That username does not exist", mention: true);
            }
            else
            {
                entries[ctx.AuthorId] = args;

                await ctx.ReplyAsync($"Added `{args}` to the whitelist" + (existing is null ? "" : $" and removed `{existing}`"), mention: true);
            }
        }
        catch (Exception ex)
        {
            await ctx.ReplyAsync("Something went wrong :/");
            await ctx.DebugAsync(ex);
        }
        finally
        {
            _whitelist.Exit();
        }
    }
}