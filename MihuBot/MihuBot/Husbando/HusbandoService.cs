using MihuBot.Helpers;

namespace MihuBot.Husbando
{
    internal sealed class HusbandoService : IHusbandoService
    {
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, (List<ulong> Husbandos, List<ulong> Waifus)>> _husbandos =
            new SynchronizedLocalJsonStore<Dictionary<ulong, (List<ulong> Husbandos, List<ulong> Waifus)>>("Husbandos.json");

        public async ValueTask<ulong?> TryGetRandomMatchAsync(bool husbando, ulong user)
        {
            return await _husbandos.QueryAsync(husbandos =>
            {
                if (!husbandos.TryGetValue(user, out var matches))
                    return null;

                var list = husbando ? matches.Husbandos : matches.Waifus;
                return list.Count == 0
                    ? (ulong?)null
                    : list.Random();
            });
        }

        public async ValueTask<bool> AddMatchAsync(bool husbando, ulong user, ulong target)
        {
            var husbandos = await _husbandos.EnterAsync();
            try
            {
                if (!husbandos.TryGetValue(user, out var matches))
                {
                    matches = husbandos[user] = (new List<ulong>(), new List<ulong>());
                }

                var list = husbando ? matches.Husbandos : matches.Waifus;
                if (list.Contains(target))
                    return false;

                list.Add(target);
                return true;
            }
            finally
            {
                _husbandos.Exit();
            }
        }

        public async ValueTask<bool> RemoveMatchAsync(bool husbando, ulong user, ulong target)
        {
            var husbandos = await _husbandos.EnterAsync();
            try
            {
                if (!husbandos.TryGetValue(user, out var matches))
                    return false;

                var list = husbando ? matches.Husbandos : matches.Waifus;
                return list.Remove(target);
            }
            finally
            {
                _husbandos.Exit();
            }
        }

        public async ValueTask<ulong[]> GetAllMatchesAsync(bool husbando, ulong user)
        {
            return await _husbandos.QueryAsync(husbandos =>
            {
                if (!husbandos.TryGetValue(user, out var matches))
                    return Array.Empty<ulong>();

                return (husbando ? matches.Husbandos : matches.Waifus).ToArray();
            });
        }

        public async ValueTask<ulong[]> GetAllUsersAsync()
        {
            return await _husbandos.QueryAsync(husbandos => husbandos.Keys.ToArray());
        }
    }
}
