using System.Threading.Tasks;

namespace MihuBot.Permissions
{
    public interface IPermissionsService
    {
        bool HasPermission(string permission, ulong userId);

        ValueTask<bool> AddPermissionAsync(string permission, ulong userId);

        ValueTask<bool> RemovePermissionAsync(string permission, ulong userId);
    }
}
