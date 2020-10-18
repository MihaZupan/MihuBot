using System.Threading.Tasks;

namespace MihuBot.Husbando
{
    public interface IHusbandoService
    {
        ValueTask<ulong?> TryGetRandomMatchAsync(bool husbando, ulong user);

        ValueTask<bool> AddMatchAsync(bool husbando, ulong user, ulong target);

        ValueTask<bool> RemoveMatchAsync(bool husbando, ulong user, ulong target);

        ValueTask<ulong[]> GetAllMatchesAsync(bool husbando, ulong user);

        ValueTask<ulong[]> GetAllUsersAsync();
    }
}
