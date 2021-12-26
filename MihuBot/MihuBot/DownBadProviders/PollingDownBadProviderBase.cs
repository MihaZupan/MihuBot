using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;

namespace MihuBot.DownBadProviders
{
    public abstract class PollingDownBadProviderBase : IDownBadProvider
    {
        private static readonly VisualFeatureTypes?[] _visualFeatureTypes = new VisualFeatureTypes?[]
        {
            VisualFeatureTypes.Categories,
            VisualFeatureTypes.Faces
        };

        private readonly Logger _logger;
        private readonly DiscordSocketClient _discord;
        private readonly IComputerVisionClient _computerVision;
        private readonly Dictionary<string, (DateTime LastPost, List<Func<Task<SocketTextChannel>>> ChannelSelectors)> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _watchTimer;
        private readonly TimeSpan _timerInterval;

        protected PollingDownBadProviderBase(Logger logger, DiscordSocketClient discord, IComputerVisionClient computerVision)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _discord = discord ?? throw new ArgumentNullException(nameof(discord));
            _computerVision = computerVision ?? throw new ArgumentNullException(nameof(computerVision));

            _timerInterval = TimeSpan.FromMinutes(Rng.Next(9, 13));
            _watchTimer = new Timer(s => Task.Run(() => ((PollingDownBadProviderBase)s).OnTimerAsync()), this, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
        }

        public abstract bool CanMatch(Uri url, out Uri normalizedUrl);

        public abstract Task<(string Data, string Error)> TryExtractUrlDataAsync(Uri url);

        public abstract Task<(Embed[] Embeds, DateTime LastPostTime)> QueryNewPostsAsync(string data, DateTime lastPostTime);

        public async Task<string> TryWatchAsync(Uri url, Func<Task<SocketTextChannel>> channelSelector)
        {
            var (data, error) = await TryExtractUrlDataAsync(url);

            if (error is not null)
            {
                return error;
            }

            lock (_subscriptions)
            {
                if (_subscriptions.TryGetValue(data, out var subscription))
                {
                    subscription.ChannelSelectors.Add(channelSelector);
                }
                else
                {
                    var channelSelectors = new List<Func<Task<SocketTextChannel>>>() { channelSelector };
                    _subscriptions.Add(data, (DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(3)), channelSelectors));
                }
            }

            return null;
        }

        private async Task OnTimerAsync()
        {
            try
            {
                foreach (var (data, subscriptions) in _subscriptions.ToArray())
                {
                    Embed[] embeds;
                    DateTime lastPostTime;
                    try
                    {
                        (embeds, lastPostTime) = await QueryNewPostsAsync(data, subscriptions.LastPost);
                    }
                    catch { continue; }

                    List<Func<Task<SocketTextChannel>>> channelSelectors;

                    lock (_subscriptions)
                    {
                        if (_subscriptions.TryGetValue(data, out var subsciption))
                        {
                            channelSelectors = subsciption.ChannelSelectors;
                            _subscriptions[data] = (lastPostTime, subsciption.ChannelSelectors);
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (embeds is null)
                    {
                        continue;
                    }

                    foreach (var channelSelector in channelSelectors)
                    {
                        try
                        {
                            var channel = await channelSelector();
                            if (channel is not null)
                            {
                                foreach (Embed embed in embeds)
                                {
                                    await channel.SendMessageAsync(embed: embed);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                _watchTimer.Change(_timerInterval, Timeout.InfiniteTimeSpan);
            }
        }

        protected async Task<bool> ImageContainsPeopleAsync(string photoUrl, string postText, string postUrl)
        {
            try
            {
                ImageAnalysis analysis = await _computerVision.AnalyzeImageAsync(photoUrl, _visualFeatureTypes);

                if (_discord.GetTextChannel(Channels.TheBoysSpam) is SocketTextChannel spamChannel)
                {
                    await spamChannel.TrySendMessageAsync(
                        embed: new EmbedBuilder()
                            .WithTitle(postText)
                            .WithUrl(postUrl)
                            .WithImageUrl(photoUrl)
                            .WithFields(analysis.Categories
                                .OrderByDescending(category => category.Score)
                                .Take(10)
                                .Select(category => new EmbedFieldBuilder()
                                    .WithName(category.Name)
                                    .WithValue($"Score: {category.Score:N4}")
                                    .WithIsInline(true))
                                .Concat((analysis.Faces ?? Array.Empty<FaceDescription>())
                                .Select(face => new EmbedFieldBuilder()
                                    .WithName("Face")
                                    .WithValue($"Age={face.Age}, Gender={face.Gender}")
                                    .WithIsInline(true))))
                            .Build(),
                        logger: _logger);
                }

                const double PeopleScoreThreshold = 0.15;

                if (!analysis.Categories.Any(t => t.Name.Contains("people", StringComparison.OrdinalIgnoreCase) && t.Score > PeopleScoreThreshold) &&
                    (analysis.Faces is null || analysis.Faces.Count == 0))
                {
                    _logger.DebugLog($"Skipping {photoUrl} as no people categories above {PeopleScoreThreshold} were detected");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync($"An expection was thrown while processing {photoUrl} for {postUrl}: {ex}");
                return true;
            }
        }

        protected bool TextLikelyContainsAds(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.Contains("#ad", StringComparison.OrdinalIgnoreCase)
                || text.Contains("sale", StringComparison.OrdinalIgnoreCase)
                || text.Contains("promo", StringComparison.OrdinalIgnoreCase)
                || text.Contains("giveaway", StringComparison.OrdinalIgnoreCase)
                || text.Contains("% off", StringComparison.OrdinalIgnoreCase)
                || text.Contains("onlyfans.", StringComparison.OrdinalIgnoreCase)
                || text.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)
                || text.Contains("youtu", StringComparison.OrdinalIgnoreCase)
                || text.Contains("instagram.", StringComparison.OrdinalIgnoreCase)
                || text.Contains("patreon", StringComparison.OrdinalIgnoreCase)
                || text.Contains("shop", StringComparison.OrdinalIgnoreCase)
                ;
        }
    }
}
