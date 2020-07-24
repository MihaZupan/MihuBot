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
            KnownUsers.Liv,
            KnownUsers.DreisBlue,
        };

        public static readonly Dictionary<ulong, HashSet<ulong>> GuildMods = new Dictionary<ulong, HashSet<ulong>>
        {
            {
                Guilds.DDs,
                new HashSet<ulong> { KnownUsers.Angelo, }
            },
            {
                Guilds.Paul,
                new HashSet<ulong> { KnownUsers.PaulK, }
            },
            {
                Guilds.LiverGang,
                new HashSet<ulong> { KnownUsers.Angelo, }
            },
            {
                Guilds.Arisas,
                new HashSet<ulong> { KnownUsers.Arisa, }
            },
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
        public const ulong DDs          = 350658308878630914ul;
        public const ulong Mihu         = 566925785563136020ul;
        public const ulong Paul         = 715374946846769202ul;
        public const ulong LiverGang    = 244642778024378368ul;
        public const ulong DresDreamers = 495391858009309184ul;
        public const ulong Arisas       = 417794764029820939ul;
        public const ulong Brandons     = 478637384347549713ul;
    }

    public static class KnownUsers
    {
        public const ulong MihuBot      = 710370560596770856ul;

        // Admins
        public const ulong Miha         = 162569877087977480ul;
        public const ulong Caroline     = 340562834658295821ul;
        public const ulong James        = 91680709588045824ul;
        public const ulong Jaeger       = 145719024544645120ul;
        public const ulong Jordan       = 236455327535464458ul;
        public const ulong Doomi        = 238754130233917440ul;
        public const ulong Liv          = 244637014547234816ul;
        public const ulong DreisBlue    = 134162712191172608ul;

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
    }

    public static class Emotes
    {
        public static readonly Emote YesW           = Emote.Parse("<:yesW:726548253059055759>");
        public static readonly Emote PudeesJammies  = Emote.Parse("<:pudeesJammies:686340394866573338>");
        public static readonly Emote DarlBoop       = Emote.Parse("<:darlBoop:712494064087597106>");
        public static readonly Emote DarlHearts     = Emote.Parse("<a:darlHearts:712496083334463528>");
        public static readonly Emote DarlHuggers    = Emote.Parse("<:darlHUGERS:725199118619902006>");
        public static readonly Emote DarlKiss       = Emote.Parse("<:darlKiss:712494206308057248>");
        public static readonly Emote DarlBASS       = Emote.Parse("<a:darlBASS:560235665040867328>");
        public static readonly Emote CreepyFace     = Emote.Parse("<:creepyface:708818227446284369>");
        public static readonly Emote MonkaHmm       = Emote.Parse("<:monkaHmm:712494625390198856>");
        public static readonly Emote Monkers        = Emote.Parse("<:MONKERS:715472497499176981>");
        public static readonly Emote MonkaEZ        = Emote.Parse("<:EZ:712494500731158549>");
        public static readonly Emote DarlPoke       = Emote.Parse("<:darlPoke:591174254372978689>");
        public static readonly Emote DarlZoom       = Emote.Parse("<a:darlZoom:574377475115581440>");
        public static readonly Emote DarlF          = Emote.Parse("<:darlF:629944838866993153>");
        public static readonly Emote SenpaiLove     = Emote.Parse("<:senpaiLove:681560481214889999>");
        public static readonly Emote PauseChamp     = Emote.Parse("<:PauseChamp:724041635821781002>");
        public static readonly Emote PepePoint      = Emote.Parse("<:pepePoint:701207439273361408>");


        public static readonly IEmote ThumbsUp      = new Emoji("👍");

        public static readonly Emote[] JamesEmotes = new Emote[]
        {
            Emote.Parse("<:james:685588058757791744>"),
            Emote.Parse("<:james:685588122939031569>"),
            Emote.Parse("<:james:694013377655209984>"),
            Emote.Parse("<:james:694013479622803476>"),
            Emote.Parse("<:james:694013490058362921>"),
            Emote.Parse("<:james:694013499377975356>"),
            Emote.Parse("<:james:694013521033297981>"),
            Emote.Parse("<:james:694013527660167229>"),
            Emote.Parse("<:james:694013534878826526>")
        };
    }
}
