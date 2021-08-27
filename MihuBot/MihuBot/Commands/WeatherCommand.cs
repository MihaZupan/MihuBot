using MihuBot.Weather;

namespace MihuBot.Commands
{
    public sealed class WeatherCommand : CommandBase
    {
        public override string Command => "weather";

        protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);
        protected override int CooldownToleranceCount => 60;

        private readonly IWeatherService _weather;
        private readonly SynchronizedLocalJsonStore<Dictionary<ulong, string>> _locations =
            new SynchronizedLocalJsonStore<Dictionary<ulong, string>>("WeatherLocations.json");

        public WeatherCommand(IWeatherService weather)
        {
            _weather = weather;
        }

        public override async Task ExecuteAsync(CommandContext ctx)
        {
            string location = ctx.ArgumentString;
            bool saved = false;

            if (string.IsNullOrEmpty(location))
            {
                location = await _locations.QueryAsync(l => l.TryGetValue(ctx.AuthorId, out string loc) ? loc : null);
                saved = true;

                if (location is null)
                {
                    await ctx.ReplyAsync("Please specify a location like `!weather Mars`");
                    return;
                }
            }

            WeatherData weather;
            try
            {
                weather = await _weather.GetWeatherDataAsync(location);
            }
            catch (Exception ex)
            {
                ctx.DebugLog(ex, location);
                await ctx.ReplyAsync("Sorry, couldn't find that", mention: true);
                return;
            }

            location = weather.Name;

            Color color = weather.FeelsLike switch
            {
                var temp when temp < -10 => new Color(255, 255, 255),
                var temp when temp < 0   => new Color(0, 255, 197),
                var temp when temp < 10  => new Color(0, 255, 154),
                var temp when temp < 20  => new Color(235, 255, 0),
                var temp when temp < 30  => new Color(241, 187, 0),
                var temp when temp < 40  => new Color(255, 142, 0),
                _ => new Color(255, 0, 0)
            };

            string temperature = $"{weather.FeelsLike:N1} °C / {ToFahrenheit(weather.FeelsLike):N1} F";
            string localTime = DateTime.UtcNow.AddSeconds(weather.Timezone).ToString("HH:mm (h:mm tt)");

            var embed = new EmbedBuilder()
                .WithTitle($"Weather in {location}")
                .WithDescription($"Currently: {weather.Description}\nTemperature: {temperature}\nLocal time: {localTime}")
                .WithThumbnailUrl(weather.IconUrl)
                .WithColor(color);

            await ctx.Channel.SendMessageAsync(embed: embed.Build());

            if (!saved)
            {
                var locations = await _locations.EnterAsync();
                try
                {
                    locations[ctx.AuthorId] = location;
                }
                finally
                {
                    _locations.Exit();
                }
            }
        }

        private static double ToFahrenheit(double celsius) => (celsius * 1.8d) + 32;
    }
}
