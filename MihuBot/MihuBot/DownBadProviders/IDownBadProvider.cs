namespace MihuBot.DownBadProviders
{
    public interface IDownBadProvider
    {
        bool CanMatch(Uri url);

        Task<string> TryWatchAsync(Uri url, Func<Task<SocketTextChannel>> channelSelector);
    }
}
