#nullable enable

namespace MihuBot.Helpers;

public static class ProcessHelper
{
    public static async Task RunProcessAsync(string fileName, string arguments, List<string>? output = null, CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }
        };

        process.Start();

        await Task.WhenAll(
            Task.Run(() => ReadOutputStreamAsync(process.StandardOutput), CancellationToken.None),
            Task.Run(() => ReadOutputStreamAsync(process.StandardError), CancellationToken.None),
            process.WaitForExitAsync(cancellationToken));

        async Task ReadOutputStreamAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken) is string line)
            {
                if (output is not null)
                {
                    lock (output)
                    {
                        output.Add(line);
                    }
                }
            }
        }
    }
}
