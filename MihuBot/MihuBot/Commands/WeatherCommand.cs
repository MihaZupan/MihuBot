﻿using MihuBot.Weather;

namespace MihuBot.Commands;

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
        _weather = weather ?? throw new ArgumentNullException(nameof(weather));
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
            string cityName = location;
            if (long.TryParse(cityName, out long cityId))
            {
                cityName = null;
            }

            weather = await _weather.GetWeatherDataAsync(cityName, cityId);
        }
        catch (Exception ex)
        {
            ctx.DebugLog(ex, location);
            await ctx.ReplyAsync("Sorry, couldn't find that", mention: true);
            return;
        }

        Color color = weather.FeelsLike switch
        {
            < -10 => new Color(255, 255, 255),
            < 0   => new Color(0, 255, 197),
            < 10  => new Color(0, 255, 154),
            < 20  => new Color(235, 255, 0),
            < 30  => new Color(241, 187, 0),
            < 40  => new Color(255, 142, 0),
            _     => new Color(255, 0, 0)
        };

        string temperature = $"{weather.Temp:N1} °C / {ToFahrenheit(weather.Temp):N1} F";
        string feelsLike = $"{weather.FeelsLike:N1} °C / {ToFahrenheit(weather.FeelsLike):N1} F";
        string localTime = DateTime.UtcNow.AddSeconds(weather.Timezone).ToString("HH:mm (h:mm tt)");

        var embed = new EmbedBuilder()
            .WithTitle($"{weather.CityName} ({weather.Country})")
            .WithDescription($"Currently: {weather.Description}\nTemperature: {temperature}\nFeels like: {feelsLike}\nLocal time: {localTime}")
            .WithThumbnailUrl(weather.IconUrl)
            .WithColor(color);

        await ctx.Channel.SendMessageAsync(embed: embed.Build());

        if (!saved)
        {
            var locations = await _locations.EnterAsync();
            try
            {
                locations[ctx.AuthorId] = weather.CityId.ToString();
            }
            finally
            {
                _locations.Exit();
            }
        }
    }

    private static double ToFahrenheit(double celsius) => (celsius * 1.8d) + 32;
}
