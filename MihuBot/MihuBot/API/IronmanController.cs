using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MihuBot.Configuration;

namespace MihuBot.API
{
    [Route("api/[controller]/[action]")]
    public sealed class IronmanController : ControllerBase
    {
        private const int MinimumFreshnessSeconds = 30;
        private const int HotCacheDurationSeconds = 10 * 60;
        private const int HotCacheRefreshSeconds = MinimumFreshnessSeconds - 5;
        private const int CacheControlMaxAge = 15;

        private static readonly Timer s_hotCacheTimer = new(_ => {
            if (Environment.TickCount64 - Volatile.Read(ref s_lastAccessedTicks) < HotCacheDurationSeconds * 1000)
            {
                s_lastIronmanDataService?.TryGetValorantRank();
                s_lastIronmanDataService?.TryGetTFTRank();
                s_lastIronmanDataService?.TryGetApexRank();
            }
        }, s_hotCacheTimer, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(HotCacheRefreshSeconds));

        private static long s_lastAccessedTicks = 0;
        private static IronmanDataService s_lastIronmanDataService;

        private readonly IronmanDataService _ironmanDataService;
        private readonly IConfigurationService _configuration;

        private string ImagePathBase => $"https://{HttpContext.Request.Host.Value}/ironman";

        public IronmanController(IronmanDataService ironmanDataService, IConfigurationService configuration)
        {
            _ironmanDataService = ironmanDataService;
            _configuration = configuration;
            s_lastIronmanDataService = ironmanDataService;
        }

        [HttpGet]
        public async Task<RankModel> Valorant()
        {
            var tierAndRank = await GetRankAsync(
                static ironman => ironman.TryGetValorantRank(),
                static (ironman, cancellation) => ironman.GetValorantRankAsync(cancellation),
                static stats => stats.RefreshedAt);

            var iconTier = tierAndRank?.Tier?.Replace(' ', '_') ?? "Iron_1";

            return new RankModel(
                tierAndRank?.RefreshedAt,
                tierAndRank?.Tier ?? "Unknown",
                tierAndRank?.RankInTier ?? 0,
                $"{ImagePathBase}/valorant/{iconTier}.png",
                GetIsCompleted("Ironman.Valorant.Completed", iconTier, static tier => ContainsAny(tier, "Ascendant_3", "Immortal", "Radiant")));
        }

        [HttpGet]
        public async Task<RankModel> TFT()
        {
            var rankAndLP = await GetRankAsync(
                static ironman => ironman.TryGetTFTRank(),
                static (ironman, cancellation) => ironman.GetTFTRankAsync(cancellation),
                static stats => stats.RefreshedAt);

            var iconRank = rankAndLP?.Rank?.Split(' ')[0] ?? "Iron";

            return new RankModel(
                rankAndLP?.RefreshedAt,
                rankAndLP?.Rank ?? "Unknown",
                rankAndLP?.LP ?? 0,
                $"{ImagePathBase}/tft/{iconRank}.webp",
                GetIsCompleted("Ironman.TFT.Completed", iconRank, static rank => ContainsAny(rank, "Master", "Grandmaster", "Challenger")));
        }

        [HttpGet]
        public async Task<RankModel> Apex()
        {
            var tierAndRP = await GetRankAsync(
                static ironman => ironman.TryGetApexRank(),
                static (ironman, cancellation) => ironman.GetApexRankAsync(cancellation),
                static stats => stats.RefreshedAt);

            var iconName = tierAndRP?.Tier?.Replace(' ', '_') ?? "Rookie_4";
            var iconPath = iconName.Contains("Apex", StringComparison.OrdinalIgnoreCase)
                ? "Apex_Predator.png"
                : $"{iconName}.webp";

            return new RankModel(
                tierAndRP?.RefreshedAt,
                tierAndRP?.Tier ?? "Unknown",
                tierAndRP?.RP ?? 0,
                $"{ImagePathBase}/apex/{iconPath}",
                GetIsCompleted("Ironman.Apex.Completed", iconPath, static rank => ContainsAny(rank, "Diamond_3", "Diamond_2", "Diamond_1", "Master", "Predator")));
        }

        [HttpGet]
        public async Task<CombinedModel> All()
        {
            var valorantTask = Valorant();
            var tftTask = TFT();
            var apexTask = Apex();

            await Task.WhenAll(valorantTask, tftTask, apexTask);

            var valorant = await valorantTask;
            var tft = await tftTask;
            var apex = await apexTask;

            if (valorant.RefreshedAt is { } valorantAge &&
                tft.RefreshedAt is { } tftAge &&
                apex.RefreshedAt is { } apexAge)
            {
                var minRefreshedAt = new DateTime(Math.Min(valorantAge.Ticks, Math.Min(tftAge.Ticks, apexAge.Ticks)), DateTimeKind.Utc);
                var age = (ulong)(DateTime.UtcNow - minRefreshedAt).TotalSeconds;
                if (age < CacheControlMaxAge)
                {
                    Response.Headers.CacheControl = $"public,max-age={Math.Min(5, CacheControlMaxAge - age)}";
                }
            }

            return new CombinedModel(valorant, tft, apex);
        }

        public record RankModel(DateTime? RefreshedAt, string Rank, int PointsInRank, string RankIconUrl, bool ReachedTop1Percent);

        public record CombinedModel(RankModel Valorant, RankModel TFT, RankModel Apex);

        private async ValueTask<T> GetRankAsync<T>(
            Func<IronmanDataService, T> tryGetRank,
            Func<IronmanDataService, CancellationToken, ValueTask<T>> getRankAsync,
            Func<T, DateTime> refreshedAtSelector)
            where T : class
        {
            Volatile.Write(ref s_lastAccessedTicks, Environment.TickCount64);

            T rank = tryGetRank(_ironmanDataService);

            if (rank is null || (DateTime.UtcNow - refreshedAtSelector(rank)).TotalSeconds > MinimumFreshnessSeconds)
            {
                rank = await getRankAsync(_ironmanDataService, HttpContext.RequestAborted);
            }

            return rank;
        }

        private bool GetIsCompleted<T>(string configKey, T state, Func<T, bool> checkCompleted)
        {
            if (_configuration.TryGet(null, configKey, out _))
            {
                return true;
            }

            if (checkCompleted(state))
            {
                _configuration.Set(null, configKey, "1");
                return true;
            }

            return false;
        }

        private static bool ContainsAny(string text, params string[] substrings)
        {
            foreach (string s in substrings)
            {
                if (text.Contains(s, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
