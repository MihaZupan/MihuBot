# Browser blackjack

Open `/blackjack`, sign in with Discord, and create a table. Share its invite
link with up to three other players. Anyone with the link can spectate; only
signed-in players can bet or act. The navigation menu shows **Blackjack** only
to signed-in users; invite links remain accessible to spectators.

In Discord, `!blackjack` / `!bj` posts a link to a shared browser lobby for
that channel. Repeated commands reuse the same table, including during play;
they do not place bets or change the game. A new six-deck lobby is created
if the previous one was closed or expired, owned by whoever requested it.
Normal per-owner and total table limits apply. To choose a different deck
count, create a table in the browser and share its link.

Gameplay is browser-only. Old Discord buttons and bet menus reply privately
with the browser lobby link instead of accepting moves. There are no Discord
game boards, image attachments, or separate channel chip balances.

At creation, choose four, six (default), or eight decks. The choice is fixed
for that table and applies to every shuffle. Other rules remain S17, DAS,
late surrender, four hands per seat, and one card to split aces.
Each table has its own shoe and 1,000 starting play chips per player.

Place a whole-chip bet from 10 to 500 to join. The first bet opens a 30-second
window, and the first seated player can deal early. Changing a bet preserves
seat order; leaving during betting refunds it. Players must opt in again for
each round. Free rebuys reset balances below 10 when no wager is active.

Turns run in seat order, with 30 seconds per move. Timeouts decline insurance
or stand on that player's remaining hands. Closing the browser does not cancel
a wager or stop the timers. Reopening the same link with the same Discord
account restores the current table and available actions.

The table uses animated dealing, a flipping dealer hole card, highlighted
turns, per-hand results, a countdown, and responsive seat layouts. Cards have
accessible names; reduced-motion preferences disable dealing/reveal effects.
The full rules are available below the game.

State is in memory and resets on server restart. Unviewed tables expire after
two hours once their active round finishes. There are at most 128 tables,
three owned tables per account, and 256 distinct players over a table's
lifetime. The card-room page links to your owned tables.

The current host can **Close table** and confirm to remove it immediately for
everyone, including any active round and all table-local chips. When the table
has no seats, its creator can close it. Closing frees an owned-table slot;
old links and pending actions no longer work.

On shorter desktop viewports, including at 125% zoom, the table uses a compact
layout. Seat columns respond to the available content width rather than the
window width. Expanded rules and unusually large split hands can still scroll
naturally; content is never clipped to hide scrollbars.

## Synchronization

`BrowserBlackjackService` owns the browser-only table instances. Mutations and
snapshot reads are serialized, and actions include a monotonically increasing
table version to reject double clicks and stale controls. Player identity comes
from the authenticated Discord claims, not a client-supplied player ID.

Blazor Server clients refresh their immutable snapshots every 500 ms; a hosted
timer advances unattended tables independently. Only visible card data crosses
the component boundary: the dealer hole card is represented by a rank-zero
placeholder, and no dealer total is included until settlement.

## Optional strategy coach

The **Strategy coach** checkbox is off by default and private to the browser
tab. It shows the Hi-Lo running count, true count, unseen decks, and a recommended
legal move on the viewer's turn. The ledger includes all publicly exposed cards
since the shuffle, even before the viewer joined. It never counts the dealer's
hole card before reveal. Splitting does not count the same card twice; shuffles
reset the ledger. True count divides the running count by unseen cards / 52,
including the hidden hole card.

Advice uses four-to-eight-deck S17/DAS/late-surrender basic strategy, with
Illustrious 18 and Fab 4 Hi-Lo index plays (floor the true count for comparison).
Insurance uses the exact fraction of unseen ten-valued cards: buy only when
it exceeds one third and the player can afford it. Other advice respects
available moves, split limits, split-ace restrictions, and uncommitted chips.

This is counting-strategy training advice, **not an exhaustive,
composition-dependent perfect-play solver** or a guarantee of winning. The
index set covers common deviations, not every possible composition exception.
The selectable deck counts are shoe games to match this strategy family.

Rule references:
- [Wizard of Odds: 4-8 deck basic strategy](https://wizardofodds.com/games/blackjack/strategy/4-decks/)
- [Wizard of Odds: Hi-Lo and index plays](https://wizardofodds.com/games/blackjack/card-counting/high-low/)
