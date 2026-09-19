using System.Collections.Concurrent;
using System.Globalization;
using Discord.Net;
using MihuBot.Discord.Games;
using static MihuBot.Discord.Games.BlackjackRenderer;

namespace MihuBot.Discord.Commands;

public sealed class BlackjackCommand(Logger logger, IHostApplicationLifetime lifetime) : CommandBase
{
    public override string Command => "blackjack";
    public override string[] Aliases => ["bj"];

    private readonly ConcurrentDictionary<ulong, ChannelTable> _tables = new();
    private static readonly int[] s_betOptions = [10, 20, 25, 30, 40, 50, 75, 100, 150, 200, 250, 300, 350, 400, 500];

    internal const string Rules = """
        **Playing here**
        `!bj [bet]` / `!blackjack [bet]`: four seats, whole-chip bets 10-500 (default 10).
        Use the dropdown to join/change your bet and the buttons to play.
        Betting lasts 30 seconds; the host or a bot admin can Deal early. Leave / refund works before dealing.
        30-second turn timeout: decline your insurance or stand on your remaining hands.

        **Table**
        6 decks / blackjack 3:2 / stand on soft 17 / dealer peek / late surrender.
        Double any two cards, including after splits. Split equal values, up to four hands.
        Split aces: one card each, no resplitting.
        Shared shoe with a 75% cut card; between-round shuffles (earlier if reserve is low).

        **Commands**
        `!bj table` refreshes the same board. `!bj balance` shows your chips.
        Start with 1,000 per channel; `!bj rebuy` restores 1,000 when below 10 and not playing.
        Balances and shoes reset on bot restart.
        """;

    private sealed class ChannelTable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly BlackjackTable State = new();
        public IUserMessage Message;
    }

    public override Task InitAsync()
    {
        PeriodicTask.Start(nameof(BlackjackCommand),
            new PeriodicTaskOptions { Interval = TimeSpan.FromSeconds(2), FailureBackoff = TimeSpan.Zero },
            logger, ExpireRoundsAsync, lifetime.ApplicationStopping);

        return Task.CompletedTask;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        string argument = ctx.Arguments.FirstOrDefault()?.ToLowerInvariant();

        if (ctx.Arguments.Length > 1 || argument is "help" or "rules")
        {
            await ctx.ReplyAsync(Rules);
            return;
        }

        ChannelTable table = _tables.GetOrAdd(ctx.Channel.Id, _ => new ChannelTable());
        await table.Gate.WaitAsync(ctx.CancellationToken);

        try
        {
            BlackjackTable state = table.State;

            if (argument == "balance")
            {
                await ctx.ReplyAsync($"You have **{Chips(state.GetBalance(ctx.AuthorId))}** available chips in this channel.", mention: true);
                return;
            }

            if (argument == "rebuy")
            {
                await ctx.ReplyAsync(state.Rebuy(ctx.AuthorId) ?? "Your balance is now 1,000 chips.", mention: true);
                return;
            }

            if (!ctx.ChannelPermissions.EmbedLinks || !ctx.ChannelPermissions.AttachFiles)
            {
                await ctx.ReplyAsync("Blackjack needs Embed Links and Attach Files permissions to display the table.");
                return;
            }

            if (state.Expire(DateTime.UtcNow) && argument != "table")
            {
                await UpdateRoundAsync(table);
            }

            if (argument == "table")
            {
                await PublishRoundAsync(table, ctx.Channel);
                return;
            }

            string error;

            if (argument == "deal")
            {
                error = state.Deal(ctx.AuthorId, DateTime.UtcNow, isAdmin: ctx.IsFromAdmin);
            }
            else if (argument == "leave")
            {
                error = state.Leave(ctx.AuthorId, DateTime.UtcNow);
            }
            else
            {
                decimal bet = 10;

                if (argument != null && !decimal.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out bet))
                {
                    await ctx.ReplyAsync("Bet must be a whole number from 10 to 500. See `!bj rules`.", mention: true);
                    return;
                }

                error = state.Join(ctx.AuthorId, ctx.Author.GetName(), bet, DateTime.UtcNow);
            }

            if (error != null)
            {
                await ctx.ReplyAsync(error, mention: true);
                return;
            }

            await PublishRoundAsync(table, ctx.Channel);
        }
        catch (Exception ex) when (!ctx.CancellationToken.IsCancellationRequested)
        {
            ctx.DebugLog(ex, "Blackjack table update failed");
            await ctx.ReplyAsync("The table could not be displayed. Your game state is retained; use `!bj table` to recover it.");
        }
        finally
        {
            table.Gate.Release();
        }
    }

    public override async Task HandleMessageComponentAsync(SocketMessageComponent component)
    {
        await component.DeferAsync();

        if (!_tables.TryGetValue(component.Channel.Id, out ChannelTable table))
        {
            await component.FollowupAsync("This table expired, possibly because the bot restarted. Start with `!bj`.", ephemeral: true);
            return;
        }

        await table.Gate.WaitAsync();

        try
        {
            BlackjackTable state = table.State;

            if (table.Message?.Id != component.Message.Id)
            {
                await component.FollowupAsync("These controls belong to an old board. Use `!bj table`.", ephemeral: true);
                return;
            }

            if (state.Expire(DateTime.UtcNow))
            {
                await UpdateRoundAsync(table);
                await component.FollowupAsync("The table advanced after the timer expired. Please use the updated controls.", ephemeral: true);
                return;
            }

            string id = component.Data.CustomId;
            string error;

            if (id == BetCustomId(state))
            {
                error = SelectBet(state, component.User.Id, component.User.GetName(), id, component.Data.Values, DateTime.UtcNow);
            }
            else if (id == state.LobbyCustomId("Leave"))
            {
                error = state.Leave(component.User.Id, DateTime.UtcNow);
            }
            else if (id == state.LobbyCustomId("Deal"))
            {
                error = state.Deal(component.User.Id, DateTime.UtcNow, isAdmin: Constants.Admins.Contains(component.User.Id));
            }
            else
            {
                error = state.TryAct(component.User.Id, id, DateTime.UtcNow) ? null
                    : "Only the highlighted player can act. This action may be unavailable or out of date; use `!bj table`.";
            }

            if (error != null)
            {
                await component.FollowupAsync(error, ephemeral: true);
                return;
            }

            await PublishRoundAsync(table, component.Channel);
        }
        catch (Exception ex)
        {
            logger.DebugLog($"Blackjack interaction failed in channel {component.Channel.Id}: {ex}");
            await component.FollowupAsync("The table could not be updated. Use `!bj table` to recover the current round.", ephemeral: true);
        }
        finally
        {
            table.Gate.Release();
        }
    }

    private async Task ExpireRoundsAsync(CancellationToken cancellationToken)
    {
        foreach (ChannelTable table in _tables.Values)
        {
            await table.Gate.WaitAsync(cancellationToken);

            try
            {
                if (table.State.Expire(DateTime.UtcNow))
                {
                    await UpdateRoundAsync(table);
                }
            }
            finally
            {
                table.Gate.Release();
            }
        }
    }

    private Task UpdateRoundAsync(ChannelTable table) =>
        table.Message is null ? Task.CompletedTask : PublishRoundAsync(table);

    private async Task PublishRoundAsync(ChannelTable table, IMessageChannel channel = null)
    {
        PanelImage[] panels = Render(table.State);
        FileAttachment[] attachments = panels.Select(panel => new FileAttachment(
            new MemoryStream(panel.Png), PanelFilename(table.State, panel),
            description: BuildTranscript(table.State, panel.SeatIndex, panel.FirstHandIndex, panel.HandCount))).ToArray();

        try
        {
            Embed[] embeds = BuildEmbeds(table.State, panels);
            MessageComponent components = BuildComponents(table.State);

            table.Message = await UpdateBoardMessageAsync(table.Message, channel ?? table.Message.Channel,
                attachments, embeds, components,
                messageId => logger.DebugLog($"Blackjack board {messageId} was deleted; posting a replacement."));
        }
        finally
        {
            foreach (FileAttachment attachment in attachments)
            {
                attachment.Dispose();
            }
        }
    }

    internal static async Task<IUserMessage> UpdateBoardMessageAsync(IUserMessage message, IMessageChannel channel,
        FileAttachment[] attachments, Embed[] embeds, MessageComponent components, Action<ulong> onDeleted)
    {
        if (message != null)
        {
            try
            {
                await message.ModifyAsync(m =>
                {
                    m.Embeds = embeds;
                    m.Components = components;
                    m.Attachments = attachments;
                    m.AllowedMentions = AllowedMentions.None;
                });
                return message;
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.UnknownMessage)
            {
                onDeleted(message.Id);

                foreach (FileAttachment attachment in attachments)
                {
                    attachment.Stream.Position = 0;
                }
            }
        }

        return await channel.SendFilesAsync(attachments, embeds: embeds,
            components: components, allowedMentions: AllowedMentions.None);
    }

    internal static string PanelFilename(BlackjackTable table, PanelImage panel) =>
        $"blackjack-{table.Id}-{table.Revision}-{panel.SeatIndex + 1}-{panel.FirstHandIndex}.png";

    private static string BetCustomId(BlackjackTable table) =>
        table.LobbyCustomId(table.IsLobby ? "Bet" : "NextBet");

    internal static string SelectBet(BlackjackTable table, ulong playerId, string name, string customId,
        IReadOnlyCollection<string> values, DateTime now)
    {
        if (customId != BetCustomId(table))
        {
            return "This bet menu is out of date. Please use the current table.";
        }

        if (values?.Count != 1 ||
            !int.TryParse(values.First(), NumberStyles.None, CultureInfo.InvariantCulture, out int bet) ||
            !s_betOptions.Contains(bet))
        {
            return "Please select one of the listed bets.";
        }

        return table.Join(playerId, name, bet, now);
    }

    internal static MessageComponent BuildComponents(BlackjackTable table)
    {
        var builder = new ComponentBuilder();

        if (table.IsLobby || !table.IsActive)
        {
            var menu = new SelectMenuBuilder()
                .WithCustomId(BetCustomId(table))
                .WithPlaceholder(table.IsLobby ? "Choose your bet / join or change" : "Choose your bet for the next round")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (int bet in s_betOptions)
            {
                menu.AddOption($"{bet} chips", bet.ToString(CultureInfo.InvariantCulture));
            }

            builder.WithSelectMenu(menu, row: 0);

            if (table.IsLobby)
            {
                builder.WithButton("Deal (host/admin)", table.LobbyCustomId("Deal"), ButtonStyle.Primary, row: 1);
                builder.WithButton("Leave / refund", table.LobbyCustomId("Leave"), ButtonStyle.Secondary, row: 1);
            }
        }
        else
        {
            foreach (BlackjackAction action in Enum.GetValues<BlackjackAction>())
            {
                if (table.Game.CanAct(action))
                {
                    string label = action switch
                    {
                        BlackjackAction.Insure => $"Insurance ({Chips(table.Game.ActiveHand.Bet / 2)})",
                        BlackjackAction.DeclineInsurance => "No insurance",
                        _ => action.ToString()
                    };
                    ButtonStyle style = action == BlackjackAction.Hit ? ButtonStyle.Success
                        : action == BlackjackAction.Stand ? ButtonStyle.Primary : ButtonStyle.Secondary;
                    builder.WithButton(label, table.CustomId(action), style);
                }
            }
        }

        return builder.Build();
    }

    internal static Embed BuildEmbed(BlackjackTable table, string filename)
    {
        long deadline = table.IsActive ? new DateTimeOffset(table.Deadline).ToUnixTimeSeconds() : 0;
        string description = table.IsLobby
            ? $"**Betting open: {table.Seats.Count}/4 seats.** Choose a bet below to join or change your wager.\n" +
                $"Auto-deal <t:{deadline}:R>; host <@{table.HostId}> or a bot admin can deal early."
            : table.Game is { IsComplete: false } game
                ? $"**Seat {game.ActivePlayerIndex + 1}: <@{game.ActivePlayer.Id}>** - " +
                    (game.OfferingInsurance ? "insurance decision." : $"play hand {game.ActivePlayer.ActiveHandIndex + 1}.") +
                    $"\nTurn ends <t:{deadline}:R>. Everyone plays against the same dealer."
                : table.Game?.IsComplete == true
                    ? "Round finished. Choose a bet below to start the next betting window."
                    : "Choose a bet below to take a seat. Up to four players can join.";
        var embed = new EmbedBuilder()
            .WithTitle("Blackjack / Card Room")
            .WithColor(Color.DarkGreen)
            .WithDescription(description + (table.Notice is null ? "" : $"\n{table.Notice}"))
            .WithImageUrl($"attachment://{filename}")
            .WithFooter("6D / S17 / 3:2 / DAS / LS | !bj rules | Balances reset on restart");

        return embed.Build();
    }

    internal static Embed[] BuildEmbeds(BlackjackTable table, IReadOnlyList<PanelImage> panels)
    {
        var embeds = new List<Embed> { BuildEmbed(table, PanelFilename(table, panels[0])) };

        foreach (PanelImage panel in panels.Skip(1))
        {
            BlackjackPlayer player = table.Seats[panel.SeatIndex];
            string result = table.Game?.IsComplete == true ? $" | Net **{SignedChips(player.Balance - player.StartingBalance)}**" : "";
            bool active = table.Game is { IsComplete: false } game && game.ActivePlayer == player &&
                player.ActiveHandIndex >= panel.FirstHandIndex && player.ActiveHandIndex < panel.FirstHandIndex + panel.HandCount;
            embeds.Add(new EmbedBuilder()
                .WithColor(active ? Color.Gold : Color.DarkGreen)
                .WithDescription($"**Seat {panel.SeatIndex + 1}** <@{player.Id}> | Chips **{Chips(player.Balance)}**{result}")
                .WithImageUrl($"attachment://{PanelFilename(table, panel)}")
                .Build());
        }

        return embeds.ToArray();
    }

    internal static string BuildTranscript(BlackjackTable table, int? seatIndex = null, int firstHandIndex = 0, int handCount = 4)
    {
        var text = new StringBuilder("Blackjack. ");

        if (seatIndex is null or < 0)
        {
            if (table.Game is { } game)
            {
                text.Append("Dealer: ").Append(game.IsComplete ? Cards(game.Dealer) : $"{game.Dealer[0]}, hidden card").Append(". ");
            }
            else
            {
                text.Append("Betting lobby. ");
            }
        }

        for (int i = 0; i < table.Seats.Count; i++)
        {
            if (seatIndex.HasValue && seatIndex != i)
            {
                continue;
            }

            BlackjackPlayer player = table.Seats[i];
            text.Append($"Seat {i + 1}, {player.Name}: ");

            foreach (BlackjackHand hand in player.Hands.Skip(firstHandIndex).Take(handCount))
            {
                text.Append($"bet {Chips(hand.Bet)}, {Cards(hand.Cards)} {hand.Result}; ");
            }

            if (player.InsuranceBet > 0)
            {
                text.Append($"insurance bet {Chips(player.InsuranceBet)}");

                if (table.Game?.IsComplete == true)
                {
                    text.Append($", net {SignedChips(player.InsuranceReturned - player.InsuranceBet)}");
                }

                text.Append("; ");
            }
        }

        return text.ToString().TruncateWithDotDotDot(1_024);
    }

    private static string Cards(IEnumerable<BlackjackCard> cards) =>
        $"{string.Join(' ', cards)} (total {BlackjackHand.Evaluate(cards).Total})";
}
