using MihuBot.Location;

namespace MihuBot.Commands;

public sealed class WeatherCommand : CommandBase
{
    public override string Command => "weather";

    protected override TimeSpan Cooldown => TimeSpan.FromSeconds(30);
    protected override int CooldownToleranceCount => 60;

    private readonly OpenWeatherClient _openWeather;
    private readonly LocationService _locationService;

    public WeatherCommand(OpenWeatherClient openWeather, LocationService locationService)
    {
        _openWeather = openWeather;
        _locationService = locationService;
    }

    public override async Task ExecuteAsync(CommandContext ctx)
    {
        var location = string.IsNullOrEmpty(ctx.ArgumentString)
            ? await _locationService.GetUserLocationAsync(ctx.AuthorId)
            : await _locationService.FindUserLocationByQueryAsync(ctx.AuthorId, ctx.ArgumentString);

        if (location is null)
        {
            if (string.IsNullOrEmpty(ctx.ArgumentString))
            {
                await ctx.ReplyAsync("Please specify a location like `!weather Mars`");
            }
            else
            {
                await ctx.ReplyAsync("Sorry, couldn't find that");
            }
            return;
        }

        OpenWeatherClient.WeatherData weather;
        try
        {
            weather = await _openWeather.GetWeatherAsync(location.Latitude, location.Longitude);
            ArgumentNullException.ThrowIfNull(weather);
        }
        catch (Exception ex)
        {
            ctx.DebugLog(ex, $"{location.Latitude} {location.Longitude}");
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
            .WithTitle($"{location.Name} ({weather.Country})")
            .WithDescription($"Currently: {weather.Description}\nTemperature: {temperature}\nFeels like: {feelsLike}\nLocal time: {localTime}")
            .WithThumbnailUrl(weather.IconUrl)
            .WithColor(color);

        await ctx.Channel.SendMessageAsync(embed: embed.Build());
    }

    private static double ToFahrenheit(double celsius) => (celsius * 1.8d) + 32;
}
