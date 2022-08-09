using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MihuBot.API
{
    [Route("api/[controller]/[action]")]
    public sealed class IronmanController : ControllerBase
    {
        private const int MinimumFreshnessSeconds = 25;

        private readonly IronmanDataService _ironmanDataService;

        public IronmanController(IronmanDataService ironmanDataService)
        {
            _ironmanDataService = ironmanDataService;
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
                $"https://{HttpContext.Request.Host.Value}/valorant/{iconTier}.png");
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
                $"https://{HttpContext.Request.Host.Value}/tft/{iconRank}.webp");
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
                $"https://{HttpContext.Request.Host.Value}/apex/{iconPath}");
        }

        [HttpGet]
        public async Task<CombinedModel> All()
        {
            var valorantTask = Valorant();
            var tftTask = TFT();
            var apexTask = Apex();

            await Task.WhenAll(valorantTask, tftTask, apexTask);

            return new CombinedModel(await valorantTask, await tftTask, await apexTask);
        }

        public record RankModel(DateTime? RefreshedAt, string Rank, int PointsInRank, string RankIconUrl);

        public record CombinedModel(RankModel Valorant, RankModel TFT, RankModel Apex);

        private async ValueTask<T> GetRankAsync<T>(
            Func<IronmanDataService, T> tryGetRank,
            Func<IronmanDataService, CancellationToken, ValueTask<T>> getRankAsync,
            Func<T, DateTime> refreshedAtSelector)
            where T : class
        {
            T rank = tryGetRank(_ironmanDataService);

            if (rank is null || (DateTime.UtcNow - refreshedAtSelector(rank)).TotalSeconds > MinimumFreshnessSeconds)
            {
                rank = await getRankAsync(_ironmanDataService, HttpContext.RequestAborted);
            }

            return rank;
        }
    }
}
