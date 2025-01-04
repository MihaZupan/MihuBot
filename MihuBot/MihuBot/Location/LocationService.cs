using Microsoft.EntityFrameworkCore;
using MihuBot.DB;
using System.ComponentModel.DataAnnotations;

namespace MihuBot.Location;

public sealed class LocationService
{
    private readonly OpenWeatherClient _openWeather;
    private readonly OpenAIService _openAI;
    private readonly IDbContextFactory<MihuBotDbContext> _db;

    public LocationService(OpenWeatherClient openWeather, OpenAIService openAI, IDbContextFactory<MihuBotDbContext> db)
    {
        _openWeather = openWeather;
        _openAI = openAI;
        _db = db;
    }

    public async Task<UserLocation?> GetUserLocationAsync(ulong userId)
    {
        await using var context = _db.CreateDbContext();

        return await context.UserLocation.FindAsync((long)userId);
    }

    public async Task<UserLocation?> FindUserLocationByQueryAsync(ulong userId, string query)
    {
        var location = await _openWeather.GetLocationAsync(query);

        if (location is null)
        {
            query = await _openAI.GetSimpleChatCompletionAsync(userId,
                $"Where is \"{query}\"?. Reply with only the city name, state code (only for the US) and country code divided by comma, or reply with \"I don't know\". Please use ISO 3166 country codes.");

            if (query.Contains("don't know", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            location = await _openWeather.GetLocationAsync(query);

            if (location is null)
            {
                return null;
            }
        }

        var userLocation = new UserLocation()
        {
            UserId = (long)userId,
            Name = location.Name,
            Latitude = location.Latitude,
            Longitude = location.Longitude,
        };

        await using var context = _db.CreateDbContext();
        context.UserLocation.Update(userLocation);
        await context.SaveChangesAsync();

        return userLocation;
    }

    public sealed class UserLocation
    {
        [Key]
        public long UserId { get; set; }

        public string Name { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
