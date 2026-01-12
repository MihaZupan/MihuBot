using System.IO.Compression;

namespace MihuBot.Helpers;

public static class ZipArchiveHelpers
{
    public static byte[] ToArray(this ZipArchiveEntry entry)
    {
        byte[] bytes = new byte[entry.Length];

        using (var ms = new MemoryStream(bytes))
        using (Stream entryStream = entry.Open())
        {
            entryStream.CopyTo(ms);

            if (ms.Length != bytes.Length)
            {
                throw new InvalidOperationException("The ZipArchiveEntry stream did not contain the expected number of bytes.");
            }
        }

        return bytes;
    }
}
