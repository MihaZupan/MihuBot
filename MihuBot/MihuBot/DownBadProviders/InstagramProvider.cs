namespace MihuBot.DownBadProviders
{
    public class InstagramProvider : PollingDownBadProviderBase
    {
        public override bool CanMatch(Uri url, out Uri normalizedUrl)
        {
            normalizedUrl = null;

            return (url.IdnHost.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) ||
                    url.IdnHost.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase))
                && url.PathAndQuery.Length > 0;
        }

        public override async Task<(string Data, string Error)> TryExtractUrlDataAsync(Uri url)
        {
            await Task.Yield();
            return (null, "Can't do that yet");
        }

        public override Task<(Embed[] Embeds, DateTime LastPostTime)> QueryNewPostsAsync(string data, DateTime lastPostTime)
        {
            throw new NotImplementedException();
        }
    }
}
