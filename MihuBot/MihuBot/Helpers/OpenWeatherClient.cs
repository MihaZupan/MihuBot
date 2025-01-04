using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

#nullable enable

namespace MihuBot.Helpers;

public partial class OpenWeatherClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public OpenWeatherClient(HttpClient httpClient, IConfiguration configuration)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = configuration["OpenWeather:ApiKey"] ?? throw new InvalidOperationException("Missing OpenWeather API key");
    }

    public async Task<WeatherData?> GetWeatherAsync(double latitude, double longitude)
    {
        var response = await GetAsync($"data/2.5/weather?lat={latitude}&lon={longitude}&units=metric", OpenWeatherContext.Default.OpenWeatherModel);

        if (response?.Main is null || response.Sys is null || response.Weather is null)
        {
            return null;
        }

        return new WeatherData()
        {
            Temp = response.Main.Temp,
            TempMin = response.Main.TempMin,
            TempMax = response.Main.TempMax,
            FeelsLike = response.Main.FeelsLike,
            Humidity = response.Main.Humidity,
            Pressure = response.Main.Pressure,
            WindSpeed = response.Wind?.Speed ?? double.NaN,
            Country = response.Sys.Country,
            CityName = response.Name,
            CityId = response.Id,
            Timezone = response.Timezone,
            Description = response.Weather.First().Description,
            IconUrl = $"http://openweathermap.org/img/wn/{response.Weather.First().Icon}@2x.png",
        };
    }

    public async Task<LocationData?> GetLocationAsync(string query)
    {
        var response = await GetAsync($"geo/1.0/direct?q={Uri.EscapeDataString(query)}&limit=1", OpenWeatherContext.Default.GeocodingModelArray);

        if (response?.FirstOrDefault() is not { } location)
        {
            return null;
        }

        return new LocationData()
        {
            Name = location.Name,
            Latitude = location.Lat,
            Longitude = location.Lon,
            Country = location.Country,
            State = location.State,
        };
    }

    private async Task<T?> GetAsync<T>(string query, JsonTypeInfo<T> typeInfo) where T : class
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using HttpResponseMessage response = await _http.GetAsync($"https://api.openweathermap.org/{query}&appid={_apiKey}", HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync(typeInfo, cts.Token);
    }

    public sealed class WeatherData
    {
        public string? CityName { get; set; }
        public long CityId { get; set; }
        public string? Country { get; set; }
        public int Timezone { get; set; }

        public double Temp { get; set; }
        public double FeelsLike { get; set; }
        public double TempMin { get; set; }
        public double TempMax { get; set; }
        public double Pressure { get; set; }
        public double Humidity { get; set; }
        public double WindSpeed { get; set; }

        public string? Description { get; set; }
        public string? IconUrl { get; set; }
    }

    public sealed class LocationData
    {
        public string? Name { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? Country { get; set; }
        public string? State { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(OpenWeatherModel))]
    [JsonSerializable(typeof(GeocodingModel[]))]
    private partial class OpenWeatherContext : JsonSerializerContext { }

    private sealed class GeocodingModel
    {
        public string? Name { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string? Country { get; set; }
        public string? State { get; set; }
    }

    private sealed class OpenWeatherModel
    {
        public Coordinates? Coords { get; set; }
        public WeatherModel[]? Weather { get; set; }
        public string? Base { get; set; }
        public MainModel? Main { get; set; }
        public double Visibility { get; set; }
        public WindModel? Wind { get; set; }
        public CloudModel? Clouds { get; set; }
        public int Dt { get; set; }
        public SysModel? Sys { get; set; }
        public int Timezone { get; set; }
        public long Id { get; set; }
        public string? Name { get; set; }
        public int Cod { get; set; }

        public sealed class Coordinates
        {
            public double Lon { get; set; }
            public double Lat { get; set; }
        }

        public sealed class WeatherModel
        {
            public int Id { get; set; }
            public string? Main { get; set; }
            public string? Description { get; set; }
            public string? Icon { get; set; }
        }

        public sealed class MainModel
        {
            public double Temp { get; set; }
            public double FeelsLike { get; set; }
            public double TempMin { get; set; }
            public double TempMax { get; set; }
            public double Pressure { get; set; }
            public double Humidity { get; set; }
        }

        public sealed class WindModel
        {
            public double Speed { get; set; }
            public double Deg { get; set; }
        }

        public sealed class CloudModel
        {
            public double All { get; set; }
        }

        public sealed class SysModel
        {
            public int Type { get; set; }
            public int Id { get; set; }
            public string? Country { get; set; }
            public int Sunrise { get; set; }
            public int Sunset { get; set; }
        }
    }
}
