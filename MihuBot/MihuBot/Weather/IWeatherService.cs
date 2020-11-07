using System.Threading.Tasks;

namespace MihuBot.Weather
{
    public interface IWeatherService
    {
        public Task<WeatherData> GetWeatherDataAsync(string location);
    }
}
