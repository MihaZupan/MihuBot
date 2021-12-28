namespace MihuBot.DownBadProviders
{
    public interface IDownBadProvider
    {
        bool CanMatch(Uri url, out Uri normalizedUrl);

        Task<string> TryWatchAsync(Uri url, Func<Task<SocketTextChannel>> channelSelector);

        Task RemoveAsync(Uri url, Func<Task<SocketTextChannel>> channelSelector);
    }
}
