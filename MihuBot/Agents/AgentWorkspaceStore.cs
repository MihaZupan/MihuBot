namespace MihuBot.Agents;

internal sealed class AgentWorkspaceStore : IDisposable
{
    private readonly string _root;
    private readonly FileStream _ownerLock;

    public AgentWorkspaceStore(string root, params string[] excludedDirectories)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ArgumentException("The agent workspace root must be an absolute path.", nameof(root));
        }

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        foreach (string excluded in excludedDirectories)
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(excluded));

            if (ContainsPath(_root, fullPath) || ContainsPath(fullPath, _root))
            {
                throw new ArgumentException("Agent workspaces must not overlap MihuBot's directories.", nameof(root));
            }
        }

        for (DirectoryInfo directory = new(_root); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 && directory.Exists)
            {
                throw new IOException("The agent workspace root must not contain symbolic links.");
            }
        }

        Directory.CreateDirectory(_root);
        _ownerLock = new FileStream(Path.Combine(_root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(_root))
            {
                if (IsRunDirectory(directory))
                {
                    Delete(directory);
                }
            }
        }
        catch
        {
            _ownerLock.Dispose();
            throw;
        }
    }

    public string Create()
    {
        string directory = Path.Combine(_root, $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Delete(string directory)
    {
        directory = Path.GetFullPath(directory);

        if (!string.Equals(Path.GetDirectoryName(directory), _root, PathComparison) || !IsRunDirectory(directory))
        {
            throw new ArgumentException("Only an agent run directory can be deleted.", nameof(directory));
        }

        if (Directory.Exists(directory))
        {
            var info = new DirectoryInfo(directory);

            if (OperatingSystem.IsWindows() && (info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                // Git checkouts can contain read-only files. Never walk through links while clearing attributes.
                foreach (FileSystemInfo entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false
                }))
                {
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                }

                info.Attributes &= ~FileAttributes.ReadOnly;
            }

            // Directory.Delete removes directory symlinks themselves, without traversing their targets.
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static Dictionary<string, string> CreateEnvironment(string directory)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "LANG", "LC_ALL" })
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        string home = Path.Combine(directory, "home");
        string temp = Path.Combine(directory, "tmp");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(temp);

        environment["HOME"] = home;
        environment["USERPROFILE"] = home;
        environment["APPDATA"] = Path.Combine(home, "config");
        environment["LOCALAPPDATA"] = Path.Combine(home, "local");
        environment["XDG_CONFIG_HOME"] = Path.Combine(home, "config");
        environment["XDG_CACHE_HOME"] = Path.Combine(home, "cache");
        environment["XDG_DATA_HOME"] = Path.Combine(home, "data");
        environment["TMP"] = temp;
        environment["TEMP"] = temp;
        environment["TMPDIR"] = temp;
        environment["COPILOT_DISABLE_KEYTAR"] = "1";
        return environment;
    }

    private static bool IsRunDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        return name.StartsWith("run-", StringComparison.Ordinal) && Guid.TryParseExact(name.AsSpan(4), "N", out _);
    }

    private static bool ContainsPath(string parent, string child) =>
        string.Equals(parent, child, PathComparison) ||
        child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void Dispose() => _ownerLock.Dispose();
}
