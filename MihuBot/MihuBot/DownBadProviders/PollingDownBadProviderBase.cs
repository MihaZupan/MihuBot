namespace MihuBot.DownBadProviders
{
    public abstract class PollingDownBadProviderBase : IDownBadProvider
    {
        private readonly Dictionary<string, (DateTime LastPost, List<Func<Task<SocketTextChannel>>> ChannelSelectors)> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _watchTimer;

        protected PollingDownBadProviderBase()
        {
            _watchTimer = new Timer(s => Task.Run(() => ((PollingDownBadProviderBase)s).OnTimerAsync()), this, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        }

        public abstract bool CanMatch(Uri url);

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
                    _subscriptions.Add(data, (DateTime.UtcNow.AddSeconds(5), channelSelectors));
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
                _watchTimer.Change(TimeSpan.FromMinutes(10), Timeout.InfiniteTimeSpan);
            }
        }
    }
}
