﻿namespace MihuBot.Helpers;

public sealed class FileBackedHashSet
{
    private readonly Stream _stream;
    private readonly HashSet<string> _hashSet;

    public FileBackedHashSet(string filePath, IEqualityComparer<string> comparer = null)
    {
        filePath = $"{Constants.StateDirectory}/{filePath}";
        comparer ??= StringComparer.Ordinal;

        if (File.Exists(filePath))
        {
            string[] lines = File.ReadAllLines(filePath);
            _hashSet = new HashSet<string>(lines, comparer);
        }
        else
        {
            _hashSet = new HashSet<string>(comparer);
        }

        _stream = File.Open(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public bool Contains(string value)
    {
        lock (_hashSet)
        {
            return _hashSet.Contains(value);
        }
    }

    public bool TryAdd(string value)
    {
        Debug.Assert(!value.Contains('\n'));

        lock (_hashSet)
        {
            if (!_hashSet.Add(value))
            {
                return false;
            }
        }

        lock (_stream)
        {
            _stream.Write(Encoding.UTF8.GetBytes(value));
            _stream.WriteByte((byte)'\n');
            _stream.Flush();
            return true;
        }
    }
}
