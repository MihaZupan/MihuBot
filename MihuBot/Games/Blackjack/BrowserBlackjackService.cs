using System.Security.Claims;

namespace MihuBot.Games.Blackjack;

public enum BrowserBlackjackCommand
{
    Join,
    Leave,
    Deal,
    Rebuy,
    Hit,
    Stand,
    Double,
    Split,
    Surrender,
    Insure,
    DeclineInsurance,
    Close
}

public sealed record BrowserBlackjackCard(int Rank, int Suit);
public sealed record BrowserBlackjackHand(
    IReadOnlyList<BrowserBlackjackCard> Cards, decimal Bet, int Total, bool Soft, bool Active, string Result);
public sealed record BrowserBlackjackSeat(
    ulong Id, string Name, decimal Balance, decimal Change, decimal InsuranceBet, decimal InsuranceReturned,
    IReadOnlyList<BrowserBlackjackHand> Hands, bool Pending, DateTime? Deadline);
public sealed record BrowserBlackjackState(
    string RoomId, string RoundId, long Version, bool Lobby, bool Complete, bool Active,
    ulong HostId, bool YourTurn, bool Insurance, DateTime Deadline, string Notice,
    int ShoeNumber, int RemainingCards, bool Shuffled, decimal Balance,
    IReadOnlyList<BrowserBlackjackCard> Dealer, int? DealerTotal,
    IReadOnlyList<BrowserBlackjackSeat> Seats, IReadOnlyList<BrowserBlackjackCommand> Actions,
    int DeckCount, int MaxHandsPerPlayer, BrowserBlackjackAdvice Advice);

public sealed record BrowserBlackjackAdvice(
    int RunningCount, double TrueCount, double UnseenDecks, BrowserBlackjackCommand? Move, string Explanation, bool Deviation,
    BrowserBlackjackBetAdvice Bet = null);

public sealed record BrowserBlackjackBetAdvice(decimal? Amount, double TrueCount, bool ShuffleExpected, string Explanation);

public sealed class BrowserBlackjackService : BackgroundService
{
    public const int MaxPlayers = BlackjackGame.MaxPlayers;
    internal const int MaxRooms = 128;
    internal const int MaxRoomsPerOwner = 3;
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(2);

    private readonly object _lock = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly Func<int, BlackjackTable> _createTable;
    private readonly BlackjackBalanceStore _balances;
    private bool _disposed;

    public BrowserBlackjackService() : this(
        TimeProvider.System, decks => new BlackjackTable(new BlackjackShoe(decks)),
        new BlackjackBalanceStore(Path.Combine(Constants.StateDirectory, "BlackjackBalances.json")))
    {
    }

    internal BrowserBlackjackService(TimeProvider clock) : this(
        clock, decks => new BlackjackTable(new BlackjackShoe(decks)), new BlackjackBalanceStore())
    {
    }

    internal BrowserBlackjackService(TimeProvider clock, Func<BlackjackTable> createTable,
        BlackjackBalanceStore balances = null)
        : this(clock, _ => createTable(), balances ?? new BlackjackBalanceStore())
    {
    }

    private BrowserBlackjackService(TimeProvider clock, Func<int, BlackjackTable> createTable,
        BlackjackBalanceStore balances)
    {
        _clock = clock;
        _createTable = createTable;
        _balances = balances;
    }

    public decimal? GetBalance(ClaimsPrincipal user)
    {
        if (!TryGetPlayer(user, out ulong id, out _))
        {
            return null;
        }

        lock (_lock)
        {
            return GetAvailableBalance(id);
        }
    }

    public (string RoomId, string Error) CreateRoom(ClaimsPrincipal user, int decks = BlackjackShoe.Decks)
    {
        if (!TryGetPlayer(user, out ulong id, out _))
        {
            return (null, "Sign in with Discord to create a table.");
        }

        if (decks is not (2 or 4 or 6 or 8))
        {
            return (null, "Choose a two-, four-, six-, or eight-deck shoe.");
        }

        return CreateRoom(id, decks);
    }

    internal (string RoomId, string Error) GetOrCreateDiscordLobby(ulong channelId, ulong authorId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(channelId);
        ArgumentOutOfRangeException.ThrowIfZero(authorId);

        lock (_lock)
        {
            Sweep();
            (string id, Room room) = _rooms.FirstOrDefault(r => r.Value.DiscordChannelId == channelId);

            if (room is not null)
            {
                room.LastViewed = UtcNow;
                return (id, null);
            }

            return CreateRoom(authorId, BlackjackShoe.Decks, channelId);
        }
    }

    private (string RoomId, string Error) CreateRoom(ulong ownerId, int decks, ulong? discordChannelId = null)
    {
        lock (_lock)
        {
            Sweep();

            if (_rooms.Count >= MaxRooms || _rooms.Values.Count(r => r.OwnerId == ownerId) >= MaxRoomsPerOwner)
            {
                return (null, "The table limit has been reached. Reuse an existing table; idle tables expire after two hours.");
            }

            string roomId = Guid.NewGuid().ToString("N");
            BlackjackTable table = _createTable(decks);
            table.SetBalanceProvider(GetAvailableBalance);
            _rooms.Add(roomId, new Room(ownerId, table, UtcNow, discordChannelId));
            return (roomId, null);
        }
    }

    public BrowserBlackjackState Read(string roomId, ClaimsPrincipal user, bool includeAdvice = false)
    {
        lock (_lock)
        {
            if (roomId is null || !_rooms.TryGetValue(roomId, out Room room))
            {
                return null;
            }

            Expire(room);

            room.LastViewed = UtcNow;
            TryGetPlayer(user, out ulong id, out _);
            return Snapshot(roomId, room, id, includeAdvice);
        }
    }

    public IReadOnlyList<string> GetOwnedRooms(ClaimsPrincipal user)
    {
        if (!TryGetPlayer(user, out ulong id, out _))
        {
            return [];
        }

        lock (_lock)
        {
            Sweep();
            return _rooms.Where(r => r.Value.OwnerId == id).Select(r => r.Key).ToArray();
        }
    }

    public string Execute(string roomId, ClaimsPrincipal user, long version, BrowserBlackjackCommand command, decimal bet = 10)
    {
        if (!TryGetPlayer(user, out ulong id, out string name))
        {
            return "Sign in with Discord to play.";
        }

        lock (_lock)
        {
            if (roomId is null || !_rooms.TryGetValue(roomId, out Room room))
            {
                return "This table has closed or expired. Create a new table.";
            }

            Expire(room);

            if (version != room.Table.GetRevision(id))
            {
                return "Your hand or the round phase changed before your move arrived. Check the updated table and try again.";
            }

            BlackjackTable table = room.Table;
            string error;

            switch (command)
            {
                case BrowserBlackjackCommand.Close:
                    if (id != GetHostId(room))
                    {
                        return "Only the host can close this table.";
                    }

                    table.FinishRound(UtcNow);
                    UpdateExposedCards(room);
                    SaveCompletedRound(room);
                    _rooms.Remove(roomId);
                    return null;

                case BrowserBlackjackCommand.Join:
                    if (!room.Players.Contains(id) && room.Players.Count >= 256)
                    {
                        return "This table has reached its player limit. Create a new table.";
                    }

                    if (table.GetBalance(id) + (table.IsLobby ? table.Seats.FirstOrDefault(p => p.Id == id)?.Hands[0].Bet ?? 0 : 0) < bet)
                    {
                        return "Not enough chips. Lower your bet, or rebuy when your balance is below 10.";
                    }

                    error = table.Join(id, name, bet, UtcNow);
                    break;

                case BrowserBlackjackCommand.Leave:
                    error = table.Leave(id, UtcNow);
                    break;

                case BrowserBlackjackCommand.Deal:
                    error = !table.IsLobby ? "Join the betting window first."
                        : table.HostId != id ? "Only the host can deal early. Otherwise the table deals automatically."
                        : table.Deal(id, UtcNow);
                    break;

                case BrowserBlackjackCommand.Rebuy:
                    error = Rebuy(table, id);
                    break;

                case BrowserBlackjackCommand.Hit:
                case BrowserBlackjackCommand.Stand:
                case BrowserBlackjackCommand.Double:
                case BrowserBlackjackCommand.Split:
                case BrowserBlackjackCommand.Surrender:
                case BrowserBlackjackCommand.Insure:
                case BrowserBlackjackCommand.DeclineInsurance:
                    error = Enum.TryParse(command.ToString(), out BlackjackAction action) &&
                        Enum.IsDefined(action) && table.TryAct(id, version, action, UtcNow)
                        ? null : "That move is not available. Choose an enabled action for your own hand.";
                    break;

                default:
                    error = "Unknown table action.";
                    break;
            }

            if (error is null)
            {
                UpdateExposedCards(room);
                room.Players.Add(id);
                room.LastViewed = UtcNow;

                SaveCompletedRound(room);
            }

            return error;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Sweep();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal void Sweep()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            foreach ((string id, Room room) in _rooms.ToArray())
            {
                Expire(room);

                if (!room.Table.IsActive && UtcNow - room.LastViewed >= IdleLifetime)
                {
                    _rooms.Remove(id);
                }
            }
        }
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private void Expire(Room room)
    {
        if (room.Table.Expire(UtcNow))
        {
            UpdateExposedCards(room);
        }

        SaveCompletedRound(room);
    }

    private decimal GetReserved(ulong userId) =>
        _rooms.Values.Where(r => r.AppliedRoundId != r.Table.Id)
            .SelectMany(r => r.Table.Seats).Where(p => p.Id == userId)
            .Sum(p => p.Hands.Sum(h => h.Bet) + p.InsuranceBet);

    private decimal GetAvailableBalance(ulong userId) => _balances.GetBalance(userId) - GetReserved(userId);

    private static decimal RoundChange(BlackjackPlayer player) =>
        player.Hands.Sum(h => h.Returned - h.Bet) + player.InsuranceReturned - player.InsuranceBet;

    private void SaveCompletedRound(Room room)
    {
        if (room.Table.Game is not { IsComplete: true } game ||
            room.AppliedRoundId == room.Table.Id)
        {
            return;
        }

        var changes = game.Players.ToDictionary(p => p.Id, p => checked((long)(RoundChange(p) * 2)));
        // The store applies results in memory before writing them to disk.
        room.AppliedRoundId = room.Table.Id;
        _balances.ApplyResults(changes);
    }

    private string Rebuy(BlackjackTable table, ulong userId)
    {
        if (GetReserved(userId) != 0)
        {
            return "Finish your rounds or leave their betting lobbies before rebuying. This applies across all tables.";
        }

        if (GetAvailableBalance(userId) >= 10)
        {
            return "Free rebuys are available only when you have fewer than 10 chips.";
        }

        _balances.Rebuy(userId);
        table.NotifyBalanceChanged(userId);
        return null;
    }

    private static bool TryGetPlayer(ClaimsPrincipal user, out ulong id, out string name)
    {
        ClaimsIdentity identity = user?.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == "Discord");
        name = identity?.Name ?? "Player";
        name = name[..Math.Min(name.Length, 80)];
        id = 0;
        return identity is not null && ulong.TryParse(identity.FindFirst(ClaimTypes.NameIdentifier)?.Value, out id) && id != 0;
    }

    private static void UpdateExposedCards(Room room)
    {
        if (room.CountedShoe != room.Table.Shoe.Number)
        {
            Array.Clear(room.ExposedCards);
            Array.Clear(room.RoundCards);
            room.CountedShoe = room.Table.Shoe.Number;
        }

        if (room.CountedRound != room.Table.Id)
        {
            Array.Clear(room.RoundCards);
            room.CountedRound = room.Table.Id;
        }

        if (room.Table.Game is not { } game)
        {
            return;
        }

        Span<int> current = stackalloc int[11];
        current.Clear();

        foreach (BlackjackCard card in game.Players.SelectMany(p => p.Hands).SelectMany(h => h.Cards))
        {
            current[card.Value]++;
        }

        for (int i = 0; i < game.Dealer.Count; i++)
        {
            if (i != 1 || game.IsComplete)
            {
                current[game.Dealer[i].Value]++;
            }
        }

        for (int rank = 1; rank <= 10; rank++)
        {
            room.ExposedCards[rank] += current[rank] - room.RoundCards[rank];
            room.RoundCards[rank] = current[rank];
        }
    }

    private BrowserBlackjackState Snapshot(string roomId, Room room, ulong viewerId, bool includeAdvice)
    {
        BlackjackTable table = room.Table;
        table.RefreshBalances();
        BlackjackGame game = table.Game;
        bool complete = game?.IsComplete == true;
        BlackjackPlayer viewer = game?.GetPlayer(viewerId);
        bool yourTurn = game?.NeedsAction(viewerId) == true;
        var actions = new List<BrowserBlackjackCommand>();
        bool seated = table.Seats.Any(p => p.Id == viewerId);

        if (viewerId != 0)
        {
            if (viewerId == GetHostId(room))
            {
                actions.Add(BrowserBlackjackCommand.Close);
            }

            if (!table.IsActive || (table.IsLobby && (seated || table.Seats.Count < BlackjackGame.MaxPlayers)))
            {
                actions.Add(BrowserBlackjackCommand.Join);
            }

            if (table.IsLobby && seated)
            {
                actions.Add(BrowserBlackjackCommand.Leave);

                if (table.HostId == viewerId)
                {
                    actions.Add(BrowserBlackjackCommand.Deal);
                }
            }

            if (table.GetBalance(viewerId) < 10 && GetReserved(viewerId) == 0)
            {
                actions.Add(BrowserBlackjackCommand.Rebuy);
            }

            if (yourTurn)
            {
                foreach (BlackjackAction action in Enum.GetValues<BlackjackAction>())
                {
                    if (game.CanAct(viewerId, action))
                    {
                        actions.Add(Enum.Parse<BrowserBlackjackCommand>(action.ToString()));
                    }
                }
            }
        }

        BrowserBlackjackAdvice advice = includeAdvice ? BlackjackStrategy.Analyze(table.Shoe.DeckCount, room.ExposedCards,
            yourTurn ? viewer.ActiveHand : null, game?.Dealer[0].Value ?? 0,
            game?.OfferingInsurance == true, actions) : null;

        if (advice is not null && actions.Contains(BrowserBlackjackCommand.Join))
        {
            int players = table.IsLobby ? table.Seats.Count + (seated ? 0 : 1) : 1;
            decimal balance = table.GetBalance(viewerId) +
                (table.IsLobby ? table.Seats.FirstOrDefault(p => p.Id == viewerId)?.Hands[0].Bet ?? 0 : 0);
            advice = advice with
            {
                Bet = BlackjackStrategy.RecommendBet(advice.TrueCount, balance, table.Shoe.NeedsShuffle(players))
            };
        }

        return new(
            roomId, table.Id, table.GetRevision(viewerId), table.IsLobby, complete, table.IsActive,
            GetHostId(room), yourTurn, game?.OfferingInsurance == true, table.GetDeadline(viewerId) ?? table.Deadline, table.Notice,
            table.Shoe.Number, table.Shoe.Remaining, table.Shuffled, viewerId == 0 ? 0 : table.GetBalance(viewerId),
            game?.Dealer.Select((c, i) => i == 1 && !complete ? new BrowserBlackjackCard(0, 0) : Card(c)).ToArray() ?? [],
            complete ? BlackjackHand.Evaluate(game.Dealer).Total : null,
            table.Seats.Select(p => new BrowserBlackjackSeat(
                p.Id, p.Name, table.GetBalance(p.Id), RoundChange(p), p.InsuranceBet, p.InsuranceReturned,
                p.Hands.Select((h, i) => new BrowserBlackjackHand(
                    h.Cards.Select(Card).ToArray(), h.Bet, h.Value.Total, h.Value.Soft,
                    game?.NeedsAction(p.Id) == true && i == p.ActiveHandIndex, h.Result)).ToArray(),
                game?.NeedsAction(p.Id) == true, table.GetDeadline(p.Id))).ToArray(),
            actions.ToArray(), table.Shoe.DeckCount, table.Shoe.MaxHandsPerPlayer,
            advice);
    }

    private static BrowserBlackjackCard Card(BlackjackCard card) => new(card.Rank, card.Suit);

    private static ulong GetHostId(Room room) => room.Table.HostId is not 0 ? room.Table.HostId : room.OwnerId;

    public override void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        base.Dispose();
    }

    private sealed class Room(ulong ownerId, BlackjackTable table, DateTime now, ulong? discordChannelId)
    {
        public ulong OwnerId { get; } = ownerId;
        public ulong? DiscordChannelId { get; } = discordChannelId;
        public BlackjackTable Table { get; } = table;
        public HashSet<ulong> Players { get; } = [];
        public DateTime LastViewed { get; set; } = now;
        public int CountedShoe { get; set; }
        public string CountedRound { get; set; }
        public int[] ExposedCards { get; } = new int[11];
        public int[] RoundCards { get; } = new int[11];
        public string AppliedRoundId { get; set; }
    }
}
