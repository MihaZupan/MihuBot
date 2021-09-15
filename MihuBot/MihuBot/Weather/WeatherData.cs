namespace MihuBot.Weather
{
    public sealed class WeatherData
    {
        public string CityName;
        public long CityId;
        public string Country;
        public int Timezone;

        public double Temp;
        public double FeelsLike;
        public double TempMin;
        public double TempMax;
        public double Pressure;
        public double Humidity;
        public double WindSpeed;

        public string Description;
        public string IconUrl;
    }
}
