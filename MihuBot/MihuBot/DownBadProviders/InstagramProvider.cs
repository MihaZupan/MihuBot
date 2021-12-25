namespace MihuBot.DownBadProviders
{
    public class InstagramProvider : IDownBadProvider
    {
        public bool CanMatch(Uri url)
        {
            return false;
        }

        public Task<string> TryWatchAsync(Uri url, Func<Task<SocketTextChannel>> channelSelector)
        {
            throw new NotImplementedException();
        }
    }
}
