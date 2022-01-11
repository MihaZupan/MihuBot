namespace MihuBot.Helpers
{
    public sealed class TempFile : IDisposable
    {
        private static readonly string _tempFolder = System.IO.Path.GetTempPath();
        private static long _counter = 1;

        public string Path { get; private set; }

        public TempFile(string extension)
        {
            Path = $"{_tempFolder}/MihuBotTemp_{DateTime.UtcNow.ToISODateTime()}_{Interlocked.Increment(ref _counter)}.{extension.TrimStart('.')}";
        }

        ~TempFile()
        {
            Cleanup();
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Cleanup();
        }

        private void Cleanup()
        {
            try
            {
                File.Delete(Path);
            }
            catch { }
        }

        public override string ToString()
        {
            return Path;
        }
    }
}
