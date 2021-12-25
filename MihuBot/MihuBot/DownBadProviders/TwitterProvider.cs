using Tweetinvi;

namespace MihuBot.DownBadProviders
{
    public sealed class TwitterProvider : PollingDownBadProviderBase
    {
        private readonly ITwitterClient _client;

        public TwitterProvider(ITwitterClient client)
        {
            _client = client;
        }

        public override bool CanMatch(Uri url)
        {
            return (url.IdnHost.Equals("twitter.com", StringComparison.OrdinalIgnoreCase) ||
                    url.IdnHost.Equals("www.twitter.com", StringComparison.OrdinalIgnoreCase))
                && url.PathAndQuery.Length > 0;
        }

        public override async Task<(string Data, string Error)> TryExtractUrlDataAsync(Uri url)
        {
            try
            {
                string name = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).First();
                var user = await _client.UsersV2.GetUserByNameAsync(name);
                return (user.User.Username, null);
            }
            catch
            {
                return (null, "Couldn't find a matching Twitter account");
            }
        }

        public override async Task<(Embed[] Embeds, DateTime LastPostTime)> QueryNewPostsAsync(string data, DateTime lastPostTime)
        {
            var tweets = (await _client.Timelines.GetUserTimelineAsync(data))
                .Where(t => t.CreatedAt > lastPostTime)
                .Where(t => !t.IsRetweet)
                .ToArray();

            if (tweets.Length == 0)
            {
                return (null, lastPostTime);
            }

            lastPostTime = tweets.Max(t => t.CreatedAt).UtcDateTime;

            var mediaTweets = tweets
                .Select(t => (Tweet: t, Media: t.Media.Where(m => m.MediaType == "photo").ToArray()))
                .Where(t => t.Media.Length != 0)
                .ToArray();

            if (mediaTweets.Length == 0)
            {
                return (null, lastPostTime);
            }

            var author = tweets.First().CreatedBy;

            var embeds = mediaTweets
                .SelectMany(tweet => tweet.Media
                    .Select(media => new EmbedBuilder()
                        .WithAuthor(author.Name, author.ProfileImageUrl, author.Url)
                        .WithUrl(tweet.Tweet.Url)
                        .WithDescription(tweet.Tweet.Text)
                        .WithImageUrl(media.MediaURLHttps)
                        .Build()))
                .ToArray();

            return (embeds, lastPostTime);
        }
    }
}
