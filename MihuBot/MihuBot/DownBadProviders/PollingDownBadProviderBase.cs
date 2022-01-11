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

        protected PollingDownBadProviderBase(Logger logger, DiscordSocketClient discord, IComputerVisionClient computerVision, TimeSpan timerInterval)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _discord = discord ?? throw new ArgumentNullException(nameof(discord));
            _computerVision = computerVision ?? throw new ArgumentNullException(nameof(computerVision));

            _timerInterval = timerInterval;
            _watchTimer = new Timer(s => Task.Run(() => ((PollingDownBadProviderBase)s).OnTimerAsync()), this, timerInterval, Timeout.InfiniteTimeSpan);
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
            string providerName = GetType().Name;
            try
            {
                _logger.DebugLog($"{nameof(OnTimerAsync)} for {providerName}");

                KeyValuePair<string, (DateTime LastPost, List<Func<Task<SocketTextChannel>>> ChannelSelectors)>[] subscriptionsCopy;
                lock (_subscriptions)
                {
                    subscriptionsCopy = _subscriptions.ToArray();
                }


                foreach (var (data, subscriptions) in subscriptionsCopy)
                {
                    Embed[] embeds;
                    DateTime lastPostTime;
                    try
                    {
                        (embeds, lastPostTime) = await QueryNewPostsAsync(data, subscriptions.LastPost);
                    }
                    catch (Exception ex)
                    {
                        _logger.DebugLog($"Querying for {providerName} posts for {data} failed: {ex}");
                        continue;
                    }

                    Func<Task<SocketTextChannel>>[] channelSelectors;

                    lock (_subscriptions)
                    {
                        if (_subscriptions.TryGetValue(data, out var subsciption))
                        {
                            channelSelectors = subsciption.ChannelSelectors.ToArray();
                            _subscriptions[data] = (lastPostTime, subsciption.ChannelSelectors);
                        }
                        else
                        {
                            _logger.DebugLog($"Found no subscription for {providerName}, {data}.");
                            continue;
                        }
                    }

                    if (embeds is null)
                    {
                        _logger.DebugLog($"Found no new {providerName} posts for {data}");
                        continue;
                    }

                    _logger.DebugLog($"Sending {embeds.Length} embeds for {providerName} to {channelSelectors.Length} channels");

                    foreach (var channelSelector in channelSelectors)
                    {
                        try
                        {
                            var channel = await channelSelector();
                            if (channel is not null)
                            {
                                foreach (Embed embed in embeds)
                                {
                                    _logger.DebugLog($"Sending embed for {providerName} to {channel.Id}");
                                    try
                                    {
                                        await channel.SendMessageAsync(embed: embed);
                                    }
                                    catch (Exception ex)
                                    {
                                        await _logger.DebugAsync($"Failed to send the embed for {providerName} to {channel.Id}: {ex}");
                                    }
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
                _logger.DebugLog($"Restarting the timer for {providerName}");
                _watchTimer.Change(_timerInterval, Timeout.InfiniteTimeSpan);
            }
        }

        protected async Task<bool> ImageContainsPeopleAsync(string photoUrl, string postText, string postUrl)
        {
            try
            {
                const int MaxRetries = 3;
                int retry = 0;
                ImageAnalysis analysis;
                while (true)
                {
                    try
                    {
                        _logger.DebugLog($"Starting analysis for {photoUrl} ({postUrl})");
                        analysis = await _computerVision.AnalyzeImageAsync(photoUrl, _visualFeatureTypes);
                        break;
                    }
                    catch (Exception ex) when (retry++ < MaxRetries)
                    {
                        int retrySec = 5 * (int)Math.Pow(2, retry);
                        _logger.DebugLog($"[Retrying after {retrySec} s] An expection was thrown while processing {photoUrl} for {postUrl}: {ex}");
                        await Task.Delay(TimeSpan.FromSeconds(retrySec));
                    }
                }

                IList<FaceDescription> faces = analysis.Faces ?? Array.Empty<FaceDescription>();
                IList<Category> categories = analysis.Categories;

                try
                {
                    if (_discord.GetTextChannel(Channels.TheBoysSpam) is SocketTextChannel spamChannel)
                    {
                        await spamChannel.TrySendMessageAsync(
                            embed: new EmbedBuilder()
                                .WithTitle(postText)
                                .WithUrl(postUrl)
                                .WithImageUrl(photoUrl)
                                .WithFields(categories
                                    .OrderByDescending(category => category.Score)
                                    .Take(5)
                                    .Select(category => new EmbedFieldBuilder()
                                        .WithName(category.Name)
                                        .WithValue($"Score: {category.Score:N4}")
                                        .WithIsInline(true))
                                    .Concat(faces
                                    .Take(5)
                                    .Select(face => new EmbedFieldBuilder()
                                        .WithName("Face")
                                        .WithValue($"Age={face.Age}, Gender={face.Gender}")
                                        .WithIsInline(true))))
                                .Build(),
                            logger: _logger);
                    }
                }
                catch { }

                if (faces.Count == 0 &&
                    !categories.Any(c => c.Name.Contains("people", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.DebugLog($"Skipping {photoUrl} as no people categories or faces were detected");
                    return false;
                }

                if (faces.Count != 0 && faces.All(f => f.Age < 14))
                {
                    _logger.DebugLog($"Skipping {photoUrl} as only young people were detected");
                    return false;
                }

                if (faces.Count == 0 && categories.All(c => c.Name == "people_baby" || c.Name == "people_young"))
                {
                    _logger.DebugLog($"Skipping {photoUrl} as only young people were detected");
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

        public async Task RemoveAsync(Uri url, Func<Task<SocketTextChannel>> channelSelector)
        {
            var (data, error) = await TryExtractUrlDataAsync(url);

            if (error is not null)
            {
                return;
            }

            lock (_subscriptions)
            {
                if (_subscriptions.TryGetValue(data, out var subscriptions))
                {
                    subscriptions.ChannelSelectors.Remove(channelSelector);
                    if (subscriptions.ChannelSelectors.Count == 0)
                    {
                        _subscriptions.Remove(data);
                    }
                }
            }
        }
    }
}
