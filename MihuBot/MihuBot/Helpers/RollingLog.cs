﻿using System.Runtime.InteropServices;

namespace MihuBot.Helpers;

internal sealed class RollingLog
{
    private readonly int _capacity;
    private readonly List<string> _lines;
    private int _discarded;

    public RollingLog(int capacity)
    {
        _lines = new List<string>(capacity);
        _capacity = capacity;
    }

    public void AddLines(ReadOnlySpan<string> lines)
    {
        lock (this)
        {
            int toAdd = Math.Min(_capacity - _lines.Count, lines.Length);
            foreach (string line in lines.Slice(0, toAdd))
            {
                _lines.Add(line);
            }

            lines = lines.Slice(toAdd);

            while (!lines.IsEmpty)
            {
                int spaceNeeded = Math.Min(_capacity, lines.Length);
                _lines.RemoveRange(0, spaceNeeded);
                _discarded += spaceNeeded;

                foreach (string line in lines.Slice(0, spaceNeeded))
                {
                    _lines.Add(line);
                }

                lines = lines.Slice(spaceNeeded);
            }
        }
    }

    public int Get(string[] lines, ref int position)
    {
        lock (this)
        {
            int toSkip = Math.Max(0, position - _discarded);
            int available = Math.Min(lines.Length, _lines.Count - toSkip);
            CollectionsMarshal.AsSpan(_lines).Slice(toSkip, available).CopyTo(lines);
            position = _discarded + toSkip + available;
            return available;
        }
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        lock (this)
        {
            foreach (string line in _lines)
            {
                builder.Append(line);
                builder.Append('\n');
            }
        }
        return builder.ToString();
    }
}
