namespace MihuBot.NextCloud
{
    public sealed class NextCloudClient
    {
        private static readonly HttpMethod HttpMethodMKCOL = new("MKCOL");

        private readonly HttpClient _httpClient;
        private readonly string _basicAuth;
        private readonly string _fileUploadUri;

        public NextCloudClient(HttpClient httpClient, string serverAddress, string username, string password)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            ArgumentNullException.ThrowIfNull(serverAddress);
            ArgumentNullException.ThrowIfNull(username);
            ArgumentNullException.ThrowIfNull(password);

            _basicAuth = $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"))}";

            var baseUri = new Uri($"https://{serverAddress}/").AbsoluteUri;
            _fileUploadUri = baseUri + $"remote.php/dav/files/{username}/";
        }

        public async Task UploadFileAsync(string fileName, Stream stream, CancellationToken cancellationToken = default)
        {
            var uri = new Uri($"{_fileUploadUri}{fileName}");

            using HttpResponseMessage response = await PutAsync(uri, stream, cancellationToken);

            response.EnsureSuccessStatusCode();
        }

        public async Task CreateDirectoryAsync(string directoryName, CancellationToken cancellationToken = default)
        {
            var uri = new Uri($"{_fileUploadUri}{directoryName}");

            var request = new HttpRequestMessage(HttpMethodMKCOL, uri);

            using HttpResponseMessage response = await SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();
        }

        private Task<HttpResponseMessage> PutAsync(Uri uri, Stream content, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StreamContent(content)
            };

            return SendAsync(request, cancellationToken);
        }

        private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("Authorization", _basicAuth);
            request.Headers.TryAddWithoutValidation("Accept", "Accept: application/json, text/html, */*;q=0.9");
            request.Headers.TryAddWithoutValidation("User-Agent", $"MihuBot-{nameof(NextCloudClient)}");

            return _httpClient.SendAsync(request, cancellationToken);
        }
    }
}
