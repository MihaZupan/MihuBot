using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace MihuBot.DB;

public sealed class LogsDbContext : DbContext
{
    public LogsDbContext(DbContextOptions<LogsDbContext> options) : base(options)
    { }

    public DbSet<LogDbEntry> Logs { get; set; }
}

[Table("logs")]
[Index(nameof(Snowflake))]
public sealed partial class LogDbEntry
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public long Id { get; set; }

    public Logger.EventType Type { get; set; }
    public long Snowflake { get; set; }
    public long GuildId { get; set; }
    public long ChannelId { get; set; }
    public long UserId { get; set; }
    public string Content { get; set; }
    public string ExtraContentJson { get; set; }

    public DateTime Timestamp => SnowflakeUtils.FromSnowflake((ulong)Snowflake).UtcDateTime;

    public string AsJson() => JsonSerializer.Serialize(this, s_jsonOptions);

    public static LogDbEntry Create<TContent>(Logger.EventType type, ulong timestamp, ulong guildId, ulong channelId, ulong userId, string content, TContent extraContent)
        where TContent : class
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timestamp, (ulong)long.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(guildId, (ulong)long.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channelId, (ulong)long.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(userId, (ulong)long.MaxValue);

        var entry = new LogDbEntry
        {
            Type = type,
            Snowflake = (long)timestamp,
            GuildId = (long)guildId,
            ChannelId = (long)channelId,
            UserId = (long)userId,
            Content = content,
        };

        if (extraContent is not null)
        {
            entry.ExtraContentJson = JsonSerializer.Serialize(extraContent, s_jsonOptions);
        }

        return entry;
    }

    public void ToString(StringBuilder builder, DiscordSocketClient client)
    {
        DateTime timestamp = Timestamp;

        builder.Append(timestamp.Year);
        builder.Append('-');

        AppendTwoDigits(builder, timestamp.Month);
        builder.Append('-');

        AppendTwoDigits(builder, timestamp.Day);
        builder.Append('_');

        AppendTwoDigits(builder, timestamp.Hour);
        builder.Append('-');

        AppendTwoDigits(builder, timestamp.Minute);
        builder.Append('-');

        AppendTwoDigits(builder, timestamp.Second);
        builder.Append(' ');

        builder.Append(Type.ToString());

        SocketGuild guild = null;
        if (GuildId != 0)
        {
            builder.Append(": ");

            guild = client.GetGuild((ulong)GuildId);
            if (guild is null)
            {
                builder.Append(GuildId);
            }
            else
            {
                builder.Append(guild.Name);
            }
        }

        if (ChannelId != 0)
        {
            builder.Append(GuildId == 0 ? ": " : " - ");

            SocketGuildChannel channel = guild?.GetChannel((ulong)ChannelId);
            if (channel is null)
            {
                builder.Append(ChannelId);
            }
            else
            {
                builder.Append(channel.Name);
            }
        }

        if (UserId != 0)
        {
            builder.Append(" - ");
            string username = client.GetUser((ulong)UserId)?.Username;
            if (username is null)
            {
                builder.Append(UserId);
            }
            else
            {
                builder.Append(username);
            }
        }

        if (Content is not null)
        {
            builder.Append(" - ");
            if (!Content.AsSpan().ContainsAny('\n', '\r'))
            {
                builder.Append(Content);
            }
            else
            {
                builder.Append(Content.NormalizeNewLines().Replace("\n", " <new-line> "));
            }
        }

        if (ExtraContentJson is not null)
        {
            builder.Append(" --- ");
            builder.Append(ExtraContentJson);
        }

        static void AppendTwoDigits(StringBuilder builder, int value)
        {
            if (value < 10) builder.Append('0');
            builder.Append(value);
        }
    }
}