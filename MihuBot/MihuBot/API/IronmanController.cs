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

        private static readonly string[] s_valorantRankOrder = new[]
        {
            "Iron 1", "Iron 2", "Iron 3",
            "Bronze 1", "Bronze 2", "Bronze 3",
            "Silver 1", "Silver 2", "Silver 3",
            "Gold 1", "Gold 2", "Gold 3",
            "Platinum 1", "Platinum 2", "Platinum 3",
            "Diamond 1", "Diamond 2", "Diamond 3",
            "Ascendant 1", "Ascendant 2", "Ascendant 3",
            "Immortal 1", "Immortal 2", "Immortal 3",
            "Radiant", "Radiant"
        };
        private static readonly string[] s_tftRankOrder = new[]
        {
            "Iron IV", "Iron III", "Iron II", "Iron I",
            "Bronze IV", "Bronze III", "Bronze II", "Bronze I",
            "Silver IV", "Silver III", "Silver II", "Silver I",
            "Gold IV", "Gold III", "Gold II", "Gold I",
            "Platinum IV", "Platinum III", "Platinum II", "Platinum I",
            "Diamond IV", "Diamond III", "Diamond II", "Diamond I",
            "Master I", "Grandmaster I", "Challenger I", "Challenger", "Challenger",
        };
        private static readonly string[] s_apexRankOrder = new[]
        {
            "Rookie 4", "Rookie 3", "Rookie 2", "Rookie 1",
            "Bronze 4", "Bronze 3", "Bronze 2", "Bronze 1",
            "Silver 4", "Silver 3", "Silver 2", "Silver 1",
            "Gold 4", "Gold 3", "Gold 2", "Gold 1",
            "Platinum 4", "Platinum 3", "Platinum 2", "Platinum 1",
            "Diamond 4", "Diamond 3", "Diamond 2", "Diamond 1",
            "Master", "Apex Predator", "Apex Predator"
        };
        private static readonly int[] s_apexRankPoints = new[]
        {
            0, 250, 500, 750,
            1000, 1500, 2000, 2500,
            3000, 3600, 4200, 4800,
            5400, 6100, 6800, 7500,
            8200, 9000, 9800, 10600,
            11400, 12300, 13200, 14100,
            15000, 100000, 100000
        };

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

            var tier = tierAndRank?.Tier ?? "Iron 1";
            var iconTier = tier.Replace(' ', '_');

            var nextRank = s_valorantRankOrder[s_valorantRankOrder.IndexOf(tier) + 1];
            var nextRankIcon = nextRank.Replace(' ', '_');

            const string RankGoal = "Ascendant 3";
            const string GoalIcon = "Ascendant_3";

            return new RankModel(
                tierAndRank?.RefreshedAt,
                tier,
                tierAndRank?.RankInTier ?? 0,
                $"{ImagePathBase}/valorant/{iconTier}.png",
                RankGoal,
                $"{ImagePathBase}/valorant/{GoalIcon}.png",
                nextRank,
                $"{ImagePathBase}/valorant/{nextRankIcon}.png",
                GetIsCompleted("Ironman.Valorant.Completed", tier, static rank => s_valorantRankOrder.IndexOf(rank) >= s_valorantRankOrder.IndexOf(RankGoal)));
        }

        [HttpGet]
        public async Task<RankModel> TFT()
        {
            var rankAndLP = await GetRankAsync(
                static ironman => ironman.TryGetTFTRank(),
                static (ironman, cancellation) => ironman.GetTFTRankAsync(cancellation),
                static stats => stats.RefreshedAt);

            var rank = rankAndLP?.Rank ?? "Iron IV";
            var iconRank = rank.Split(' ')[0];

            var nextRank = s_tftRankOrder[s_tftRankOrder.IndexOf(rank) + 1];
            var nextRankIcon = nextRank.Split(' ')[0];

            const string RankGoal = "Diamond 1";
            const string GoalIcon = "Diamond";

            return new RankModel(
                rankAndLP?.RefreshedAt,
                rank,
                rankAndLP?.LP ?? 0,
                $"{ImagePathBase}/tft/{iconRank}.webp",
                RankGoal,
                $"{ImagePathBase}/tft/{GoalIcon}.webp",
                nextRank,
                $"{ImagePathBase}/tft/{nextRankIcon}.webp",
                GetIsCompleted("Ironman.TFT.Completed", rank, static rank => s_tftRankOrder.IndexOf(rank) >= s_tftRankOrder.IndexOf(RankGoal)));
        }

        [HttpGet]
        public async Task<RankModel> Apex()
        {
            var tierAndRP = await GetRankAsync(
                static ironman => ironman.TryGetApexRank(),
                static (ironman, cancellation) => ironman.GetApexRankAsync(cancellation),
                static stats => stats.RefreshedAt);

            var tier = tierAndRP?.Tier ?? "Rookie 4";
            var iconName = tier.Replace(' ', '_');
            var iconPath = iconName.Contains("Apex", StringComparison.OrdinalIgnoreCase)
                ? "Apex_Predator.png"
                : $"{iconName}.webp";

            var nextRank = s_apexRankOrder[s_apexRankOrder.IndexOf(tier) + 1];
            var nextRankIconName = nextRank.Replace(' ', '_');
            var nextRankIconPath = nextRankIconName.Contains("Apex", StringComparison.OrdinalIgnoreCase)
                ? "Apex_Predator.png"
                : $"{nextRankIconName}.webp";

            const string RankGoal = "Diamond 3";
            const string GoalIcon = "Diamond_3";

            int currentPoints = tierAndRP?.RP ?? 0;
            int rankIndex = s_apexRankOrder.IndexOf(tier);
            int pointsForCurrentRank = s_apexRankPoints[rankIndex];
            int pointsForNextRank = s_apexRankPoints[rankIndex + 1];
            int progressionInRank = (int)((currentPoints - pointsForCurrentRank) / (double)(pointsForNextRank - pointsForCurrentRank));

            return new RankModel(
                tierAndRP?.RefreshedAt,
                tier,
                progressionInRank,
                $"{ImagePathBase}/apex/{iconPath}",
                RankGoal,
                $"{ImagePathBase}/apex/{GoalIcon}.webp",
                nextRank,
                $"{ImagePathBase}/apex/{nextRankIconPath}",
                GetIsCompleted("Ironman.Apex.Completed", tier, static rank => s_apexRankOrder.IndexOf(rank) >= s_apexRankOrder.IndexOf(RankGoal)));
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

        public record RankModel(DateTime? RefreshedAt,
            string Rank, int PointsInRank, string RankIconUrl,
            string RankGoal, string RankGoalIcon,
            string NextRank, string NextRankIcon,
            bool ReachedTop1Percent);

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
