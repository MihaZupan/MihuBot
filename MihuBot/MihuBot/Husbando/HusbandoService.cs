using MihuBot.Helpers;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MihuBot.Husbando
{
    internal sealed class HusbandoService : IHusbandoService
    {
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, (List<ulong> Husbandos, List<ulong> Waifus)>> _husbandos =
            new SynchronizedLocalJsonStore<Dictionary<ulong, (List<ulong> Husbandos, List<ulong> Waifus)>>("Husbandos.json");

        private readonly IConnectionMultiplexer _redis;

        public HusbandoService(IConnectionMultiplexer redis)
        {
            _redis = redis;

            if (_husbandos.QueryAsync(i => i).Result.Count == 0)
            {
                Task.Run(async () =>
                {
                    var server = _redis.GetServer(Secrets.Redis.DatabaseAddress, 6379);

                    var keys = new List<(RedisKey Key, ulong Id, bool Husbando)>();

                    await foreach (var husbandoKey in server.KeysAsync(pattern: "husbando-*"))
                        keys.Add((husbandoKey, ulong.Parse(husbandoKey.ToString().Split('-')[1]), true));

                    await foreach (var waifuKey in server.KeysAsync(pattern: "waifu-*"))
                        keys.Add((waifuKey, ulong.Parse(waifuKey.ToString().Split('-')[1]), false));

                    var husbandos = await _husbandos.EnterAsync();
                    try
                    {
                        var db = _redis.GetDatabase();
                        foreach (var key in keys)
                        {
                            if (!husbandos.TryGetValue(key.Id, out (List<ulong> Husbandos, List<ulong> Waifus) matches))
                            {
                                matches = husbandos[key.Id] = (new List<ulong>(), new List<ulong>());
                            }

                            var newMatches = await db.SetScanAsync(key.Key).ToArrayAsync();

                            (key.Husbando ? matches.Husbandos : matches.Waifus)
                                .AddRange(newMatches.Select(m => ulong.Parse(m)));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                    finally
                    {
                        _husbandos.Exit();
                    }
                });
            }
        }

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
