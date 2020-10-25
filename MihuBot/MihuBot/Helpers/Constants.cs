using Discord;
using System.Collections.Generic;
using System.Linq;

namespace MihuBot.Helpers
{
    public static class Constants
    {
        public static readonly HashSet<ulong> GuildIDs = new HashSet<ulong>(typeof(Guilds).GetFields().Select(f => (ulong)f.GetRawConstantValue()));
        public static readonly HashSet<ulong> Admins = new HashSet<ulong>()
        {
            KnownUsers.Miha,
            KnownUsers.Caroline,
            KnownUsers.James,
            KnownUsers.Jaeger,
            KnownUsers.Jordan,
            KnownUsers.Doomi,
            KnownUsers.Darling,
        };

        public const string VCDefaultVolume = "0.25";

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
        public const ulong Mihu         = 566925785563136020ul;
        public const ulong PrivateLogs  = 750706593858977802ul;
        public const ulong DDs          = 350658308878630914ul;
        public const ulong Paul         = 715374946846769202ul;
        public const ulong LiverGang    = 244642778024378368ul;
        public const ulong DresDreamers = 495391858009309184ul;
        public const ulong Arisas       = 417794764029820939ul;
        public const ulong Brandons     = 478637384347549713ul;
        public const ulong Dreamlings   = 612032458258644992ul;
        public const ulong TheWaddle    = 509232675001991169ul;
    }

    public static class Channels
    {
        public const ulong DDsGeneral       = 353640904185085953ul;
        public const ulong DDsIntroductions = 374585088404488202ul;

        public const ulong Debug            = 719903263297896538ul;
        public const ulong LogText          = 750706839431413870ul;
        public const ulong LogReports       = 750736528661020781ul;

        public const ulong BirthdaysLog     = 752617146172964875ul;
        public const ulong TwitchAddLogs    = 769854674881740820ul;
    }

    public static class KnownUsers
    {
        public const ulong MihuBot      = Secrets.Discord.BotId;

        // Admins
        public const ulong Miha         = 162569877087977480ul;
        public const ulong Caroline     = 340562834658295821ul;
        public const ulong James        = 91680709588045824ul;
        public const ulong Jaeger       = 145719024544645120ul;
        public const ulong Jordan       = 236455327535464458ul;
        public const ulong Doomi        = 238754130233917440ul;
        public const ulong Darling      = 399843400041365516ul;

        public const ulong Liv          = 244637014547234816ul;
        public const ulong CurtIs       = 237788815626862593ul;
        public const ulong Christian    = 397254656025427968ul;
        public const ulong Arisa        = 417794401721384960ul;
        public const ulong Angelo       = 326082313257484288ul;
        public const ulong Gradravin    = 235218831247671297ul;
        public const ulong PaulK        = 267771172962304000ul;
        public const ulong Maric        = 399032007138476032ul;
        public const ulong Conor        = 533609249394130947ul;
        public const ulong Sticky       = 162050931326713857ul;
        public const ulong Sfae         = 224015634206425088ul;
        public const ulong Ai           = 359549469680730114ul;
        public const ulong Brandon      = 138790549661417473ul;
        public const ulong Joster       = 105498799706759168ul;
        public const ulong Awescar      = 228363521077805068ul;
    }

    public static class Emotes
    {
        // TODO
        // DarlClown, DarlUwU, DarlHug

        // DDs
        public static Emote DarlBASS { get; }       = Emote.Parse("<a:darlBASS:726548569833603104>");
        public static Emote DarlBoop { get; }       = Emote.Parse("<:dbp:767763877697945630>");
        public static Emote DarlFighting { get; }   = Emote.Parse("<a:darlFighting:758118026523967529>");
        public static Emote DarlHearts { get; }     = Emote.Parse("<a:darlHearts:726548703888015401>");
        public static Emote DarlKiss { get; }       = Emote.Parse("<:darlKiss:758117833275342848>");
        public static Emote DarlPatPat { get; }     = Emote.Parse("<a:darlPatPat:760596696090279937>");
        public static Emote DarlShyShy { get; }     = Emote.Parse("<a:darlshyshy:758846620765913088>");
        public static Emote DarlZoom { get; }       = Emote.Parse("<a:darlZoom:574377475115581440>");
        public static Emote MonkaHmm { get; }       = Emote.Parse("<:monkaHmm:758118234284490792>");
        public static Emote PudeesJammies { get; }  = Emote.Parse("<a:pudeesJammies:686340394866573338>");
        public static Emote WeeHypers { get; }      = Emote.Parse("<a:WeeHypers:758118343210434603>");
        public static Emote YesW { get; }           = Emote.Parse("<:yesW:726548253059055759>");

        // Mihu
        public static Emote CreepyFace { get; }     = Emote.Parse("<:creepyface:708818227446284369>");
        public static Emote EyesShaking { get; }    = Emote.Parse("<a:eyesShaking:719904795091009636>");
        public static Emote James { get; }          = Emote.Parse("<:james:685587814330794040>");
        public static Emote KissAHomie { get; }     = Emote.Parse("<a:KissAHomie:769335184750805003>");
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
    }
}
