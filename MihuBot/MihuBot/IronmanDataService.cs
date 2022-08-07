﻿using System.Net.Http.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace MihuBot
{
    public sealed class IronmanDataService
    {
        private const int RefreshAfterMinimumMs = 10 * 1000;

        private readonly HttpClient _httpClient;
        private readonly DataSource<ValorantStatus> _valorantDataSource;
        private readonly DataSource<TFTStatus> _tftDataSource;
        private readonly DataSource<ApexStatus> _apexDataSource;

        public IronmanDataService(HttpClient httpClient, Logger logger, IConfiguration configuration)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(configuration);

            _valorantDataSource = new DataSource<ValorantStatus>(logger, async () =>
            {
                var response = await _httpClient.GetFromJsonAsync<ValorantMmrResponseModel>(
                    "https://api.henrikdev.xyz/valorant/v2/mmr/na/ironmanchallenge/iron",
                    CancellationToken.None);

                var current = response?.Data?.CurrentData;
                if (current?.Tier is null)
                {
                    return null;
                }

                return new ValorantStatus(DateTime.UtcNow, current.Tier, current.RankInTier);
            });

            _tftDataSource = new DataSource<TFTStatus>(logger, async () =>
            {
                string apiKey = configuration["RiotGames:ApiKey"] ?? throw new Exception("Missing API Key");

                var response = await _httpClient.GetFromJsonAsync<TFTResponseModel[]>(
                    $"https://na1.api.riotgames.com/tft/league/v1/entries/by-summoner/_YQwcaIf4O3y-NR8j2jXthwJE-acdSCxvpgXYq39sWoNkTn15AQj0gXa-w?api_key={apiKey}",
                    CancellationToken.None);

                var current = response?.FirstOrDefault();
                if (current?.Tier is null)
                {
                    return null;
                }

                return new TFTStatus(DateTime.UtcNow, $"{current.Tier} {current.Rank}", current.LeaguePoints);
            });

            _apexDataSource = new DataSource<ApexStatus>(logger, async () =>
            {
                string apiKey = configuration["TrackerGG:ApiKey"] ?? throw new Exception("Missing API Key");

                var request = new HttpRequestMessage(HttpMethod.Get, "https://public-api.tracker.gg/v2/apex/standard/profile/origin/IronmanChallenge");
                request.Headers.TryAddWithoutValidation("TRN-Api-Key", apiKey);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using var responseMessage = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

                var response = await responseMessage.Content.ReadFromJsonAsync<ApexResponseModel>(cancellationToken: CancellationToken.None);

                var score = response?.Data?.Segments?.FirstOrDefault(s => s?.Type == "overview")?.Stats?.RankScore;
                if (score?.Metadata?.RankName is null)
                {
                    return null;
                }

                return new ApexStatus(DateTime.UtcNow, score.Metadata.RankName, (int)score.Value, score.Metadata.IconUrl);
            });
        }

        public ValorantStatus? TryGetValorantRank() =>
            _valorantDataSource.TryGetLatest();

        public ValueTask<ValorantStatus?> GetValorantRankAsync(CancellationToken cancellation) =>
            _valorantDataSource.GetLatestAsync(cancellation);

        public TFTStatus? TryGetTFTRank() =>
            _tftDataSource.TryGetLatest();

        public ValueTask<TFTStatus?> GetTFTRankAsync(CancellationToken cancellation) =>
            _tftDataSource.GetLatestAsync(cancellation);

        public ApexStatus? TryGetApexRank() =>
            _apexDataSource.TryGetLatest();

        public ValueTask<ApexStatus?> GetApexRankAsync(CancellationToken cancellation) =>
            _apexDataSource.GetLatestAsync(cancellation);

        public record ValorantStatus(DateTime RefreshedAt, string Tier, int RankInTier);

        public record TFTStatus(DateTime RefreshedAt, string Rank, int LP);

        public record ApexStatus(DateTime RefreshedAt, string Tier, int RP, string? IconUrl);

        private sealed class ValorantMmrResponseModel
        {
            [JsonPropertyName("data")]
            public ValorantMmrResponseData? Data { get; set; }
        }

        private sealed class ValorantMmrResponseData
        {
            [JsonPropertyName("current_data")]
            public ValorantMmrResponseCurrentData? CurrentData { get; set; }
        }

        private sealed class ValorantMmrResponseCurrentData
        {
            [JsonPropertyName("currenttierpatched")]
            public string? Tier { get; set; }

            [JsonPropertyName("ranking_in_tier")]
            public int RankInTier { get; set; }
        }

        private sealed class TFTResponseModel
        {
            [JsonPropertyName("tier")]
            public string? Tier { get; set; }

            [JsonPropertyName("rank")]
            public string? Rank { get; set; }

            [JsonPropertyName("leaguePoints")]
            public int LeaguePoints { get; set; }
        }

        private sealed class ApexResponseModel
        {
            [JsonPropertyName("data")]
            public ApexResponseData? Data { get; set; }
        }

        private sealed class ApexResponseData
        {
            [JsonPropertyName("segments")]
            public ApexResponseSegment?[]? Segments { get; set; }
        }

        private sealed class ApexResponseSegment
        {
            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("stats")]
            public ApexResponseStats? Stats { get; set; }
        }

        private sealed class ApexResponseStats
        {
            [JsonPropertyName("rankScore")]
            public ApexResponseRankScore? RankScore { get; set; }

            [JsonPropertyName("arenaRankScore")]
            public ApexResponseRankScore? ArenaRankScore { get; set; }
        }

        private sealed class ApexResponseRankScore
        {
            [JsonPropertyName("value")]
            public double Value { get; set; }

            [JsonPropertyName("metadata")]
            public ApexResponseRankScoreMetadata? Metadata { get; set; }
        }

        private sealed class ApexResponseRankScoreMetadata
        {
            [JsonPropertyName("iconUrl")]
            public string? IconUrl { get; set; }

            [JsonPropertyName("rankName")]
            public string? RankName { get; set; }
        }


        private sealed class DataSource<T>
        {
            private readonly Logger _logger;
            private readonly Func<Task<T?>> _valueFactory;
            private T? _lastResponseData;
            private Task? _currentFetchOperation;
            private long _lastRefreshTicks = -RefreshAfterMinimumMs;

            private long ElapsedMs => Environment.TickCount64 - _lastRefreshTicks;

            public DataSource(Logger logger, Func<Task<T?>> valueFactory)
            {
                _logger = logger ?? throw new ArgumentNullException(nameof(logger));
                _valueFactory = valueFactory ?? throw new ArgumentNullException(nameof(valueFactory));
            }

            public T? TryGetLatest()
            {
                var task = GetLatestAsyncCore(block: false, CancellationToken.None);
                Debug.Assert(task.IsCompletedSuccessfully);
                return task.Result;
            }

            public ValueTask<T?> GetLatestAsync(CancellationToken cancellation)
            {
                return GetLatestAsyncCore(block: true, cancellation);
            }

            private async ValueTask<T?> GetLatestAsyncCore(bool block, CancellationToken cancellation)
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

                return _lastResponseData;

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
                                _currentFetchOperation = null;
                            }
                            else
                            {
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        _logger.DebugLog($"Fetching new {typeof(T).Name} rank information");

                                        _lastResponseData = await _valueFactory();

                                        _logger.DebugLog($"Got new {typeof(T).Name} rank information: {_lastResponseData}");
                                    }
                                    catch (Exception ex)
                                    {
                                        await _logger.DebugAsync($"Failed to fetch new {typeof(T).Name} rank information: {ex}");
                                    }
                                    finally
                                    {
                                        _lastRefreshTicks = Environment.TickCount64;
                                        tcs.SetResult();
                                        _currentFetchOperation = null;
                                    }
                                }, CancellationToken.None);
                            }
                        }
                    }
                    return currentFetch;
                }
            }
        }
    }
}
