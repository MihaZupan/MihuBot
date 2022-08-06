using System.Net.Http.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot
{
    public sealed class IronmanDataService
    {
        private const int RefreshAfterMinimumMs = 10 * 1000;
        private const int DropStaleDataAfterMs = 2 * 60 * 60 * 1000;

        private readonly HttpClient _httpClient;
        private readonly Logger _logger;
        private MmrResponseCurrentData? _lastResponseData;
        private Task? _currentFetchOperation;
        private long _lastRefreshTicks = -RefreshAfterMinimumMs;

        private long ElapsedMs => Environment.TickCount64 - _lastRefreshTicks;

        public IronmanDataService(HttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public (string? Tier, int Ranking) TryGetCurrentRank()
        {
            var task = GetCurrentRankAsyncCore(block: false, CancellationToken.None);
            Debug.Assert(task.IsCompletedSuccessfully);
            return task.Result;
        }

        public ValueTask<(string? Tier, int Ranking)> GetCurrentRankAsync(CancellationToken cancellation)
        {
            return GetCurrentRankAsyncCore(block: true, cancellation);
        }

        private async ValueTask<(string? Tier, int Ranking)> GetCurrentRankAsyncCore(bool block, CancellationToken cancellation)
        {
            if (ElapsedMs > RefreshAfterMinimumMs)
            {
                Task pendingRefreshTask = EnsureRefreshOperation();

                if (block)
                {
                    _logger.DebugLog("Blocking on Valorant rank information fetch");
                    await pendingRefreshTask.WaitAsync(cancellation);
                }
            }

            MmrResponseCurrentData? currentData = _lastResponseData;

            if (!block && ElapsedMs > DropStaleDataAfterMs)
            {
                currentData = null;
            }

            return (currentData?.Tier, currentData?.RankingInTier ?? 0);

            Task EnsureRefreshOperation()
            {
                Task? currentFetch = _currentFetchOperation;
                if (currentFetch is null)
                {
                    var tcs = new TaskCompletionSource();
                    currentFetch = Interlocked.CompareExchange(ref _currentFetchOperation, tcs.Task, null);
                    if (currentFetch is null)
                    {
                        currentFetch = tcs.Task;

                        if (ElapsedMs < RefreshAfterMinimumMs)
                        {
                            tcs.SetResult();
                        }
                        else
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    _logger.DebugLog("Fetching new Valorant rank information");

                                    var response = await _httpClient.GetFromJsonAsync<MmrResponseModel>(
                                        "https://api.henrikdev.xyz/valorant/v2/mmr/na/ironmanchallenge/iron",
                                        CancellationToken.None);

                                    _lastResponseData = response?.Data?.CurrentData;
                                    _logger.DebugLog($"Got new Valorant rank information: {_lastResponseData?.Tier}, {_lastResponseData?.RankingInTier}");
                                }
                                catch (Exception ex)
                                {
                                    await _logger.DebugAsync($"Failed to fetch new Valorant rank information: {ex}");
                                }
                                finally
                                {
                                    _lastRefreshTicks = Environment.TickCount64;
                                    tcs.SetResult();
                                }
                            }, CancellationToken.None);
                        }
                    }
                }
                return currentFetch;
            }
        }

        private sealed class MmrResponseModel
        {
            [JsonPropertyName("data")]
            public MmrResponseData? Data { get; set; }
        }

        private sealed class MmrResponseData
        {
            [JsonPropertyName("current_data")]
            public MmrResponseCurrentData? CurrentData { get; set; }
        }

        private sealed class MmrResponseCurrentData
        {
            [JsonPropertyName("currenttierpatched")]
            public string? Tier { get; set; }

            [JsonPropertyName("ranking_in_tier")]
            public int RankingInTier { get; set; }
        }
    }
}
