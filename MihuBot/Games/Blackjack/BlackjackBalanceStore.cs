namespace MihuBot.Games.Blackjack;

internal sealed class BlackjackBalanceStore
{
    private const long StartingHalfChips = (long)(BlackjackTable.StartingChips * 2);
    private const ulong HouseProfitKey = 0;
    private readonly SynchronizedLocalJsonStore<Dictionary<ulong, long>> _store;

    internal BlackjackBalanceStore()
    {
        _store = new SynchronizedLocalJsonStore<Dictionary<ulong, long>>(new Dictionary<ulong, long>());
    }

    internal BlackjackBalanceStore(string path, Action<string, string> replaceFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        _store = new SynchronizedLocalJsonStore<Dictionary<ulong, long>>(path, (_, balances) =>
        {
            if (balances is null || balances.Any(p => p.Key != HouseProfitKey && p.Value < 0))
            {
                throw new InvalidDataException("The blackjack balance file has invalid balances.");
            }

            return balances;
        }, replaceFile);
    }

    public decimal GetBalance(ulong userId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(userId);
        return _store.Query(balances => balances.GetValueOrDefault(userId, StartingHalfChips) / 2m);
    }

    public decimal HouseProfit => _store.Query(balances => balances.GetValueOrDefault(HouseProfitKey) / 2m);

    public void ApplyResults(IReadOnlyDictionary<ulong, long> halfChipChanges)
    {
        _store.Modify(balances =>
        {
            long houseProfit = balances.GetValueOrDefault(HouseProfitKey);

            foreach ((ulong id, long change) in halfChipChanges)
            {
                ArgumentOutOfRangeException.ThrowIfZero(id);
                long balance = checked(balances.GetValueOrDefault(id, StartingHalfChips) + change);
                ArgumentOutOfRangeException.ThrowIfNegative(balance);
                houseProfit = checked(houseProfit - change);
            }

            foreach ((ulong id, long change) in halfChipChanges)
            {
                balances[id] = balances.GetValueOrDefault(id, StartingHalfChips) + change;
            }

            balances[HouseProfitKey] = houseProfit;
        });
    }

    public void Rebuy(ulong userId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(userId);
        _store.Modify(balances => balances[userId] = StartingHalfChips);
    }
}
