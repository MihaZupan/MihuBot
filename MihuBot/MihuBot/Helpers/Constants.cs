namespace MihuBot.Helpers
{
    public static class Constants
    {
        public static string StateDirectory => "State";

        public static readonly HashSet<ulong> GuildIDs = new(typeof(Guilds).GetFields().Select(f => (ulong)f.GetRawConstantValue()));
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
        public const ulong DDs              = 350658308878630914ul;
        public const ulong Paul             = 715374946846769202ul;
        public const ulong LiverGang        = 244642778024378368ul;
        public const ulong DresDreamers     = 495391858009309184ul;
        public const ulong Arisas           = 417794764029820939ul;
        public const ulong Brandons         = 478637384347549713ul;
        public const ulong Dreamlings       = 612032458258644992ul;
        public const ulong TheWaddle        = 509232675001991169ul;
        public const ulong TheFragmentary   = 489465972772831233ul;
        public const ulong RetirementHome   = 357322948501045259ul;
        public const ulong DevBotOnlyTest   = 811117737866166312ul;
        public const ulong ComfyCove        = 841908886583246858ul;
    }

    public static class Channels
    {
        public const ulong DDsGeneral       = 353640904185085953ul;
        public const ulong DDsIntroductions = 374585088404488202ul;
        public const ulong DDsPancake       = 836483780965302292ul;

        public const ulong Debug            = 806048964021190656ul;
        public const ulong LogText          = 750706839431413870ul;
        public const ulong Files            = 774147493319540736ul;
        public const ulong Email            = 880649603580559401ul;

        public const ulong BirthdaysLog     = 752617146172964875ul;
        public const ulong TwitchAddLogs    = 769854674881740820ul;
        public const ulong DuelsTexts       = 780506349414121503ul;

        public const ulong RetirementHomeWhitelist = 782770488233558016ul;
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
        public const ulong Caroline     = 340562834658295821ul;
        public const ulong James        = 91680709588045824ul;
        public const ulong Jordan       = 236455327535464458ul;
        public const ulong Darling      = 399843400041365516ul;

        public const ulong CurtIs       = 237788815626862593ul;
        public const ulong Christian    = 397254656025427968ul;
        public const ulong Gradravin    = 235218831247671297ul;
        public const ulong PaulK        = 267771172962304000ul;
        public const ulong Sticky       = 162050931326713857ul;
        public const ulong Joster       = 105498799706759168ul;
        public const ulong Richard      = 136981443497689088ul;
        public const ulong Adi          = 360977915975958537ul;
        public const ulong Ryboh        = 156253985207091202ul;
        public const ulong Moo          = 409919984207265793ul;
    }

    public static class Emotes
    {
        // TODO
        // DarlClown, DarlUwU, DarlHug

        // DDs
        public static Emote DarlFighting { get; }   = Emote.Parse("<a:darlFighting:806975528942567484>");
        public static Emote DarlHearts { get; }     = Emote.Parse("<a:darlHearts:819656027367669791>");
        public static Emote DarlShyShy { get; }     = Emote.Parse("<a:darlShyshy:807001178582810674>");
        public static Emote DarlZoom { get; }       = Emote.Parse("<a:darlsipzoom:646124538312261651>");
        public static Emote MonkaHmm { get; }       = Emote.Parse("<:monkaHmm:758118234284490792>");
        public static Emote PudeesJammies { get; }  = Emote.Parse("<a:pudeesJammies:686340394866573338>");
        public static Emote WeeHypers { get; }      = Emote.Parse("<a:WeeHypers:758118343210434603>");
        public static Emote YesW { get; }           = Emote.Parse("<:yesW:726548253059055759>");

        // Mihu
        public static Emote CreepyFace { get; }     = Emote.Parse("<:creepyface:708844396791201833>");
        public static Emote EyesShaking { get; }    = Emote.Parse("<a:eyesShaking:719904795091009636>");
        public static Emote James { get; }          = Emote.Parse("<:james:685587814330794040>");
        public static Emote KissAHomie { get; }     = Emote.Parse("<a:KissAHomie:769335184750805003>");
        public static Emote OmegaLUL { get; }       = Emote.Parse("<:OMEGALUL:775860675938353202>");
        public static Emote PauseChamp { get; }     = Emote.Parse("<:PauseChamp:724041635821781002>");
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
        public static IEmote Checkmark { get; }     = new Emoji("✅");
        public static IEmote RedCross { get; }      = new Emoji("❌");
        public static IEmote QuestionMark { get; }  = new Emoji("❓");
    }
}
