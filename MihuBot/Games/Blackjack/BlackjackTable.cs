namespace MihuBot.Games.Blackjack;

internal sealed class BlackjackTable
{
    public const decimal StartingChips = 1_000;
    public const int MinimumBet = 10;
    public const int MaximumBet = 500;
    public static readonly TimeSpan BettingTime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TurnTime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<ulong, decimal> _balances = [];
    private readonly Dictionary<ulong, long> _playerRevisions = [];
    private readonly Dictionary<ulong, DateTime> _deadlines = [];
    private readonly List<BlackjackPlayer> _seats = [];
    private Func<ulong, decimal> _balanceProvider;
    private long _phaseRevision;
    private DateTime _bettingDeadline;

    public BlackjackShoe Shoe { get; }
    public IReadOnlyList<BlackjackPlayer> Seats => _seats;
    public BlackjackGame Game { get; private set; }
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public long Revision { get; private set; }
    public DateTime Deadline => IsLobby ? _bettingDeadline : _deadlines.Values.DefaultIfEmpty().Min();
    public bool Shuffled { get; private set; }
    public string Notice { get; private set; }
    public bool IsLobby => Game is null && _seats.Count != 0;
    public bool IsActive => IsLobby || Game is { IsComplete: false };
    public ulong HostId => _seats.FirstOrDefault()?.Id ?? 0;

    public BlackjackTable(BlackjackShoe shoe = null)
    {
        Shoe = shoe ?? new BlackjackShoe();
    }

    public decimal GetBalance(ulong userId) => _balanceProvider?.Invoke(userId) ?? _balances.GetValueOrDefault(userId, StartingChips);

    public void SetBalanceProvider(Func<ulong, decimal> balanceProvider)
    {
        ArgumentNullException.ThrowIfNull(balanceProvider);

        if (_balanceProvider is not null)
        {
            throw new InvalidOperationException("This table already has a balance provider.");
        }

        _balanceProvider = balanceProvider;
    }

    public void RefreshBalances()
    {
        if (_balanceProvider is not null)
        {
            foreach (BlackjackPlayer player in _seats)
            {
                player.Balance = GetBalance(player.Id);
            }
        }
    }

    public void NotifyBalanceChanged(ulong userId) => Changed(userId);

    public long GetRevision(ulong userId) => Math.Max(_phaseRevision, _playerRevisions.GetValueOrDefault(userId));

    public DateTime? GetDeadline(ulong userId) => _deadlines.TryGetValue(userId, out DateTime deadline) ? deadline : null;

    public string Join(ulong userId, string name, decimal bet, DateTime now)
    {
        if (bet is < MinimumBet or > MaximumBet || decimal.Truncate(bet) != bet)
        {
            return $"Bet must be a whole number from {MinimumBet} to {MaximumBet}.";
        }

        if (Game is { IsComplete: false } || (IsLobby && now >= Deadline))
        {
            return "This round has already started. You can join the next betting window.";
        }

        BlackjackPlayer existing = IsLobby ? _seats.Find(p => p.Id == userId) : null;

        if (IsLobby && existing is null && _seats.Count == BlackjackGame.MaxPlayers)
        {
            return "All six seats are taken. You can join the next round.";
        }

        decimal balance = GetBalance(userId) + (existing?.Hands[0].Bet ?? 0);

        if (balance < bet)
        {
            return $"Not enough chips. Lower your bet, or rebuy when your balance is below {MinimumBet}.";
        }

        if (existing != null)
        {
            existing.Hands[0].Bet = bet;
            existing.Balance = balance - bet;
            _balances[userId] = existing.Balance;
            Changed(userId);
            return null;
        }

        if (!IsLobby)
        {
            _seats.Clear();
            Game = null;
            Id = Guid.NewGuid().ToString("N");
            _bettingDeadline = now + BettingTime;
            Shuffled = false;
        }

        var player = new BlackjackPlayer(userId, name, balance, bet);
        _seats.Add(player);
        _balances[userId] = player.Balance;
        Notice = null;
        Changed(userId);
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
        Changed(userId);
        return null;
    }

    public string Deal(ulong userId, DateTime now)
    {
        if (!IsLobby)
        {
            return "There is no open betting window. Place a bet to join.";
        }

        if (userId != HostId)
        {
            return "Only the host can deal early. Otherwise the table deals automatically.";
        }

        Start(now);
        return null;
    }

    public void FinishRound(DateTime now)
    {
        if (Game is { IsComplete: false })
        {
            RefreshBalances();
            Game.AutoStand();
            SaveBalances();
            Changed(phaseChanged: true);
            ResetDeadlines(now);
        }
    }

    public bool TryAct(ulong userId, long revision, BlackjackAction action, DateTime now)
    {
        if (Game is not { IsComplete: false } || revision != GetRevision(userId) ||
            GetDeadline(userId) is not { } deadline || now >= deadline)
        {
            return false;
        }

        bool insurance = Game.OfferingInsurance;
        RefreshBalances();

        if (!Game.TryAct(userId, action))
        {
            return false;
        }

        Notice = null;
        AfterAction(userId, insurance, now);
        return true;
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
            return true;
        }

        var notices = new List<string>();
        RefreshBalances();

        foreach (BlackjackPlayer player in _seats.Where(p => Game.NeedsAction(p.Id) && GetDeadline(p.Id) <= now).ToArray())
        {
            bool insurance = Game.OfferingInsurance;
            int seat = _seats.IndexOf(player) + 1;
            notices.Add(insurance ? $"Seat {seat} timed out: insurance declined."
                : $"Seat {seat} timed out: all their remaining hands stood.");
            Game.AutoStandPlayer(player.Id);
            AfterAction(player.Id, insurance, now);

            if (insurance != Game.OfferingInsurance || Game.IsComplete)
            {
                break;
            }
        }

        Notice = string.Join(' ', notices);
        return notices.Count != 0;
    }

    private void Start(DateTime now)
    {
        Shuffled = Shoe.PrepareRound(_seats.Count);
        RefreshBalances();
        Game = new BlackjackGame(Shoe.Draw, _seats, Shoe.MaxHandsPerPlayer);
        SaveBalances();
        Changed(phaseChanged: true);
        ResetDeadlines(now);
    }

    private void AfterAction(ulong userId, bool wasInsurance, DateTime now)
    {
        SaveBalances();
        bool phaseChanged = wasInsurance != Game.OfferingInsurance || Game.IsComplete;
        Changed(userId, phaseChanged);

        if (phaseChanged)
        {
            ResetDeadlines(now);
        }
        else if (Game.NeedsAction(userId))
        {
            _deadlines[userId] = now + TurnTime;
        }
        else
        {
            _deadlines.Remove(userId);
        }
    }

    private void ResetDeadlines(DateTime now)
    {
        _deadlines.Clear();

        foreach (BlackjackPlayer player in _seats.Where(p => Game.NeedsAction(p.Id)))
        {
            _deadlines.Add(player.Id, now + TurnTime);
        }
    }

    private void Changed(ulong? userId = null, bool phaseChanged = false)
    {
        Revision++;

        if (userId is ulong id)
        {
            _playerRevisions[id] = Revision;
        }

        if (phaseChanged)
        {
            _phaseRevision = Revision;
        }
    }

    private void SaveBalances()
    {
        foreach (BlackjackPlayer player in _seats)
        {
            _balances[player.Id] = player.Balance;
        }
    }
}
