using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MihuBot.API
{
    [Route("api/[controller]/[action]")]
    public sealed class IronmanController : ControllerBase
    {
        private readonly IronmanDataService _ironmanDataService;

        public IronmanController(IronmanDataService ironmanDataService)
        {
            _ironmanDataService = ironmanDataService;
        }

        [HttpGet]
        public async Task<RankModel> Valorant(bool waitForRefresh = true)
        {
            var tierAndRank = waitForRefresh
                ? await _ironmanDataService.GetValorantRankAsync(HttpContext.RequestAborted)
                : _ironmanDataService.TryGetValorantRank();

            var iconTier = tierAndRank?.Tier?.Replace(' ', '_') ?? "Iron_1";

            return new RankModel(
                tierAndRank?.RefreshedAt,
                tierAndRank?.Tier ?? "Unknown",
                tierAndRank?.RankInTier ?? 0,
                $"https://{HttpContext.Request.Host.Value}/valorant/{iconTier}.png");
        }

        [HttpGet]
        public async Task<RankModel> TFT(bool waitForRefresh = true)
        {
            var rankAndLP = waitForRefresh
                ? await _ironmanDataService.GetTFTRankAsync(HttpContext.RequestAborted)
                : _ironmanDataService.TryGetTFTRank();

            var iconRank = rankAndLP?.Rank?.Split(' ')[0] ?? "Iron";

            return new RankModel(
                rankAndLP?.RefreshedAt,
                rankAndLP?.Rank ?? "Unknown",
                rankAndLP?.LP ?? 0,
                $"https://{HttpContext.Request.Host.Value}/tft/{iconRank}.webp");
        }

        [HttpGet]
        public async Task<RankModel> Apex(bool waitForRefresh = true)
        {
            var tierAndRP = waitForRefresh
                ? await _ironmanDataService.GetApexRankAsync(HttpContext.RequestAborted)
                : _ironmanDataService.TryGetApexRank();

            return new RankModel(
                tierAndRP?.RefreshedAt,
                tierAndRP?.Tier ?? "Unknown",
                tierAndRP?.RP ?? 0,
                tierAndRP?.IconUrl);
        }

        [HttpGet]
        public async Task<CombinedModel> All(bool waitForRefresh = true)
        {
            var valorantTask = Valorant(waitForRefresh);
            var tftTask = TFT(waitForRefresh);
            var apexTask = Apex(waitForRefresh);

            return new CombinedModel(await valorantTask, await tftTask, await apexTask);
        }

        public record RankModel(DateTime? RefreshedAt, string Rank, int PointsInRank, string RankIconUrl);

        public record CombinedModel(RankModel Valorant, RankModel TFT, RankModel Apex);
    }
}
