using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using MihuBot.RuntimeUtils.AI;
using Octokit;

namespace MihuBot.API;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExistingAreaLabelPrediction(
    [Required, StringLength(100), RegularExpression(@"[^\r\n]+")] string LabelName,
    [Required, Range(0d, 1d)] double? Confidence);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AreaLabelPredictionRequest(
    [Required, RegularExpression(@"[A-Za-z0-9][A-Za-z0-9-]{0,38}/(?!\.{1,2}$)[A-Za-z0-9_.-]{1,100}")] string Repository,
    [Range(1, int.MaxValue)] int Number,
    [Required, StringLength(100)] string LabelPrefix = "area-",
    ExistingAreaLabelPrediction ExistingPrediction = null);

[Route("api/RuntimeUtils/[controller]")]
[ApiController]
[EnableRateLimiting("area-labels")]
public sealed class AreaLabelsController(AreaLabelDetector detector, ILogger<AreaLabelsController> logger) : ControllerBase
{
    [HttpPost("Predict")]
    [RequestSizeLimit(1024)]
    public async Task<ActionResult<AreaLabelSuggestion[]>> Predict([FromBody] AreaLabelPredictionRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            long start = Stopwatch.GetTimestamp();
            var suggestions = await detector.PredictAsync(request.Repository, request.Number, request.LabelPrefix, timeout.Token);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            if (request.ExistingPrediction is { } existing)
            {
                string predictions = suggestions.Length == 0 ? "none" : string.Join(", ", suggestions.Select(s => $"{s.LabelName} ({s.Confidence:P0})"));
                logger.LogInformation("Area label prediction comparison for <https://github.com/{Repository}/issues/{Number}> in {ElapsedSeconds:F2}s: ML {ExistingLabel} ({ExistingConfidence:P0}); LLM {Predictions}",
                    request.Repository, request.Number, elapsed.TotalSeconds, existing.LabelName, existing.Confidence, predictions);
            }
            return suggestions;
        }
        catch (NotFoundException)
        {
            return NotFound(new ProblemDetails { Title = "Tracked public repository or requested item not found." });
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            logger.LogWarning(ex, "Failed to fetch public GitHub data for {Repository}#{Number}", request.Repository, request.Number);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ProblemDetails { Title = "GitHub is temporarily unavailable or rate limited." });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Area label prediction timed out for {Repository}#{Number}", request.Repository, request.Number);
            return StatusCode(StatusCodes.Status504GatewayTimeout, new ProblemDetails { Title = "Area label prediction timed out." });
        }
    }
}
