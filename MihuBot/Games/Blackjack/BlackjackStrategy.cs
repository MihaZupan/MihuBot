namespace MihuBot.Games.Blackjack;

internal static class BlackjackStrategy
{
    internal static BrowserBlackjackAdvice Analyze(
        int decks, IReadOnlyList<int> exposed, BlackjackHand hand, int dealer, bool insurance,
        IReadOnlyList<BrowserBlackjackCommand> actions)
    {
        int running = exposed[2] + exposed[3] + exposed[4] + exposed[5] + exposed[6] - exposed[1] - exposed[10];
        int unseen = (decks * 52) - exposed.Sum();
        double unseenDecks = unseen / 52d;
        double trueCount = running / unseenDecks;
        int indexCount = (int)Math.Floor(trueCount);

        BrowserBlackjackAdvice Advice(BrowserBlackjackCommand? move, string explanation, bool deviation = false) =>
            new(running, trueCount, unseenDecks, move, explanation, deviation);

        bool Can(BrowserBlackjackCommand action) => actions.Contains(action);

        if (hand is null)
        {
            return Advice(null, "Advice appears on your turn. Counts include all cards exposed since the last shuffle.");
        }

        if (insurance)
        {
            // No peek has happened yet: every unseen card is equally likely to be the hole card.
            bool profitable = ((decks * 16) - exposed[10]) * 3 > unseen;
            bool insure = profitable && Can(BrowserBlackjackCommand.Insure);
            return Advice(insure ? BrowserBlackjackCommand.Insure : BrowserBlackjackCommand.DeclineInsurance,
                profitable ? insure ? "More than one third of unseen cards are ten-valued: insurance has positive expected value."
                    : "Insurance has positive expected value, but you do not have enough uncommitted chips."
                    : "At most one third of unseen cards are ten-valued: decline insurance.",
                insure);
        }

        BrowserBlackjackCommand basic = BasicStrategy(hand, dealer, actions);
        int total = hand.Value.Total;

        BrowserBlackjackAdvice Indexed(BrowserBlackjackCommand high, BrowserBlackjackCommand low, int threshold)
        {
            BrowserBlackjackCommand move = indexCount >= threshold ? high : low;
            return Advice(move,
                $"Hi-Lo index {threshold:+0;-0;0}; current floored true count {indexCount:+0;-0;0}. " +
                $"{high} at or above the index, otherwise {low}.", move != basic);
        }

        if (Can(BrowserBlackjackCommand.Split) && hand.Cards.All(c => c.Value == 10) && dealer is 5 or 6)
        {
            return Indexed(BrowserBlackjackCommand.Split, BrowserBlackjackCommand.Stand, dealer == 5 ? 5 : 4);
        }

        if (basic == BrowserBlackjackCommand.Split || hand.Value.Soft)
        {
            return Advice(basic, "Basic strategy for 4-8 decks, stand on soft 17, double after split, and late surrender.");
        }

        if (Can(BrowserBlackjackCommand.Surrender))
        {
            int? surrenderIndex = (total, dealer) switch
            {
                (14, 10) => 3,
                (15, 10) => 0,
                (15, 9) => 2,
                (15, 1) => 1,
                _ => null
            };

            if (surrenderIndex is int threshold)
            {
                return Indexed(BrowserBlackjackCommand.Surrender, BrowserBlackjackCommand.Hit, threshold);
            }

            if (basic == BrowserBlackjackCommand.Surrender)
            {
                return Advice(basic, "Late surrender is available on this original two-card hand.");
            }
        }

        int? standIndex = (total, dealer) switch
        {
            (16, 10) => 0,
            (15, 10) => 4,
            (12, 3) => 2,
            (12, 2) => 3,
            (16, 9) => 5,
            (13, 2) => -1,
            (12, 4) => 0,
            (12, 5) => -2,
            (12, 6) => -1,
            (13, 3) => -2,
            _ => null
        };

        if (standIndex is int stand)
        {
            return Indexed(BrowserBlackjackCommand.Stand, BrowserBlackjackCommand.Hit, stand);
        }

        if (Can(BrowserBlackjackCommand.Double))
        {
            int? doubleIndex = (total, dealer) switch
            {
                (10, 10 or 1) => 4,
                (11, 1) => 1,
                (9, 2) => 1,
                (9, 7) => 3,
                _ => null
            };

            if (doubleIndex is int doubling)
            {
                return Indexed(BrowserBlackjackCommand.Double, BrowserBlackjackCommand.Hit, doubling);
            }
        }

        return Advice(basic, "Basic strategy, restricted to the moves available for this hand and bankroll.");
    }

    internal static BrowserBlackjackCommand BasicStrategy(
        BlackjackHand hand, int dealer, IReadOnlyList<BrowserBlackjackCommand> actions)
    {
        bool Can(BrowserBlackjackCommand action) => actions.Contains(action);
        int total = hand.Value.Total;
        bool soft = hand.Value.Soft;
        bool pair = hand.Cards.Count == 2 && hand.Cards[0].Value == hand.Cards[1].Value;
        int rank = hand.Cards[0].Value;

        if (!soft && Can(BrowserBlackjackCommand.Surrender) &&
            ((total == 16 && dealer is 9 or 10 or 1 && !(pair && rank == 8 && Can(BrowserBlackjackCommand.Split))) ||
             (total == 15 && dealer == 10)))
        {
            return BrowserBlackjackCommand.Surrender;
        }

        if (pair && Can(BrowserBlackjackCommand.Split) && (rank switch
        {
            1 or 8 => true,
            2 or 3 or 7 => dealer is >= 2 and <= 7,
            4 => dealer is 5 or 6,
            6 => dealer is >= 2 and <= 6,
            9 => dealer is (>= 2 and <= 6) or 8 or 9,
            _ => false
        }))
        {
            return BrowserBlackjackCommand.Split;
        }

        bool doubleDown = soft ? total switch
        {
            13 or 14 => dealer is 5 or 6,
            15 or 16 => dealer is >= 4 and <= 6,
            17 or 18 => dealer is >= 3 and <= 6,
            _ => false
        } : total switch
        {
            9 => dealer is >= 3 and <= 6,
            10 => dealer is >= 2 and <= 9,
            11 => dealer != 1,
            _ => false
        };

        if (doubleDown && Can(BrowserBlackjackCommand.Double))
        {
            return BrowserBlackjackCommand.Double;
        }

        bool stand = soft ? total >= 19 || (total == 18 && dealer is >= 2 and <= 8)
            : total >= 17 || (total == 12 && dealer is >= 4 and <= 6) || (total is >= 13 and <= 16 && dealer is >= 2 and <= 6);
        return stand ? BrowserBlackjackCommand.Stand : BrowserBlackjackCommand.Hit;
    }
}
