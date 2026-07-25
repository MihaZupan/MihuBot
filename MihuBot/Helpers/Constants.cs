using System.Reflection;

namespace MihuBot.Helpers;

public static class BuildInfo
{
    public static string GetCommitId()
    {
        return GetCommitId(typeof(Program).Assembly);
    }

    public static string GetCommitId(Assembly assembly)
    {
        string commit = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (commit is not null)
        {
            int plusOffset = commit.IndexOf('+');
            if (plusOffset >= 0)
            {
                commit = commit.Substring(plusOffset + 1);
            }
        }

        return commit ?? "unknown";
    }
}

public static class Constants
{
    public static string StateDirectory => "State";

    // Dev credentials are used everywhere except the Linux deployment.
    public static string DevSuffix { get; } = OperatingSystem.IsLinux() ? "" : "-dev";

    // Bulk file storage for the StorageService. Defaults to living under the state directory,
    // but may point elsewhere (e.g. a separate Docker volume) via MIHUBOT_STORAGE_DIRECTORY.
    public static string StorageDirectory { get; } =
        Environment.GetEnvironmentVariable("MIHUBOT_STORAGE_DIRECTORY") is { Length: > 0 } dir
            ? dir
            : $"{StateDirectory}/Files";

    public static readonly FrozenSet<ulong> Admins = FrozenSet.Create(
    [
        KnownUsers.Miha,
    ]);

    public const long MihuTelegramId = 168175103;

    public const float VCDefaultVolume = 0.40f;

    public static readonly string[] NumberEmojis =
    [
        ":zero:", ":one:", ":two:", ":three:", ":four:", ":five:", ":six:", ":seven:", ":eight:", ":nine:"
    ];

    private const char CombiningEnclosingKeycap = '⃣';

    public static readonly IEmote[] NumberEmotes = Enumerable
        .Range(0, 9)
        .Select(i => i.ToString() + CombiningEnclosingKeycap)
        .Select(e => new Emoji(e))
        .ToArray();

    public static readonly HashSet<string> NetworkingLabels =
    [
        "area-System.Net",
        "area-System.Net.Http",
        "area-System.Net.Security",
        "area-System.Net.Sockets",
        "area-System.Net.Quic",
        "area-Extensions-HttpClientFactory"
    ];
}

public static class Guilds
{
    public const ulong Mihu             = 566925785563136020ul;
    public const ulong PrivateLogs      = 750706593858977802ul;
    public const ulong LiverGang        = 244642778024378368ul;
    public const ulong RetirementHome   = 357322948501045259ul;
    public const ulong TheBoys          = 890697765615697960ul;
    public const ulong BushNation       = 439527451937341461ul;
}

public static class Channels
{
    public const ulong Debug            = 806048964021190656ul;
    public const ulong PrivateGeneral   = 750706594412757094ul;
    public const ulong TheBoysTgRelay   = 1152429855355457617ul;
    public const ulong LogText          = 750706839431413870ul;
    public const ulong Files            = 774147493319540736ul;
    public const ulong TheBoysSpam      = 924503695738171402ul;
    public const ulong DuplicatesList   = 1396832159888703498ul;
    public const ulong DuplicatesPosted = 1396843623898939462ul;
    public const ulong SuggestedLabels  = 1464776244586483722ul;
}

public static class KnownUsers
{
    public static ulong MihuBot = OperatingSystem.IsLinux()
        ? 710370560596770856ul
        : 767172321999585281ul;

    // Admins
    public const ulong Miha         = 162569877087977480ul;
}

public static class Emotes
{
    // Mihu
    public static Emote EyesShaking { get; }    = Emote.Parse("<a:eyesShaking:719904795091009636>");
    public static Emote KissAHomie { get; }     = Emote.Parse("<a:KissAHomie:769335184750805003>");
    public static Emote OmegaLUL { get; }       = Emote.Parse("<:OMEGALUL:775860675938353202>");
    public static Emote PepePoint { get; }      = Emote.Parse("<:pepePoint:701207439273361408>");
    public static Emote WeirdChamp { get; }     = Emote.Parse("<:WeirdChamp:715663367741898785>");

    // The Boys
    public static Emote SomeoneSayCiv { get; }  = Emote.Parse("<:DIDSOMEONESAYCIV:968527414256877608>");

    // Liv
    public static Emote SenpaiLove { get; }     = Emote.Parse("<:senpaiLove:681560481214889999>");

    // Paul
    public static Emote KermitUwU { get; }      = Emote.Parse("<:KermitUwU:716355675457847336>");
    public static Emote MonkaStab { get; }      = Emote.Parse("<:monkaStab:715603083345789088>");

    public static IEmote ThumbsUp { get; }      = new Emoji("👍");
    public static IEmote RedCross { get; }      = new Emoji("❌");
    public static IEmote Heart { get; }         = new Emoji("❤️");
    public static IEmote Stopwatch { get; }     = new Emoji("⏱️");

    public static IEmote RegionalIndicator_K { get; } = new Emoji("🇰");
}
