using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace MihuBot.Helpers
{
    public sealed class StreamerSongListClient
    {
        private readonly HttpClient _http;
        private readonly Logger _logger;

        public StreamerSongListClient(HttpClient httpClient, Logger logger)
        {
            _http = httpClient;
            _logger = logger;
        }

        public async Task<bool> TryAddSongAsync(string title, string artist)
        {
            try
            {
                var requestModel = new RequestModel
                {
                    Title = title,
                    Artist = artist,
                    Active = true,
                    MinAmount = 0,
                    Attributes = Array.Empty<string>()
                };

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.streamersonglist.com/v1/streamers/72/songs")
                {
                    Content = new StringContent(JsonConvert.SerializeObject(requestModel), Encoding.UTF8, "application/json")
                };

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
                request.Headers.Add("origin", "https://www.streamersonglist.com");
                request.Headers.Add("referer", "https://www.streamersonglist.com/");
                request.Headers.Add("sec-fetch-dest", "empty");
                request.Headers.Add("sec-fetch-mode", "cors");
                request.Headers.Add("sec-fetch-site", "same-site");
                request.Headers.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/86.0.4240.111 Safari/537.36");
                request.Headers.Add("x-ssl-user-types", "mod");
                request.Headers.Add("authorization", "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJ1c2VySWQiOjgwNDQyLCJ1c2VybmFtZSI6Im1paGF6dXBhbiIsInN0cmVhbWVySWQiOm51bGwsImlhdCI6MTYwMjAwODMyOCwiZXhwIjoxNjA0NjAwMzI4LCJpc3MiOiJzdHJlYW1lcnNvbmdsaXN0LmNvbSJ9.XzjVIstusJJBkSUmc4Q3RkRvijLKY9uMAhTguBddRos");

                using var response = await _http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex)
            {
                await _logger.DebugAsync(ex.ToString());
                return false;
            }
        }

        [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
        private sealed class RequestModel
        {
            public string Title { get; set; }
            public string Artist { get; set; }
            public bool Active { get; set; }
            public int MinAmount { get; set; }
            public string[] Attributes { get; set; }
        }
    }
}
