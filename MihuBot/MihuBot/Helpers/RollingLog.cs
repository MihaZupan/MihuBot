using System.Runtime.InteropServices;

namespace MihuBot.Helpers
{
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

        public void AddLines(string[] lines)
        {
            lock (this)
            {
                int toAdd = Math.Min(_capacity - _lines.Count, lines.Length);
                foreach (string line in lines.AsSpan(0, toAdd))
                {
                    _lines.Add(line);
                }

                ReadOnlySpan<string> leftOver = lines.AsSpan(toAdd);
                while (!leftOver.IsEmpty)
                {
                    int spaceNeeded = Math.Min(_capacity, leftOver.Length);
                    _lines.RemoveRange(0, spaceNeeded);
                    _discarded += spaceNeeded;

                    foreach (string line in lines.AsSpan(0, spaceNeeded))
                    {
                        _lines.Add(line);
                    }

                    leftOver = leftOver.Slice(spaceNeeded);
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
    }
}
