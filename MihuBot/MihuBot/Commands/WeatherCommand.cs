using MihuBot.Helpers;
using MihuBot.Weather;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

            await ctx.ReplyAsync($"Weather in {location}: {weather.FeelsLike:N1} °C / {ToFahrenheit(weather.FeelsLike):N1} F");

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
