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
    IReadOnlyList<BrowserBlackjackHand> Hands);
public sealed record BrowserBlackjackState(
    string RoomId, string RoundId, long Version, bool Lobby, bool Complete, bool Active,
    ulong HostId, ulong ActivePlayerId, bool Insurance, DateTime Deadline, string Notice,
    int ShoeNumber, int RemainingCards, bool Shuffled, decimal Balance,
    IReadOnlyList<BrowserBlackjackCard> Dealer, int? DealerTotal,
    IReadOnlyList<BrowserBlackjackSeat> Seats, IReadOnlyList<BrowserBlackjackCommand> Actions,
    int DeckCount, BrowserBlackjackAdvice Advice);

public sealed record BrowserBlackjackAdvice(
    int RunningCount, double TrueCount, double UnseenDecks, BrowserBlackjackCommand? Move, string Explanation, bool Deviation);

public sealed class BrowserBlackjackService : BackgroundService
{
    internal const int MaxRooms = 128;
    internal const int MaxRoomsPerOwner = 3;
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromHours(2);

    private readonly object _lock = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly Func<int, BlackjackTable> _createTable;

    public BrowserBlackjackService()
    {
        _clock = TimeProvider.System;
        _createTable = decks => new BlackjackTable(new BlackjackShoe(decks));
    }

    internal BrowserBlackjackService(TimeProvider clock, Func<BlackjackTable> createTable)
    {
        _clock = clock;
        _createTable = _ => createTable();
    }

    public (string RoomId, string Error) CreateRoom(ClaimsPrincipal user, int decks = BlackjackShoe.Decks)
    {
        if (!TryGetPlayer(user, out ulong id, out _))
        {
            return (null, "Sign in with Discord to create a table.");
        }

        if (decks is not (4 or 6 or 8))
        {
            return (null, "Choose a four-, six-, or eight-deck shoe.");
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
            _rooms.Add(roomId, new Room(ownerId, _createTable(decks), UtcNow, discordChannelId));
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

            if (version != room.Version)
            {
                return "The table changed before your move arrived. Check the updated table and try again.";
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
                    error = table.Rebuy(id);
                    break;

                case BrowserBlackjackCommand.Hit:
                case BrowserBlackjackCommand.Stand:
                case BrowserBlackjackCommand.Double:
                case BrowserBlackjackCommand.Split:
                case BrowserBlackjackCommand.Surrender:
                case BrowserBlackjackCommand.Insure:
                case BrowserBlackjackCommand.DeclineInsurance:
                    error = Enum.TryParse(command.ToString(), out BlackjackAction action) &&
                        Enum.IsDefined(action) && table.TryAct(id, table.CustomId(action), UtcNow)
                        ? null : "That move is not available. Wait for your turn and choose an enabled action.";
                    break;

                default:
                    error = "Unknown table action.";
                    break;
            }

            if (error is null)
            {
                UpdateExposedCards(room);
                room.Players.Add(id);
                room.Version++;
                room.LastViewed = UtcNow;
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
            room.Version++;
        }
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

    private static BrowserBlackjackState Snapshot(string roomId, Room room, ulong viewerId, bool includeAdvice)
    {
        BlackjackTable table = room.Table;
        BlackjackGame game = table.Game;
        bool complete = game?.IsComplete == true;
        ulong activeId = game?.ActivePlayer?.Id ?? 0;
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

            if (table.GetBalance(viewerId) < 10 && !(table.IsActive && seated))
            {
                actions.Add(BrowserBlackjackCommand.Rebuy);
            }

            if (activeId == viewerId)
            {
                foreach (BlackjackAction action in Enum.GetValues<BlackjackAction>())
                {
                    if (game.CanAct(action))
                    {
                        actions.Add(Enum.Parse<BrowserBlackjackCommand>(action.ToString()));
                    }
                }
            }
        }

        return new(
            roomId, table.Id, room.Version, table.IsLobby, complete, table.IsActive,
            GetHostId(room), activeId, game?.OfferingInsurance == true, table.Deadline, table.Notice,
            table.Shoe.Number, table.Shoe.Remaining, table.Shuffled, table.GetBalance(viewerId),
            game?.Dealer.Select((c, i) => i == 1 && !complete ? new BrowserBlackjackCard(0, 0) : Card(c)).ToArray() ?? [],
            complete ? BlackjackHand.Evaluate(game.Dealer).Total : null,
            table.Seats.Select(p => new BrowserBlackjackSeat(
                p.Id, p.Name, table.GetBalance(p.Id), p.Balance - p.StartingBalance, p.InsuranceBet, p.InsuranceReturned,
                p.Hands.Select((h, i) => new BrowserBlackjackHand(
                    h.Cards.Select(Card).ToArray(), h.Bet, h.Value.Total, h.Value.Soft,
                    p.Id == activeId && i == p.ActiveHandIndex, h.Result)).ToArray())).ToArray(),
            actions.ToArray(), table.Shoe.DeckCount,
            includeAdvice ? BlackjackStrategy.Analyze(table.Shoe.DeckCount, room.ExposedCards,
                activeId != 0 && activeId == viewerId ? game.ActiveHand : null, game?.Dealer[0].Value ?? 0,
                game?.OfferingInsurance == true, actions) : null);
    }

    private static BrowserBlackjackCard Card(BlackjackCard card) => new(card.Rank, card.Suit);

    private static ulong GetHostId(Room room) => room.Table.HostId is not 0 ? room.Table.HostId : room.OwnerId;

    private sealed class Room(ulong ownerId, BlackjackTable table, DateTime now, ulong? discordChannelId)
    {
        public ulong OwnerId { get; } = ownerId;
        public ulong? DiscordChannelId { get; } = discordChannelId;
        public BlackjackTable Table { get; } = table;
        public HashSet<ulong> Players { get; } = [];
        public DateTime LastViewed { get; set; } = now;
        public long Version { get; set; }
        public int CountedShoe { get; set; }
        public string CountedRound { get; set; }
        public int[] ExposedCards { get; } = new int[11];
        public int[] RoundCards { get; } = new int[11];
    }
}
