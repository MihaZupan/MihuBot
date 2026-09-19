# Blackjack

Use `!blackjack [bet]` or `!bj [bet]` in a Discord text channel, then use the
buttons to hit, stand, double, split, surrender, or decide on insurance.
The default bet is 10; bets must be whole chips from 10 to 500.
Each channel has a four-seat table with one shared dealer hand and shoe.
The first bet opens a 30-second betting window. Other users join by choosing
a wager from the table's dropdown. Selecting another amount changes your
existing bet in place, keeping your seat order and host status. Increasing
the wager reserves only the difference; decreasing it returns the difference.
The same dropdown stays available at a full table so seated players can
adjust their bets. It does not extend the betting window.

The dropdown provides preset wagers from 10 to 500; `!bj [bet]` supports any
whole amount in that range and also changes an existing lobby bet.
Updates edit the existing board, and selection errors are shown privately
rather than posting channel messages. The first seated player is the host
and can **Deal** / `!bj deal` early, including when playing solo; otherwise the
round starts automatically. Seats are filled in join order.

`!bj leave` / **Leave / refund** returns a lobby wager before cards are dealt.
If the host leaves, the next seated player becomes host. Once play starts,
new players wait for the next betting window. After settlement, choosing
a wager from the dropdown opens a new lobby; players must opt in again rather than being charged
automatically.

`!bj rules` explains all table rules, `!bj table` refreshes the current/latest
round (including after a failed message update), and `!bj balance` shows your
channel balance. Players act in seat order, completing all their split hands
before the next player's turn. Only the highlighted player can act, and old
buttons cannot act on a newer turn or round. After two minutes without a move,
the bot declines that player's pending insurance or stands on their remaining
hands. The next player gets their own full turn; a timeout does not auto-play
everyone else.

The server renders separate, compact PNG panels for the dealer and occupied
seats, with large card ranks, names, totals, and a highlighted active hand.
Each player panel holds at most two hands; additional splits get a second
panel instead of shrinking the entire table to fit Discord's image preview.
There are no empty-seat panels. All panels stay in one message, with separate
image embeds rather than a thumbnail gallery (at most nine images).
The same channel message is edited across rounds, including when starting the
next betting window or using `!bj table`. Its embeds, controls, and attachments
are replaced together, with per-panel descriptions for text accessibility.
A new board is posted only when there is no tracked message (including after
bot restart) or Discord confirms that the old message was deleted. Other edit
failures are reported without posting duplicate boards. Edits suppress mentions.

## Table rules

- Six decks; dealer stands on soft 17 and peeks for blackjack before player actions.
- Natural blackjack pays 3:2; other wins pay 1:1; ties return the wager.
- Double on any initial two cards, including after a split, for exactly one more card.
- Split equal-value cards into up to four hands per player, played sequentially. Split aces
  receive one card each, with no resplitting or doubling. Split 21 pays only 1:1.
- Late surrender is available on the original two-card hand after the dealer's
  blackjack check; it returns half the wager.
- Insurance is offered against an ace before checking for blackjack, even if the
  player has blackjack. It costs half the original wager and pays 2:1.
  All players decide in seat order before the dealer checks the hole card;
  no player can learn the result before placing or declining insurance.
- Splits, doubles, and insurance require sufficient uncommitted chips.
- The dealer reveals the hole card at settlement, but does not draw unnecessarily
  when every player hand has busted, surrendered, or is a natural.
  The dealer plays once after all players finish; each hand is settled separately.

The shoe is shared by the channel and retained between rounds, with a cut card
at 75% penetration. The next round starts with a cryptographically shuffled
six-deck shoe once 234 or more cards have been dealt; a hand never reshuffles
mid-play. With multiple seats, a safety check can shuffle earlier if the
remaining cards cannot guarantee enough reserve for every seat's four splits
and the dealer. The board shows shoe number, remaining cards, and fresh shuffles,
but never shows the hole card or dealer total before settlement.

These player-friendly rules, meaningful penetration, and 1:50 betting spread
let advantage players use counting and strategy deviations in favorable shoes.
There is no artificial win-rate adjustment or guarantee of profit; ordinary
basic strategy still leaves a small house edge. No running count is supplied.

## Chips and lifetime

Players start with 1,000 chips per channel. `!bj rebuy` resets a balance
below 10 to 1,000, provided that player has no active hand. Balances, active hands, and shoes are
in-memory only and reset on bot restart. Blackjack needs Send Messages,
Embed Links, and Attach Files permissions.

## Rendering assets

Card art is drawn locally with SkiaSharp; no external image service, avatars,
system fonts, or downloaded card images are needed at runtime. Native assets
are packaged for Windows and Linux (including the no-dependencies Linux build).
The bundled Atkinson Hyperlegible fonts are copyright 2020 Braille Institute
of America, Inc., and distributed under the SIL Open Font License in
`Fonts/OFL.txt`. Unmodified font files come from `google/fonts` revision
`95f4904fc8bcf26d3420fe315560c96417c6dec7`, directory `ofl/atkinsonhyperlegible`.
