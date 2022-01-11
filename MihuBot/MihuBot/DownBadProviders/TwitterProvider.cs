using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Tweetinvi;

namespace MihuBot.DownBadProviders
{
    public sealed class TwitterProvider : PollingDownBadProviderBase
    {
        private readonly Logger _logger;
        private readonly ITwitterClient _twitter;

        public TwitterProvider(Logger logger, DiscordSocketClient discord, IComputerVisionClient computerVision, ITwitterClient twitter)
            : base(logger, discord, computerVision, TimeSpan.FromMinutes(10))
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _twitter = twitter ?? throw new ArgumentNullException(nameof(twitter));
        }

        public override bool CanMatch(Uri url, out Uri normalizedUrl)
        {
            string host = url.IdnHost;

            if ((host.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) || host.Equals("www.twitter.com", StringComparison.OrdinalIgnoreCase))
                && url.AbsolutePath.Length > 0)
            {
                string name = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).First();
                normalizedUrl = new Uri($"https://twitter.com/{name}", UriKind.Absolute);
                return true;
            }
            else
            {
                normalizedUrl = null;
                return false;
            }
        }

        public override async Task<(string Data, string Error)> TryExtractUrlDataAsync(Uri url)
        {
            try
            {
                string name = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).First();
                var user = await _twitter.UsersV2.GetUserByNameAsync(name);
                return (user.User.Username, null);
            }
            catch
            {
                return (null, "Couldn't find a matching Twitter account");
            }
        }

        public override async Task<(Embed[] Embeds, DateTime LastPostTime)> QueryNewPostsAsync(string data, DateTime lastPostTime)
        {
            _logger.DebugLog($"{nameof(QueryNewPostsAsync)} for {nameof(TwitterProvider)}, {data} with {nameof(lastPostTime)}={lastPostTime}");

            var tweets = (await _twitter.Timelines.GetUserTimelineAsync(data))
                .Where(t => t.CreatedAt > lastPostTime)
                .Where(t => !t.IsRetweet)
                .ToArray();

            if (tweets.Length == 0)
            {
                _logger.DebugLog($"Found no new Tweets for {data}");
                return (null, lastPostTime);
            }

            lastPostTime = tweets.Max(t => t.CreatedAt).UtcDateTime;
            _logger.DebugLog($"New {nameof(lastPostTime)} for {data} is {lastPostTime}");

            var photoTweets = tweets
                .Select(t => (Tweet: t, Photos: t.Media.Where(m => m.MediaType == "photo").ToArray()))
                .Where(t => t.Photos.Length != 0)
                .ToArray();

            if (photoTweets.Length == 0)
            {
                _logger.DebugLog($"Found no new photo Tweets for {data}");
                return (null, lastPostTime);
            }

            var author = photoTweets.First().Tweet.CreatedBy;

            var embeds = new List<Embed>();

            foreach (var (tweet, photos) in photoTweets)
            {
                string tweetText = tweet.Text;
                if (string.IsNullOrWhiteSpace(tweetText) || tweetText.Length > 128 || TextLikelyContainsAds(tweetText) || TextLikelyContainsAds(tweet.FullText))
                {
                    tweetText = "Tweet";
                }

                foreach (var photo in photos)
                {
                    _logger.DebugLog($"Testing {photo.URL} for {tweet.Url}");
                    try
                    {
                        if (await ImageContainsPeopleAsync(photo.MediaURL, tweetText, tweet.Url))
                        {
                            _logger.DebugLog($"Adding {photo.URL} for {tweet.Url}");
                            embeds.Add(new EmbedBuilder()
                                .WithAuthor(author.ScreenName, author.ProfileImageUrl, $"https://twitter.com/{author.ScreenName}")
                                .WithTitle(tweetText)
                                .WithUrl(tweet.Url)
                                .WithImageUrl(photo.MediaURLHttps)
                                .Build());
                        }
                        else
                        {
                            _logger.DebugLog($"Skipping {photo.URL} for {tweet.Url}");
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync($"Exception while evaulating {photo} for {tweet.Url}: {ex}");
                    }
                }
            }

            return (embeds.ToArray(), lastPostTime);
        }
    }
}
