using System.Security.Cryptography;

#nullable enable

namespace MihuBot.Discord.Audio;

internal static class WhisperModel
{
    internal static readonly Uri DownloadUri = new(
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small.en.bin");
    internal const string Sha512 = "529d7638d332ed25549dd0df2f9cfa1248a9dfa46e011b651619b87443146ddafb54d3e5e255dc65c27b785eea2122802c471c4323a89a0abff517040f9ac2b2";
    public static string ModelPath => Path.Combine(Constants.StateDirectory, "Whisper", "ggml-small.en.bin");

    internal static async Task EnsureDownloadedAsync(
        HttpClient http, string path, Uri uri, string expectedHash, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));

        try
        {
            using HttpResponseMessage response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await response.Content.CopyToAsync(file, timeout.Token);
                file.Position = 0;
                byte[] hash = await SHA512.HashDataAsync(file, timeout.Token);

                if (!Convert.ToHexString(hash).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The downloaded Whisper model failed its SHA-512 integrity check.");
                }
            }

            File.Move(temporary, path);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
