namespace MihuBot.Helpers
{
    public static class Constants
    {
        public static string StateDirectory => "State";

        public static readonly HashSet<ulong> Admins = new()
        {
            KnownUsers.Miha,
            KnownUsers.James,
            KnownUsers.Jordan,
        };

        public const float VCDefaultVolume = 0.30f;

        public static readonly char[] SpaceAndQuotes = new[] { ' ', '\'', '\"' };

        public static readonly string[] NumberEmojis = new[]
        {
            ":zero:", ":one:", ":two:", ":three:", ":four:", ":five:", ":six:", ":seven:", ":eight:", ":nine:"
        };

        private const char CombiningEnclosingKeycap = '⃣';

        public static readonly IEmote[] NumberEmotes = Enumerable
            .Range(0, 9)
            .Select(i => i.ToString() + CombiningEnclosingKeycap)
            .Select(e => new Emoji(e))
            .ToArray();
    }

    public static class Guilds
    {
        public const ulong Mihu             = 566925785563136020ul;
        public const ulong PrivateLogs      = 750706593858977802ul;
        public const ulong LiverGang        = 244642778024378368ul;
        public const ulong RetirementHome   = 357322948501045259ul;
    }

    public static class Channels
    {
        public const ulong Debug            = 806048964021190656ul;
        public const ulong LogText          = 750706839431413870ul;
        public const ulong Files            = 774147493319540736ul;
        public const ulong Email            = 880649603580559401ul;
        public const ulong TheBoysSpam      = 924503695738171402ul;
        public const ulong TheBoysWordle    = 928980369984860161ul;
    }

    public static class KnownUsers
    {
#if DEBUG
        public const ulong MihuBot      = 767172321999585281ul;
#else
        public const ulong MihuBot      = 710370560596770856ul;
#endif

        // Admins
        public const ulong Miha         = 162569877087977480ul;
        public const ulong James        = 91680709588045824ul;
        public const ulong Jordan       = 236455327535464458ul;

        public const ulong Christian    = 397254656025427968ul;
        public const ulong PaulK        = 267771172962304000ul;
        public const ulong Sticky       = 162050931326713857ul;
        public const ulong Joster       = 105498799706759168ul;
        public const ulong Ryboh        = 156253985207091202ul;
    }

    public static class Emotes
    {
        // Mihu
        public static Emote CreepyFace { get; }     = Emote.Parse("<:creepyface:708844396791201833>");
        public static Emote EyesShaking { get; }    = Emote.Parse("<a:eyesShaking:719904795091009636>");
        public static Emote James { get; }          = Emote.Parse("<:james:685587814330794040>");
        public static Emote KissAHomie { get; }     = Emote.Parse("<a:KissAHomie:769335184750805003>");
        public static Emote OmegaLUL { get; }       = Emote.Parse("<:OMEGALUL:775860675938353202>");
        public static Emote PepePoint { get; }      = Emote.Parse("<:pepePoint:701207439273361408>");
        public static Emote WeirdChamp { get; }     = Emote.Parse("<:WeirdChamp:715663367741898785>");

        // Liv
        public static Emote SenpaiLove { get; }     = Emote.Parse("<:senpaiLove:681560481214889999>");

        // Paul
        public static Emote KermitUwU { get; }      = Emote.Parse("<:KermitUwU:716355675457847336>");
        public static Emote MonkaStab { get; }      = Emote.Parse("<:monkaStab:715603083345789088>");


        public static readonly Emote[] JamesEmotes  = new Emote[]
        {
            Emote.Parse("<:james:685588058757791744>"),
            Emote.Parse("<:james:685588122939031569>"),
            Emote.Parse("<:james:694013377655209984>"),
            Emote.Parse("<:james:694013479622803476>"),
            Emote.Parse("<:james:694013490058362921>"),
            Emote.Parse("<:james:694013499377975356>"),
            Emote.Parse("<:james:694013521033297981>"),
            Emote.Parse("<:james:694013527660167229>"),
            Emote.Parse("<:james:694013534878826526>"),
            Emote.Parse("<:james:766226359306813440>"),
            Emote.Parse("<:james:766226402026061864>"),
            Emote.Parse("<:james:766226424007753728>"),
            Emote.Parse("<:james:766226439099777024>"),
            Emote.Parse("<:james:766226456992284692>"),
            Emote.Parse("<:james:767317111219159052>"),
            Emote.Parse("<:james:767317134379450368>"),
            Emote.Parse("<:james:767317154202517514>"),
            Emote.Parse("<:james:767317169901010944>"),
            Emote.Parse("<:james:767317186448326717>"),
        };

        public static IEmote ThumbsUp { get; }      = new Emoji("👍");
        public static IEmote RedCross { get; }      = new Emoji("❌");
        public static IEmote Heart { get; }         = new Emoji("❤️");
    }
}
