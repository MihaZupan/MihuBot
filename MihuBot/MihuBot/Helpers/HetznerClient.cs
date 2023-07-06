using System.Text.Json.Serialization;
using System.Text.Json;
using System.Net.Http.Json;

namespace MihuBot.Helpers
{
    public sealed class HetznerClient
    {
        private static readonly JsonSerializerOptions s_hetznerJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        private readonly HttpClient _http;
        private readonly Logger _logger;
        private readonly string _apiKey;

        public HetznerClient(HttpClient http, Logger logger, IConfiguration configuration)
        {
            _http = http;
            _logger = logger;
            _apiKey = configuration["Hetzner:ApiKey"] ?? throw new ArgumentException("Missing Hetzner API Key");
        }

        public async Task<HetznerServerResponse> CreateServerAsync(string name, string image, string location, string serverType, string userData, CancellationToken cancellationToken)
        {
            return await PostAsJsonAsync<HetznerServerResponse>("servers", new
            {
                Name = name,
                Image = image.ToLowerInvariant(),
                Location = location.ToLowerInvariant(),
                ServerType = serverType.ToLowerInvariant(),
                UserData = userData,
            }, cancellationToken);
        }

        public async Task<HetznerServerResponse> DeleteServerAsync(long serverId, CancellationToken cancellationToken)
        {
            return await DeleteAsync<HetznerServerResponse>($"servers/{serverId}", cancellationToken);
        }

        //private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
        //{
        //    var request = CreateRequestMessage(url, HttpMethod.Get);
        //
        //    return await SendRequestAsync<T>(request, cancellationToken);
        //}

        private async Task<T> DeleteAsync<T>(string url, CancellationToken cancellationToken)
        {
            var request = CreateRequestMessage(url, HttpMethod.Delete);

            return await SendRequestAsync<T>(request, cancellationToken);
        }

        private async Task<T> PostAsJsonAsync<T>(string actionName, object body, CancellationToken cancellationToken)
        {
            var request = CreateRequestMessage(actionName, HttpMethod.Post);
            request.Content = JsonContent.Create(body, options: s_hetznerJsonOptions);

            return await SendRequestAsync<T>(request, cancellationToken);
        }

        private HttpRequestMessage CreateRequestMessage(string actionName, HttpMethod method)
        {
            string url = $"https://api.hetzner.cloud/v1/{actionName}";

            var request = new HttpRequestMessage(method, url);

            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            return request;
        }

        private async Task<T> SendRequestAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                _logger.DebugLog($"Hetzner server request: {await request.Content.ReadAsStringAsync(cancellationToken)}");
            }

            using var response = await _http.SendAsync(request, cancellationToken);

            string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.DebugLog($"Hetzner server response: {responseJson}");

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Failed to post to {request.RequestUri?.AbsoluteUri}: {response.StatusCode}");
            }

            return JsonSerializer.Deserialize<T>(responseJson, s_hetznerJsonOptions)
                ?? throw new Exception("Deserialized a null response");
        }


        public sealed class HetznerServerResponse
        {
            public HetznerServer? Server { get; set; }
            public string? RootPassword { get; set; }

            public sealed class HetznerServer
            {
                public long Id { get; set; }
                public HetznerServerType? ServerType { get; set; }
                public HetznerPublicNet? PublicNet { get; set; }
            }

            public sealed class HetznerServerType
            {
                public string? Name { get; set; }
                public string? CpuType { get; set; }
                public string? Architecture { get; set; }
                public double Cores { get; set; }
                public double Memory { get; set; }
                public double Disk { get; set; }
            }

            public sealed class HetznerPublicNet
            {
                public HetznerIp? Ipv4 { get; set; }
            }

            public sealed class HetznerIp
            {
                public string? Ip { get; set; }
            }
        }
    }
}
