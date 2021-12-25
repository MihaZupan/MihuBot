using Tweetinvi;

namespace MihuBot.DownBadProviders
{
    public sealed class TwitterProvider : IDownBadProvider
    {
        private readonly ITwitterClient _client;
        private readonly Dictionary<string, (DateTime LastTweet, List<Func<Task<SocketTextChannel>>> ChannelSelectors)> _subscriptions = new();
        private readonly Timer _watchTimer;

        public TwitterProvider(ITwitterClient client)
        {
            _client = client;
            _watchTimer = new Timer(s => Task.Run(() => ((TwitterProvider)s).OnTimerAsync()), this, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        }

        public bool CanMatch(Uri url)
        {
            return (url.IdnHost.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) ||
                    url.IdnHost.Equals("www.twitter.com", StringComparison.OrdinalIgnoreCase))
                && url.PathAndQuery.Length > 0;
        }

        public async Task<string> TryWatchAsync(Uri url, Func<Task<SocketTextChannel>> channelSelector)
        {
            string username;
            try
            {
                string name = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).First();
                var user = await _client.UsersV2.GetUserByNameAsync(name);
                username = user.User.Username;
            }
            catch
            {
                return "Couldn't find a matching Twitter account";
            }

            lock (_subscriptions)
            {
                if (_subscriptions.TryGetValue(username, out var subscription))
                {
                    subscription.ChannelSelectors.Add(channelSelector);
                }
                else
                {
                    var channelSelectors = new List<Func<Task<SocketTextChannel>>>() { channelSelector };
                    _subscriptions.Add(username, (DateTime.UtcNow.AddSeconds(5), channelSelectors));
                }
            }

            return null;
        }

        private async Task OnTimerAsync()
        {
            try
            {
                foreach (var (username, subscriptions) in _subscriptions.ToArray())
                {
                    string result;
                    DateTime lastTweetTime;
                    try
                    {
                        (result, lastTweetTime) = await QueryAsync(username, subscriptions.LastTweet);

                        if (result is null)
                        {
                            continue;
                        }
                    }
                    catch { continue; }

                    List<Func<Task<SocketTextChannel>>> channelSelectors;

                    lock (_subscriptions)
                    {
                        if (_subscriptions.TryGetValue(username, out var subsciption))
                        {
                            channelSelectors = subsciption.ChannelSelectors;
                            _subscriptions[username] = (lastTweetTime, subsciption.ChannelSelectors);
                        }
                        else
                        {
                            continue;
                        }
                    }

                    foreach (var channelSelector in channelSelectors)
                    {
                        try
                        {
                            var channel = await channelSelector();
                            if (channel is not null)
                            {
                                await channel.SendMessageAsync(result);
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

        private async Task<(string Result, DateTime LastTweetTime)> QueryAsync(string username, DateTime lastTweetTime)
        {
            var tweets = (await _client.Timelines.GetUserTimelineAsync(username))
                .Where(t => t.CreatedAt > lastTweetTime)
                .ToArray();

            if (tweets.Length == 0)
            {
                return (null, lastTweetTime);
            }

            lastTweetTime = tweets.Max(t => t.CreatedAt).UtcDateTime;

            var mediaTweets = tweets
                .Where(t => t.Media.Any(m => m.MediaType == "photo"))
                .SelectMany(t => t.Media)
                .Where(m => m.MediaType == "photo")
                .ToArray();

            if (mediaTweets.Length == 0)
            {
                return (null, lastTweetTime);
            }

            return (string.Join('\n', mediaTweets.Select(m => m.MediaURLHttps)), lastTweetTime);
        }
    }
}
