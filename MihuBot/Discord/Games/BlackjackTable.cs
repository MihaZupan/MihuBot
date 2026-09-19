namespace MihuBot.Discord.Games;

internal sealed class BlackjackTable
{
    public const decimal StartingChips = 1_000;
    public static readonly TimeSpan BettingTime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TurnTime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<ulong, decimal> _balances = [];
    private readonly List<BlackjackPlayer> _seats = [];

    public BlackjackShoe Shoe { get; }
    public IReadOnlyList<BlackjackPlayer> Seats => _seats;
    public BlackjackGame Game { get; private set; }
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public int Revision { get; private set; }
    public DateTime Deadline { get; private set; }
    public bool Shuffled { get; private set; }
    public string Notice { get; private set; }
    public bool IsLobby => Game is null && _seats.Count != 0;
    public bool IsActive => IsLobby || Game is { IsComplete: false };
    public ulong HostId => _seats.FirstOrDefault()?.Id ?? 0;

    public BlackjackTable(BlackjackShoe shoe = null)
    {
        Shoe = shoe ?? new BlackjackShoe();
    }

    public decimal GetBalance(ulong userId) => _balances.GetValueOrDefault(userId, StartingChips);

    public string LobbyCustomId(string action) => $"blackjack-{Id}-lobby-{action}";
    public string CustomId(BlackjackAction action) => $"blackjack-{Id}-{Revision}-{action}";

    public string Join(ulong userId, string name, decimal bet, DateTime now)
    {
        if (bet is < 10 or > 500 || decimal.Truncate(bet) != bet)
        {
            return "Bet must be a whole number from 10 to 500.";
        }

        if (Game is { IsComplete: false } || (IsLobby && now >= Deadline))
        {
            return "This round has already started. You can join the next betting window.";
        }

        BlackjackPlayer existing = IsLobby ? _seats.Find(p => p.Id == userId) : null;

        if (IsLobby && existing is null && _seats.Count == BlackjackGame.MaxPlayers)
        {
            return "All four seats are taken. You can join the next round.";
        }

        decimal balance = existing?.StartingBalance ?? GetBalance(userId);

        if (balance < bet)
        {
            return "Not enough chips. Use `!bj balance`, lower your bet, or `!bj rebuy` if below 10.";
        }

        if (existing != null)
        {
            existing.Hands[0].Bet = bet;
            existing.Balance = balance - bet;
            _balances[userId] = existing.Balance;
            Revision++;
            return null;
        }

        if (!IsLobby)
        {
            _seats.Clear();
            Game = null;
            Id = Guid.NewGuid().ToString("N");
            Revision = 0;
            Deadline = now + BettingTime;
            Shuffled = false;
        }

        var player = new BlackjackPlayer(userId, name, balance, bet);
        _seats.Add(player);
        _balances[userId] = player.Balance;
        Notice = null;
        Revision++;
        return null;
    }

    public string Leave(ulong userId, DateTime now)
    {
        if (!IsLobby || now >= Deadline)
        {
            return "You can leave only while betting is open.";
        }

        BlackjackPlayer player = _seats.Find(p => p.Id == userId);

        if (player is null)
        {
            return "You're not seated at this table.";
        }

        _balances[userId] = player.StartingBalance;
        _seats.Remove(player);
        Revision++;
        return null;
    }

    public string Deal(ulong userId, DateTime now, bool isAdmin = false)
    {
        if (!IsLobby)
        {
            return "There is no open betting window. Join with `!bj [bet]`.";
        }

        if (userId != HostId && !isAdmin)
        {
            return "Only the host or a bot admin can deal early. Otherwise the table deals automatically.";
        }

        Start(now);
        return null;
    }

    public string Rebuy(ulong userId)
    {
        if (IsActive && _seats.Any(p => p.Id == userId))
        {
            return "Finish your current round or leave the betting lobby before rebuying.";
        }

        if (GetBalance(userId) >= 10)
        {
            return "Free rebuys are available only when you have fewer than 10 chips.";
        }

        _balances[userId] = StartingChips;
        return null;
    }

    public bool TryAct(ulong userId, string customId, DateTime now)
    {
        if (Game is not { IsComplete: false } || Game.ActivePlayer.Id != userId || now >= Deadline)
        {
            return false;
        }

        foreach (BlackjackAction action in Enum.GetValues<BlackjackAction>())
        {
            if (customId == CustomId(action) && Game.TryAct(action))
            {
                Notice = null;
                SaveBalances();
                Revision++;
                Deadline = now + TurnTime;
                return true;
            }
        }

        return false;
    }

    public bool Expire(DateTime now)
    {
        if (!IsActive || now < Deadline)
        {
            return false;
        }

        if (IsLobby)
        {
            Start(now);
        }
        else
        {
            int seat = Game.ActivePlayerIndex + 1;
            Notice = Game.OfferingInsurance
                ? $"Seat {seat} timed out: insurance declined."
                : $"Seat {seat} timed out: all their remaining hands stood.";
            Game.AutoStandCurrentPlayer();
            SaveBalances();
            Revision++;
            Deadline = now + TurnTime;
        }

        return true;
    }

    private void Start(DateTime now)
    {
        Shuffled = Shoe.PrepareRound(_seats.Count);
        Game = new BlackjackGame(Shoe.Draw, _seats);
        SaveBalances();
        Revision++;
        Deadline = now + TurnTime;
    }

    private void SaveBalances()
    {
        foreach (BlackjackPlayer player in _seats)
        {
            _balances[player.Id] = player.Balance;
        }
    }
}
