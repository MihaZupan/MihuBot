using System.Buffers.Text;
using System.Security.Cryptography;

namespace MihuBot.Helpers;

public sealed class StorageClient
{
    private const string Host = "https://storage.mihubot.xyz";
    private const string PathPrefix = "/s";

    private readonly HttpClient _http;
    private readonly string _containerName;
    private readonly string _containerUrl;
    private readonly byte[] _sasKey;
    private readonly bool _isPublic;

    public StorageClient(HttpClient httpClient, string containerName, string sasKey, bool isPublic)
    {
        _http = httpClient;
        _containerName = containerName;
        _containerUrl = $"{Host}{PathPrefix}/{containerName}";
        _sasKey = Encoding.UTF8.GetBytes(sasKey);
        _isPublic = isPublic;
    }

    public string GetFileUrl(string path, TimeSpan duration, bool writeAccess)
    {
        if (_isPublic && !writeAccess)
        {
            return $"{_containerUrl}/{path}";
        }

        string toSign = GetUnsignedUrl($"{PathPrefix}/{_containerName}/{path}", duration, writeAccess);
        return $"{Host}{toSign}&sig={Sign(toSign)}";
    }

    public static string GetFileUrl(string containerSasUrl, string path)
    {
        int queryOffset = containerSasUrl.IndexOf('?');
        return $"{containerSasUrl.AsSpan(0, queryOffset)}/{path}{containerSasUrl.AsSpan(queryOffset)}";
    }

    public string GetContainerUrl(TimeSpan duration, bool writeAccess)
    {
        if (_isPublic && !writeAccess)
        {
            return _containerUrl;
        }

        string toSign = GetUnsignedUrl(_containerName, duration, writeAccess);
        return $"{Host}{PathPrefix}/{toSign}&sig={Sign(toSign)}";
    }

    private static string GetUnsignedUrl(string @base, TimeSpan duration, bool writeAccess)
    {
        DateTime expiration = DateTime.UtcNow.Add(duration);
        return $"{@base}?exp={expiration:yyyy-MM-dd_HH-mm-ss}&w={(writeAccess ? "1" : "0")}";
    }

    private string Sign(string toSign)
    {
        byte[] sig = HMACSHA256.HashData(_sasKey, Encoding.UTF8.GetBytes(toSign));
        return Base64Url.EncodeToString(sig);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, GetFileUrl(path, TimeSpan.FromMinutes(1), writeAccess: false));
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
        return response.StatusCode == HttpStatusCode.OK;
    }

    public async Task UploadAsync(string path, Stream stream, CancellationToken cancellationToken = default)
    {
        string url = GetFileUrl(path, TimeSpan.FromMinutes(1), writeAccess: true);
        using HttpResponseMessage response = await _http.PostAsync(url, new StreamContent(stream), cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
