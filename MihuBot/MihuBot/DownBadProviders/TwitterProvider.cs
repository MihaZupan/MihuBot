using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Tweetinvi;

namespace MihuBot.DownBadProviders
{
    public sealed class TwitterProvider : PollingDownBadProviderBase
    {
        private readonly ITwitterClient _twitter;

        public TwitterProvider(Logger logger, DiscordSocketClient discord, IComputerVisionClient computerVision, ITwitterClient twitter)
            : base(logger, discord, computerVision)
        {
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
            var tweets = (await _twitter.Timelines.GetUserTimelineAsync(data))
                .Where(t => t.CreatedAt > lastPostTime)
                .Where(t => !t.IsRetweet)
                .ToArray();

            if (tweets.Length == 0)
            {
                return (null, lastPostTime);
            }

            lastPostTime = tweets.Max(t => t.CreatedAt).UtcDateTime;

            var photoTweets = tweets
                .Select(t => (Tweet: t, Photos: t.Media.Where(m => m.MediaType == "photo").ToArray()))
                .Where(t => t.Photos.Length != 0)
                .ToArray();

            if (photoTweets.Length == 0)
            {
                return (null, lastPostTime);
            }

            var author = photoTweets.First().Tweet.CreatedBy;

            var embeds = new List<Embed>();

            foreach (var (tweet, photos) in photoTweets)
            {
                string tweetText = tweet.Text;
                if (string.IsNullOrWhiteSpace(tweetText))
                {
                    tweetText = "Tweet";
                }
                else if (TextLikelyContainsAds(tweetText) || TextLikelyContainsAds(tweet.FullText))
                {
                    tweetText = $"Ad ||{tweetText.Replace('|', 'I')}||";
                }

                foreach (var photo in photos)
                {
                    if (await ImageContainsPeopleAsync(photo.MediaURL, tweetText, tweet.Url))
                    {
                        embeds.Add(new EmbedBuilder()
                            .WithAuthor(author.Name, author.ProfileImageUrl, $"https://twitter.com/{author.Name}")
                            .WithTitle(tweetText)
                            .WithUrl(tweet.Url)
                            .WithImageUrl(photo.MediaURLHttps)
                            .Build());
                    }
                }
            }

            return (embeds.ToArray(), lastPostTime);
        }
    }
}
