using InstagramApiSharp;
using InstagramApiSharp.API;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using System.Globalization;

namespace MihuBot.DownBadProviders
{
    public sealed class InstagramProvider : PollingDownBadProviderBase
    {
        private readonly Logger _logger;
        private readonly IInstaApi _instagram;

        public InstagramProvider(Logger logger, DiscordSocketClient discord, IComputerVisionClient computerVision, IInstaApi instagram)
            : base(logger, discord, computerVision)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _instagram = instagram ?? throw new ArgumentNullException(nameof(instagram));
        }

        public override bool CanMatch(Uri url, out Uri normalizedUrl)
        {
            string host = url.IdnHost;

            if ((host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) || host.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase))
                && url.AbsolutePath.Length > 0)
            {
                string name = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).First();
                normalizedUrl = new Uri($"https://instagram.com/{name}", UriKind.Absolute);
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
                string name = url.AbsolutePath.Trim('/');

                var userResult = await _instagram.UserProcessor.GetUserInfoByUsernameAsync(name);

                if (!userResult.Succeeded)
                {
                    return (null, "Couldn't find a matching Instagram account");
                }

                var user = userResult.Value;

                if (user.IsPrivate)
                {
                    return (null, "Can't look at posts of private accounts");
                }

                return (user.Pk.ToString(CultureInfo.InvariantCulture), null);
            }
            catch
            {
                return (null, "Something went wrong when looking for the account");
            }
        }

        public override async Task<(Embed[] Embeds, DateTime LastPostTime)> QueryNewPostsAsync(string data, DateTime lastPostTime)
        {
            _logger.DebugLog($"{nameof(QueryNewPostsAsync)} for {nameof(InstagramProvider)}, {data} with {nameof(lastPostTime)}={lastPostTime}");

            var mediaResult = await _instagram.UserProcessor.GetUserMediaByIdAsync(long.Parse(data), PaginationParameters.MaxPagesToLoad(3));

            if (!mediaResult.Succeeded)
            {
                _logger.DebugLog($"Failed to fetch new posts for {data}: {mediaResult.Info.Message}");
                return (null, lastPostTime);
            }

            var newPosts = mediaResult.Value
                .Where(media => media.TakenAt > lastPostTime)
                .ToArray();

            if (newPosts.Length == 0)
            {
                _logger.DebugLog($"Found no new posts for {data}");
                return (null, lastPostTime);
            }

            lastPostTime = newPosts.Max(p => p.TakenAt);
            _logger.DebugLog($"New {nameof(lastPostTime)} for {data} is {lastPostTime}");

            var embeds = new List<Embed>();

            foreach (var post in newPosts)
            {
                string postText = post.Caption.Text;
                if (string.IsNullOrWhiteSpace(postText) || postText.Length > 128 || TextLikelyContainsAds(postText))
                {
                    postText = "Post";
                }

                string postUrl = $"https://www.instagram.com/p/{post.Code}";
                string authorUrl = $"https://www.instagram.com/{post.User.UserName}";

                _logger.DebugLog($"Evaluating {postUrl}");

                if (post.Carousel is not null)
                {
                    foreach (var item in post.Carousel)
                    {
                        var photo = item.Images.MaxBy(i => i.Width * i.Height);

                        if (await ImageContainsPeopleAsync(photo.Uri, postText, postUrl))
                        {
                            _logger.DebugLog($"Adding {photo.Uri} for {postUrl}");
                            embeds.Add(new EmbedBuilder()
                                .WithAuthor(post.User.UserName, post.User.ProfilePicUrl, authorUrl)
                                .WithTitle(postText)
                                .WithUrl(postUrl)
                                .WithImageUrl(photo.Uri)
                                .Build());
                        }
                    }
                }
                else if (post.Images is not null)
                {
                    var photo = post.Images.MaxBy(i => i.Width * i.Height);

                    if (await ImageContainsPeopleAsync(photo.Uri, postText, postUrl))
                    {
                        _logger.DebugLog($"Adding {photo.Uri} for {postUrl}");
                        embeds.Add(new EmbedBuilder()
                            .WithAuthor(post.User.UserName, post.User.ProfilePicUrl, authorUrl)
                            .WithTitle(postText)
                            .WithUrl(postUrl)
                            .WithImageUrl(photo.Uri)
                            .Build());
                    }
                }
            }

            return (embeds.ToArray(), lastPostTime);
        }
    }
}
