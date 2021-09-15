using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MihuBot.Weather
{
    public sealed class WeatherService : IWeatherService
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;

        public WeatherService(HttpClient httpClient, IConfiguration configuration)
        {
            _http = httpClient;
            _apiKey = configuration["OpenWeather:ApiKey"];
        }

        public async Task<WeatherData> GetWeatherDataAsync(string location, long? cityId)
        {
            if (location is null && cityId is null)
            {
                throw new ArgumentNullException(nameof(location));
            }

            string query = location is null ? $"id={cityId}" : $"q={Uri.EscapeDataString(location)}";
            string url = $"https://api.openweathermap.org/data/2.5/weather?{query}&units=metric&appid={_apiKey}";
            string json = await _http.GetStringAsync(url);
            OpenWeatherModel response = JsonConvert.DeserializeObject<OpenWeatherModel>(json);

            return new WeatherData()
            {
                Temp = response.Main.Temp,
                TempMin = response.Main.TempMin,
                TempMax = response.Main.TempMax,
                FeelsLike = response.Main.FeelsLike,
                Humidity = response.Main.Humidity,
                Pressure = response.Main.Pressure,
                WindSpeed = response.Wind.Speed,
                Country = response.Sys.Country,
                CityName = response.Name,
                CityId = response.Id,
                Timezone = response.Timezone,
                Description = response.Weather.First().Description,
                IconUrl = $"http://openweathermap.org/img/wn/{response.Weather.First().Icon}@2x.png",
            };
        }

#pragma warning disable CS0649

        [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
        private class OpenWeatherModel
        {
            public Coordinates Coords;
            public WeatherModel[] Weather;
            public string Base;
            public MainModel Main;
            public double Visibility;
            public WindModel Wind;
            public CloudModel Clouds;
            public int Dt;
            public SysModel Sys;
            public int Timezone;
            public long Id;
            public string Name;
            public int Cod;

            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class Coordinates
            {
                public double Lon;
                public double Lat;
            }

            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class WeatherModel
            {
                public int Id;
                public string Main;
                public string Description;
                public string Icon;
            }

            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class MainModel
            {
                public double Temp;
                public double FeelsLike;
                public double TempMin;
                public double TempMax;
                public double Pressure;
                public double Humidity;
            }

            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class WindModel
            {
                public double Speed;
                public double Deg;
            }

            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class CloudModel
            {
                public double All;
            }

            [JsonObject(NamingStrategyType = typeof(SnakeCaseNamingStrategy))]
            public class SysModel
            {
                public int Type;
                public int Id;
                public string Country;
                public int Sunrise;
                public int Sunset;
            }
        }

#pragma warning restore
    }
}
