using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Tweetinvi;

namespace MihuBot.DownBadProviders
{
    public sealed class TwitterProvider : PollingDownBadProviderBase
    {
        private static readonly VisualFeatureTypes?[] _visualFeatureTypes = new VisualFeatureTypes?[]
        {
            VisualFeatureTypes.Categories
        };

        private readonly Logger _logger;
        private readonly DiscordSocketClient _discord;
        private readonly ITwitterClient _twitter;
        private readonly IComputerVisionClient _computerVision;

        public TwitterProvider(Logger logger, DiscordSocketClient discord, ITwitterClient twitter, IComputerVisionClient computerVision)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _discord = discord ?? throw new ArgumentNullException(nameof(discord));
            _twitter = twitter ?? throw new ArgumentNullException(nameof(twitter));
            _computerVision = computerVision ?? throw new ArgumentNullException(nameof(computerVision));
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

            SocketTextChannel spamChannel = _discord.GetTextChannel(Channels.TheBoysSpam);

            foreach (var (tweet, photos) in photoTweets)
            {
                string tweetText = tweet.Text;
                if (string.IsNullOrWhiteSpace(tweetText))
                {
                    tweetText = "Tweet";
                }
                else if (DetectAnAd(tweetText) || DetectAnAd(tweet.FullText))
                {
                    tweetText = $"Ad ||{tweetText.Replace('|', 'I')}||";
                }

                foreach (var photo in photos)
                {
                    try
                    {
                        ImageAnalysis analysis = await _computerVision.AnalyzeImageAsync(photo.MediaURL, _visualFeatureTypes);

                        await spamChannel.TrySendMessageAsync(
                            embed: new EmbedBuilder()
                                .WithTitle(tweetText)
                                .WithUrl(tweet.Url)
                                .WithImageUrl(photo.MediaURLHttps)
                                .WithFields(analysis.Categories
                                    .OrderByDescending(category => category.Score)
                                    .Take(10)
                                    .Select(category => new EmbedFieldBuilder()
                                        .WithName(category.Name)
                                        .WithValue($"Score: {category.Score}")
                                        .WithIsInline(true)))
                                .Build(),
                            logger: _logger);

                        const double PeopleScoreThreshold = 0.75;

                        if (!analysis.Categories.Any(t => t.Name.Contains("people", StringComparison.OrdinalIgnoreCase) && t.Score > PeopleScoreThreshold))
                        {
                            _logger.DebugLog($"Skipping {photo.URL} as no people categories above {PeopleScoreThreshold} were detected");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        await _logger.DebugAsync($"An expection was thrown while processing {photo.MediaURLHttps} for {tweet.Url}: {ex}");
                    }

                    embeds.Add(new EmbedBuilder()
                        .WithAuthor(author.Name, author.ProfileImageUrl, $"https://twitter.com/{author.Name}")
                        .WithTitle(tweetText)
                        .WithUrl(tweet.Url)
                        .WithImageUrl(photo.MediaURLHttps)
                        .Build());
                }
            }

            return (embeds.ToArray(), lastPostTime);
        }

        private static bool DetectAnAd(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return text.Contains("#ad",         StringComparison.OrdinalIgnoreCase)
                || text.Contains("sale",        StringComparison.OrdinalIgnoreCase)
                || text.Contains("promo",       StringComparison.OrdinalIgnoreCase)
                || text.Contains("giveaway",    StringComparison.OrdinalIgnoreCase)
                || text.Contains("% off",       StringComparison.OrdinalIgnoreCase)
                || text.Contains("onlyfans.",   StringComparison.OrdinalIgnoreCase)
                || text.Contains("twitch.tv",   StringComparison.OrdinalIgnoreCase)
                || text.Contains("youtu",       StringComparison.OrdinalIgnoreCase)
                || text.Contains("instagram.",  StringComparison.OrdinalIgnoreCase)
                || text.Contains("patreon",     StringComparison.OrdinalIgnoreCase)
                ;
        }
    }
}
