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
        public async Task<RankModel> Valorant()
        {
            var tierAndRank = await _ironmanDataService.GetValorantRankAsync(HttpContext.RequestAborted);
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
            var rankAndLP = await _ironmanDataService.GetTFTRankAsync(HttpContext.RequestAborted);
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
            var tierAndRP = await _ironmanDataService.GetApexRankAsync(HttpContext.RequestAborted);

            return new RankModel(
                tierAndRP?.RefreshedAt,
                tierAndRP?.Tier ?? "Unknown",
                tierAndRP?.RP ?? 0,
                tierAndRP?.IconUrl);
        }

        [HttpGet]
        public async Task<CombinedModel> All()
        {
            var valorantTask = Valorant();
            var tftTask = TFT();
            var apexTask = Apex();

            return new CombinedModel(await valorantTask, await tftTask, await apexTask);
        }

        public record RankModel(DateTime? RefreshedAt, string Rank, int PointsInRank, string RankIconUrl);

        public record CombinedModel(RankModel Valorant, RankModel TFT, RankModel Apex);
    }
}
