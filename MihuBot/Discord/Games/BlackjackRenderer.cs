using System.Globalization;
using SkiaSharp;

namespace MihuBot.Discord.Games;

internal static class BlackjackRenderer
{
    public const int Width = 800;
    public const int CardRankSize = 32;
    public const int HandTextSize = 28;
    public const int HandsPerPanel = 2;
    public const int MaxPanelHeight = 420;

    internal sealed record PanelImage(int SeatIndex, int FirstHandIndex, int HandCount, byte[] Png);

    private static readonly SKColor s_gold = SKColor.Parse("#EAC780");
    private static readonly SKColor s_cream = SKColor.Parse("#F9F4E9");
    private static readonly SKColor s_muted = SKColor.Parse("#A8C5B8");
    private static readonly SKColor s_ink = SKColor.Parse("#152C31");
    private static readonly SKColor s_red = SKColor.Parse("#C5394D");
    private static readonly SKColor s_loss = SKColor.Parse("#FF9AA5");
    private static readonly SKColor s_green = SKColor.Parse("#86E3B1");
    private static readonly SKTypeface s_regular = LoadFont("Regular");
    private static readonly SKTypeface s_bold = LoadFont("Bold");

    public static PanelImage[] Render(BlackjackTable table)
    {
        var panels = new List<PanelImage> { new(-1, 0, 0, RenderPanel(table, -1, 0, 0)) };

        for (int seat = 0; seat < table.Seats.Count; seat++)
        {
            for (int firstHand = 0; firstHand < table.Seats[seat].Hands.Count; firstHand += HandsPerPanel)
            {
                int count = Math.Min(HandsPerPanel, table.Seats[seat].Hands.Count - firstHand);
                panels.Add(new(seat, firstHand, count, RenderPanel(table, seat, firstHand, count)));
            }
        }

        return panels.ToArray();
    }

    private static byte[] RenderPanel(BlackjackTable table, int seat, int firstHand, int handCount)
    {
        // Separate, height-bounded embeds avoid Discord shrinking a tall full-table image.
        int height = seat < 0 ? 240 : 124 + (148 * handCount);
        using var bitmap = new SKBitmap(Width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var background = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(new SKPoint(400, 120), Width,
                [SKColor.Parse("#1B604C"), SKColor.Parse("#081E23")], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, Width, height, background);

        bool active = seat >= 0 && table.Game is { IsComplete: false } game &&
            game.ActivePlayerIndex == seat && game.ActivePlayer.ActiveHandIndex >= firstHand &&
            game.ActivePlayer.ActiveHandIndex < firstHand + handCount;
        using var border = new SKPaint
        {
            Color = active ? s_gold : s_gold.WithAlpha(90),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = active ? 4 : 2,
            IsAntialias = true
        };
        canvas.DrawRoundRect(new SKRect(8, 8, Width - 8, height - 8), 18, 18, border);

        if (seat < 0)
        {
            DrawDealer(canvas, table);
        }
        else
        {
            DrawSeat(canvas, table, seat, firstHand, handCount, height, active);
        }

        using SKData image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return image.ToArray();
    }

    private static void DrawDealer(SKCanvas canvas, BlackjackTable table)
    {
        Text(canvas, "DEALER", 24, 47, 36, s_cream, bold: true);
        BlackjackGame game = table.Game;

        // Build the visible hand without ever passing the hole card to the card renderer.
        BlackjackCard?[] cards = game is null ? [null, null] : game.IsComplete
            ? game.Dealer.Select(c => (BlackjackCard?)c).ToArray()
            : [game.Dealer[0], null];
        DrawCards(canvas, cards, 24, 74, 752);
        string total = game is null ? table.IsLobby ? "BETTING OPEN" : "TAKE A SEAT"
            : game.IsComplete ? Total(game.Dealer) : "HOLE CARD HIDDEN";
        Pill(canvas, total, 470, 20, 306, s_ink, s_cream);
        Text(canvas, "3:2 / S17 / DAS / LS", 24, 218, 24, s_gold, bold: true);
        string shoe = $"Shoe {table.Shoe.Number} / {table.Shoe.Remaining} cards{(table.Shuffled ? " / Shuffled" : "")}";
        Text(canvas, shoe, 776, 218, 24, s_muted, SKTextAlign.Right, maxWidth: 454);
    }

    private static void DrawSeat(SKCanvas canvas, BlackjackTable table, int index, int firstHand, int handCount, int height, bool active)
    {
        BlackjackPlayer player = table.Seats[index];
        Text(canvas, $"{index + 1} / {player.Name}", 24, 47, 36, s_cream, bold: true, maxWidth: 510);
        string seatStatus = active ? table.Game.OfferingInsurance ? "INSURANCE" : "YOUR TURN"
            : table.IsLobby ? "BET PLACED"
            : table.Game.IsComplete ? "SETTLED"
            : player.Hands.All(h => h.Finished) ? "DONE" : "WAITING";
        Text(canvas, seatStatus, 776, 45, 26, active ? s_gold : s_muted, SKTextAlign.Right, bold: true);

        for (int h = firstHand; h < firstHand + handCount; h++)
        {
            BlackjackHand hand = player.Hands[h];
            float y = 84 + ((h - firstHand) * 148);
            bool current = active && h == player.ActiveHandIndex && !table.Game.OfferingInsurance;
            Text(canvas, $"Hand {h + 1} / Bet {Chips(hand.Bet)}", 24, y, HandTextSize, current ? s_gold : s_muted, bold: true);

            if (table.IsLobby)
            {
                DrawCards(canvas, [null, null], 24, y + 16, 608);
                Text(canvas, "Waiting for the deal", 234, y + 80, HandTextSize, s_muted);
            }
            else
            {
                string result = table.Game.IsComplete ? $"{hand.Result.ToUpperInvariant()}  {SignedChips(hand.Returned - hand.Bet)}"
                    : hand.Value.Total > 21 ? "BUST"
                    : hand.Surrendered ? "SURRENDER"
                    : hand.IsBlackjack ? "BLACKJACK"
                    : hand.Finished ? "STAND" : current ? "PLAYING" : "WAITING";
                SKColor resultColor = table.Game.IsComplete ? hand.Returned > hand.Bet ? s_green : hand.Returned < hand.Bet ? s_loss : s_muted
                    : current ? s_gold : s_muted;
                Text(canvas, result, 776, y, HandTextSize, resultColor, SKTextAlign.Right, bold: true, maxWidth: 400);
                DrawCards(canvas, hand.Cards.Select(c => (BlackjackCard?)c).ToArray(), 24, y + 16, 608);
                Pill(canvas, Total(hand.Cards), 644, y + 48, 132, current ? s_gold : s_ink, current ? s_ink : s_cream);
            }
        }

        float bottom = height - 23;
        Chip(canvas, 40, height - 33, s_gold);
        Text(canvas, $"Chips {Chips(player.Balance)}", 72, bottom, 26, s_cream, bold: true, maxWidth: 265);

        if (table.Game?.IsComplete == true)
        {
            decimal net = player.Balance - player.StartingBalance;
            Text(canvas, $"Net {SignedChips(net)}", 776, bottom, 28,
                net > 0 ? s_green : net < 0 ? s_loss : s_muted, SKTextAlign.Right, bold: true);

            if (player.InsuranceBet > 0)
            {
                Text(canvas, $"Ins. {SignedChips(player.InsuranceReturned - player.InsuranceBet)}",
                    430, bottom, 26, s_muted, SKTextAlign.Center);
            }
        }
        else
        {
            Text(canvas, player.InsuranceBet > 0 ? $"Insurance {Chips(player.InsuranceBet)}" : $"Wagered {Chips(player.Hands.Sum(h => h.Bet))}",
                776, bottom, 26, s_gold, SKTextAlign.Right);
        }
    }

    private static void DrawCards(SKCanvas canvas, IReadOnlyList<BlackjackCard?> cards, float x, float y, float width)
    {
        float step = Math.Min(84, (width - 76) / Math.Max(1, cards.Count - 1));

        for (int i = 0; i < cards.Count; i++)
        {
            DrawCard(canvas, cards[i], x + (i * step), y, 76, 108);
        }
    }

    private static void DrawCard(SKCanvas canvas, BlackjackCard? card, float x, float y, float width, float height)
    {
        using var paint = new SKPaint { Color = SKColors.Black.WithAlpha(60), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(x + 2, y + 4, x + width + 2, y + height + 4), 7, 7, paint);
        paint.Color = card.HasValue ? s_cream : SKColor.Parse("#203C61");
        canvas.DrawRoundRect(new SKRect(x, y, x + width, y + height), 7, 7, paint);

        if (card is not { } face)
        {
            paint.Color = s_gold.WithAlpha(95);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            canvas.DrawRoundRect(new SKRect(x + 5, y + 5, x + width - 5, y + height - 5), 4, 4, paint);

            for (int i = 0; i < 5; i++)
            {
                float inset = 12 + (i * 4);
                canvas.DrawLine(x + inset, y + 12, x + width - 12, y + height - inset, paint);
                canvas.DrawLine(x + 12, y + inset, x + width - inset, y + height - 12, paint);
            }

            DrawSuit(canvas, 1, x + (width / 2), y + (height / 2), 12, s_gold);
            return;
        }

        SKColor color = face.Suit is 1 or 2 ? s_red : s_ink;
        string rank = face.Rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => face.Rank.ToString(CultureInfo.InvariantCulture) };
        Text(canvas, rank, x + 7, y + 32, CardRankSize, color, bold: true);
        DrawSuit(canvas, face.Suit, x + 17, y + 48, 9, color);
        DrawSuit(canvas, face.Suit, x + (width / 2) + 8, y + (height * 0.62f), width * 0.20f, color);
        Text(canvas, rank, x + width - 7, y + height - 7, 22, color, SKTextAlign.Right, bold: true);
    }

    private static void DrawSuit(SKCanvas canvas, int suit, float x, float y, float size, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        using var path = new SKPathBuilder();
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(size);

        switch (suit)
        {
            case 0:
                canvas.DrawCircle(0, -0.48f, 0.52f, paint);
                canvas.DrawCircle(-0.48f, 0.18f, 0.52f, paint);
                canvas.DrawCircle(0.48f, 0.18f, 0.52f, paint);
                break;

            case 1:
                path.MoveTo(0, -1);
                path.LineTo(0.75f, 0);
                path.LineTo(0, 1);
                path.LineTo(-0.75f, 0);
                path.Close();

                using (SKPath diamond = path.Detach())
                {
                    canvas.DrawPath(diamond, paint);
                }

                break;

            case 2:
            case 3:
                canvas.Save();

                if (suit == 3)
                {
                    canvas.RotateDegrees(180);
                }

                path.MoveTo(0, 0.9f);
                path.CubicTo(-2, -0.4f, -0.55f, -1.5f, 0, -0.55f);
                path.CubicTo(0.55f, -1.5f, 2, -0.4f, 0, 0.9f);
                path.Close();

                using (SKPath heart = path.Detach())
                {
                    canvas.DrawPath(heart, paint);
                }

                canvas.Restore();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(suit));
        }

        if (suit is 0 or 3)
        {
            using var stem = new SKPathBuilder();
            stem.MoveTo(0, 0.15f);
            stem.LineTo(0.38f, 1);
            stem.LineTo(-0.38f, 1);
            stem.Close();
            using SKPath stemPath = stem.Detach();
            canvas.DrawPath(stemPath, paint);
        }

        canvas.Restore();
    }

    private static void Chip(SKCanvas canvas, float x, float y, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawCircle(x, y + 4, 21, paint);
        paint.Color = s_cream;
        canvas.DrawCircle(x, y, 21, paint);
        paint.Color = color;
        canvas.DrawCircle(x, y, 17, paint);
        paint.Color = s_cream;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        canvas.DrawCircle(x, y, 12, paint);

        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            paint.StrokeWidth = 4;
            canvas.DrawLine(x + ((float)Math.Cos(angle) * 17), y + ((float)Math.Sin(angle) * 17),
                x + ((float)Math.Cos(angle) * 21), y + ((float)Math.Sin(angle) * 21), paint);
        }
    }

    private static void Panel(SKCanvas canvas, SKRect rect, SKColor fill, SKColor outline, float strokeWidth = 1)
    {
        using var paint = new SKPaint { Color = fill, IsAntialias = true };
        canvas.DrawRoundRect(rect, 16, 16, paint);
        paint.Color = outline;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = strokeWidth;
        canvas.DrawRoundRect(rect, 16, 16, paint);
    }

    private static void Pill(SKCanvas canvas, string text, float x, float y, float width, SKColor fill, SKColor textColor)
    {
        Panel(canvas, new SKRect(x, y, x + width, y + 44), fill, fill);
        Text(canvas, text, x + (width / 2), y + 32, 28, textColor, SKTextAlign.Center, bold: true, maxWidth: width - 12);
    }

    private static void Text(SKCanvas canvas, string text, float x, float y, float size, SKColor color,
        SKTextAlign align = SKTextAlign.Left, bool bold = false, float maxWidth = float.MaxValue)
    {
        using var font = new SKFont(bold ? s_bold : s_regular, size);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        text = string.Concat(text.EnumerateRunes().Take(80).Select(r => Rune.IsControl(r) ? " " : r.ToString()));

        if (font.MeasureText(text) > maxWidth)
        {
            var runes = text.EnumerateRunes().ToList();

            while (runes.Count > 0 && font.MeasureText(string.Concat(runes) + "...") > maxWidth)
            {
                runes.RemoveAt(runes.Count - 1);
            }

            text = string.Concat(runes) + "...";
        }

        canvas.DrawText(text, x, y, align, font, paint);
    }

    internal static string Chips(decimal amount) => amount.ToString("0.##", CultureInfo.InvariantCulture);
    internal static string SignedChips(decimal amount) => (amount > 0 ? "+" : "") + Chips(amount);

    private static string Total(IEnumerable<BlackjackCard> cards)
    {
        (int total, bool soft) = BlackjackHand.Evaluate(cards);
        return $"{total}{(soft ? " SOFT" : "")}";
    }

    private static SKTypeface LoadFont(string weight)
    {
        using Stream stream = typeof(BlackjackRenderer).Assembly.GetManifestResourceStream(
            $"MihuBot.Discord.Games.Fonts.AtkinsonHyperlegible-{weight}.ttf")
            ?? throw new InvalidOperationException($"Missing bundled Blackjack font: {weight}");
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"Invalid Blackjack font: {weight}");
    }
}
