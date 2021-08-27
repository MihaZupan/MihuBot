namespace MihuBot.Permissions
{
    public sealed class PermissionsService : IPermissionsService
    {
        private readonly Dictionary<string, HashSet<ulong>> _root;
        private readonly SynchronizedLocalJsonStore<Dictionary<string, HashSet<ulong>>> _store;
        private readonly ReaderWriterLockSlim _lock;

        public PermissionsService()
        {
            _store = new SynchronizedLocalJsonStore<Dictionary<string, HashSet<ulong>>>("Permissions.json");
            _root = _store.DangerousGetValue();
            _lock = new ReaderWriterLockSlim();
        }

        public bool HasPermission(string permission, ulong userId)
        {
            if (Constants.Admins.Contains(userId))
                return true;

            _lock.EnterReadLock();
            try
            {
                return _root.TryGetValue(permission, out HashSet<ulong> userIds)
                    && userIds.Contains(userId);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public async ValueTask<bool> AddPermissionAsync(string permission, ulong userId)
        {
            await _store.EnterAsync();
            try
            {
                _lock.EnterWriteLock();

                if (!_root.TryGetValue(permission, out HashSet<ulong> userIds))
                    userIds = _root[permission] = new HashSet<ulong>();

                return userIds.Add(userId);
            }
            finally
            {
                _lock.ExitWriteLock();
                _store.Exit();
            }
        }

        public async ValueTask<bool> RemovePermissionAsync(string permission, ulong userId)
        {
            await _store.EnterAsync();
            try
            {
                _lock.EnterWriteLock();

                if (!_root.TryGetValue(permission, out HashSet<ulong> userIds))
                    return false;

                if (!userIds.Remove(userId))
                    return false;

                if (userIds.Count == 0)
                {
                    _root.Remove(permission);
                }

                return true;
            }
            finally
            {
                _lock.ExitWriteLock();
                _store.Exit();
            }
        }
    }
}
