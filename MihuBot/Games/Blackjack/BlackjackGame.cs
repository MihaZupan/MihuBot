using System.Security.Cryptography;

namespace MihuBot.Games.Blackjack;

internal readonly record struct BlackjackCard(int Rank, int Suit)
{
    public int Value => Math.Min(Rank, 10);

    public override string ToString() =>
        $"{Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => Rank.ToString() }}{"CDHS"[Suit]}";
}

internal sealed class BlackjackShoe
{
    public const int Decks = 4;

    private readonly List<BlackjackCard> _cards = [];

    public int Remaining => _cards.Count;
    public int Number { get; private set; }
    public int DeckCount { get; }
    public int CutCardRemaining => DeckCount * 52 / 4;
    public int MaxHandsPerPlayer => GetMaxHandsPerPlayer(DeckCount);

    public static int GetMaxHandsPerPlayer(int decks) => decks == 2 ? 3 : 4;

    public BlackjackShoe(int decks = Decks)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(decks, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decks, 8);
        DeckCount = decks;
    }

    internal BlackjackShoe(IEnumerable<BlackjackCard> cards, int decks = Decks) : this(decks)
    {
        _cards.AddRange(cards.Reverse());
        Number = 1;
    }

    public bool PrepareRound(int playerCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(playerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(playerCount, BlackjackGame.MaxPlayers);

        // Each finished hand uses at most 30 points of cards (aces counted as one).
        // Reserve enough for every permitted split hand plus the dealer, even in a low-card shoe.
        int reserve = 30 * ((MaxHandsPerPlayer * playerCount) + 1);

        // A full deck contains 340 points when aces count as one.
        if (DeckCount * 340 <= reserve)
        {
            throw new ArgumentOutOfRangeException(nameof(playerCount), "The shoe is too small to safely support this many players.");
        }

        if (Remaining > CutCardRemaining && _cards.Sum(c => c.Value) > reserve)
        {
            return false;
        }

        _cards.Clear();

        for (int deck = 0; deck < DeckCount; deck++)
        {
            for (int suit = 0; suit < 4; suit++)
            {
                for (int rank = 1; rank <= 13; rank++)
                {
                    _cards.Add(new(rank, suit));
                }
            }
        }

        for (int i = _cards.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (_cards[i], _cards[j]) = (_cards[j], _cards[i]);
        }

        Number++;
        return true;
    }

    public BlackjackCard Draw()
    {
        // Never reshuffle during a hand: the cut card leaves ample reserve for splits.
        BlackjackCard card = _cards[^1];
        _cards.RemoveAt(_cards.Count - 1);
        return card;
    }
}

internal enum BlackjackAction
{
    Hit,
    Stand,
    Double,
    Split,
    Surrender,
    Insure,
    DeclineInsurance
}

internal sealed class BlackjackHand
{
    internal readonly List<BlackjackCard> Cards = [];

    public decimal Bet { get; internal set; }
    public bool FromSplit { get; init; }
    public bool Finished { get; internal set; }
    public bool Surrendered { get; internal set; }
    public decimal Returned { get; internal set; }
    public string Result { get; internal set; }
    public (int Total, bool Soft) Value => Evaluate(Cards);
    public bool IsBlackjack => !FromSplit && Cards.Count == 2 && Value.Total == 21;

    internal static (int Total, bool Soft) Evaluate(IEnumerable<BlackjackCard> cards)
    {
        int total = 0;
        bool ace = false;

        foreach (BlackjackCard card in cards)
        {
            total += card.Value;
            ace |= card.Rank == 1;
        }

        bool soft = ace && total <= 11;
        return (total + (soft ? 10 : 0), soft);
    }
}

internal sealed class BlackjackPlayer
{
    public ulong Id { get; }
    public string Name { get; }
    public decimal StartingBalance { get; }
    public decimal Balance { get; internal set; }
    public decimal InsuranceBet { get; internal set; }
    public decimal InsuranceReturned { get; internal set; }
    public bool InsuranceDecided { get; internal set; }
    public int ActiveHandIndex { get; internal set; }
    public List<BlackjackHand> Hands { get; } = [];
    public BlackjackHand ActiveHand => ActiveHandIndex < Hands.Count ? Hands[ActiveHandIndex] : null;

    public BlackjackPlayer(ulong id, string name, decimal balance, decimal bet)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bet);
        ArgumentOutOfRangeException.ThrowIfLessThan(balance, bet);

        Id = id;
        Name = name;
        StartingBalance = balance;
        Balance = balance - bet;
        Hands.Add(new BlackjackHand { Bet = bet });
    }
}

internal sealed class BlackjackGame
{
    public const int MaxPlayers = 6;

    private readonly Func<BlackjackCard> _draw;
    private readonly List<BlackjackCard> _dealer = [];

    public IReadOnlyList<BlackjackPlayer> Players { get; }
    public int MaxHandsPerPlayer { get; }
    public IReadOnlyList<BlackjackCard> Dealer => _dealer;
    public bool OfferingInsurance { get; private set; }
    public bool IsComplete { get; private set; }
    public BlackjackPlayer GetPlayer(ulong userId) => Players.FirstOrDefault(p => p.Id == userId);

    public bool NeedsAction(ulong userId) => !IsComplete && GetPlayer(userId) is { } player &&
        (OfferingInsurance ? !player.InsuranceDecided : player.ActiveHand is not null);

    public BlackjackGame(Func<BlackjackCard> draw, IReadOnlyList<BlackjackPlayer> players, int maxHandsPerPlayer = 4)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentOutOfRangeException.ThrowIfLessThan(players.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(players.Count, MaxPlayers);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHandsPerPlayer, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxHandsPerPlayer, 4);

        if (players.Select(p => p.Id).Distinct().Count() != players.Count ||
            players.Any(p => p.Hands.Count != 1 || p.Hands[0].Cards.Count != 0))
        {
            throw new ArgumentException("Players must have unique IDs and undealt hands.", nameof(players));
        }

        _draw = draw;
        Players = players.ToArray();
        MaxHandsPerPlayer = maxHandsPerPlayer;

        for (int pass = 0; pass < 2; pass++)
        {
            foreach (BlackjackPlayer player in Players)
            {
                player.Hands[0].Cards.Add(_draw());
            }

            _dealer.Add(_draw());
        }

        OfferingInsurance = _dealer[0].Rank == 1;

        if (!OfferingInsurance)
        {
            Peek();
        }
    }

    public bool CanAct(ulong userId, BlackjackAction action)
    {
        if (!NeedsAction(userId))
        {
            return false;
        }

        BlackjackPlayer player = GetPlayer(userId);

        if (OfferingInsurance)
        {
            return action == BlackjackAction.DeclineInsurance ||
                (action == BlackjackAction.Insure && player.Balance >= player.ActiveHand.Bet / 2);
        }

        BlackjackHand hand = player.ActiveHand;

        return action switch
        {
            BlackjackAction.Hit or BlackjackAction.Stand => true,
            BlackjackAction.Double => hand.Cards.Count == 2 && player.Balance >= hand.Bet,
            BlackjackAction.Split => hand.Cards.Count == 2 && player.Hands.Count < MaxHandsPerPlayer &&
                hand.Cards[0].Value == hand.Cards[1].Value && player.Balance >= hand.Bet,
            BlackjackAction.Surrender => !hand.FromSplit && hand.Cards.Count == 2,
            _ => false
        };
    }

    public bool TryAct(ulong userId, BlackjackAction action)
    {
        if (!CanAct(userId, action))
        {
            return false;
        }

        BlackjackPlayer player = GetPlayer(userId);
        BlackjackHand hand = player.ActiveHand;

        switch (action)
        {
            case BlackjackAction.Insure:
                player.InsuranceBet = hand.Bet / 2;
                player.Balance -= player.InsuranceBet;
                goto case BlackjackAction.DeclineInsurance;

            case BlackjackAction.DeclineInsurance:
                player.InsuranceDecided = true;

                if (Players.All(p => p.InsuranceDecided))
                {
                    OfferingInsurance = false;
                    Peek();
                }

                return true;

            case BlackjackAction.Hit:
                hand.Cards.Add(_draw());
                hand.Finished = hand.Value.Total >= 21;
                break;

            case BlackjackAction.Stand:
                hand.Finished = true;
                break;

            case BlackjackAction.Double:
                player.Balance -= hand.Bet;
                hand.Bet *= 2;
                hand.Cards.Add(_draw());
                hand.Finished = true;
                break;

            case BlackjackAction.Split:
                player.Balance -= hand.Bet;
                var second = new BlackjackHand { Bet = hand.Bet, FromSplit = true };
                second.Cards.Add(hand.Cards[1]);
                var first = new BlackjackHand { Bet = hand.Bet, FromSplit = true };
                first.Cards.Add(hand.Cards[0]);
                player.Hands[player.ActiveHandIndex] = first;
                player.Hands.Insert(player.ActiveHandIndex + 1, second);
                first.Cards.Add(_draw());
                first.Finished = first.Cards[0].Rank == 1 || first.Value.Total == 21;
                break;

            case BlackjackAction.Surrender:
                hand.Surrendered = true;
                hand.Finished = true;
                break;

            default:
                throw new InvalidOperationException($"Unexpected blackjack action: {action}");
        }

        Advance(player);
        FinishDealer();
        return true;
    }

    public void AutoStand()
    {
        while (!IsComplete)
        {
            foreach (BlackjackPlayer player in Players)
            {
                AutoStandPlayer(player.Id);
            }
        }
    }

    public void AutoStandPlayer(ulong userId)
    {
        if (OfferingInsurance)
        {
            TryAct(userId, BlackjackAction.DeclineInsurance);
            return;
        }

        while (NeedsAction(userId))
        {
            TryAct(userId, BlackjackAction.Stand);
        }
    }

    private void Peek()
    {
        bool dealerBlackjack = BlackjackHand.Evaluate(_dealer).Total == 21;

        if (dealerBlackjack)
        {
            Settle();
            return;
        }

        foreach (BlackjackPlayer player in Players)
        {
            player.Hands[0].Finished = player.Hands[0].IsBlackjack;
            Advance(player);
        }

        FinishDealer();
    }

    private void Advance(BlackjackPlayer player)
    {
        while (player.ActiveHandIndex < player.Hands.Count)
        {
            BlackjackHand hand = player.ActiveHand;

            // A player's split hands remain sequential, independent of the other seats.
            if (hand.Cards.Count == 1)
            {
                hand.Cards.Add(_draw());
                hand.Finished = hand.Cards[0].Rank == 1 || hand.Value.Total == 21;
            }

            if (!hand.Finished)
            {
                return;
            }

            player.ActiveHandIndex++;
        }
    }

    private void FinishDealer()
    {
        if (IsComplete || Players.Any(p => p.ActiveHand is not null))
        {
            return;
        }

        if (Players.SelectMany(p => p.Hands).Any(h => !h.Surrendered && !h.IsBlackjack && h.Value.Total <= 21))
        {
            while (BlackjackHand.Evaluate(_dealer).Total < 17)
            {
                _dealer.Add(_draw());
            }
        }

        Settle();
    }

    private void Settle()
    {
        int dealerTotal = BlackjackHand.Evaluate(_dealer).Total;
        bool dealerBlackjack = _dealer.Count == 2 && dealerTotal == 21;

        foreach (BlackjackPlayer player in Players)
        {
            player.InsuranceReturned = dealerBlackjack ? player.InsuranceBet * 3 : 0;
            player.Balance += player.InsuranceReturned;

            foreach (BlackjackHand hand in player.Hands)
            {
                int total = hand.Value.Total;
                (hand.Result, hand.Returned) = (hand.Surrendered, total > 21, dealerBlackjack, hand.IsBlackjack) switch
                {
                    (true, _, _, _) => ("Surrender", hand.Bet / 2),
                    (_, true, _, _) => ("Bust", 0m),
                    (_, _, true, true) => ("Push", hand.Bet),
                    (_, _, true, false) => ("Dealer blackjack", 0m),
                    (_, _, false, true) => ("Blackjack (3:2)", hand.Bet * 2.5m),
                    _ when dealerTotal > 21 || total > dealerTotal => ("Win", hand.Bet * 2),
                    _ when total == dealerTotal => ("Push", hand.Bet),
                    _ => ("Loss", 0m)
                };

                hand.Finished = true;
                player.Balance += hand.Returned;
            }
        }

        IsComplete = true;
    }
}
