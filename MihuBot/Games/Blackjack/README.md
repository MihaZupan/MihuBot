# Browser blackjack

Open `/blackjack`, sign in with Discord, and create a table. Share its invite
link with up to five other players. Anyone with the link can spectate; only
signed-in players can bet or act. The navigation menu shows **Blackjack** only
to signed-in users; invite links remain accessible to spectators.

In Discord, `!blackjack` / `!bj` posts a preview-free `<url>` link to a shared browser lobby for
that channel. Repeated commands reuse the same table, including during play;
they do not place bets or change the game. A new four-deck lobby is created
if the previous one was closed or expired, owned by whoever requested it.
Normal per-owner and total table limits apply. To choose a different deck
count, create a table in the browser and share its link.

Gameplay is browser-only. Old Discord buttons and bet menus reply privately
with the browser lobby link instead of accepting moves. There are no Discord
game boards or image attachments.

At creation, choose two, four (default), six, or eight decks. The choice is fixed
for that table and applies to every shuffle. Other rules remain S17, DAS,
late surrender, and one card to split aces. Two-deck tables allow up to three
hands per player; larger shoes allow four. This keeps all six seats safe from
running out of cards without shuffling mid-hand. The safety reserve can cause
earlier shuffles, especially at a full two-deck table.
Each table has its own shoe. Every Discord account starts with 1,000 play chips
and shares one persistent balance across all tables.

Place a whole-chip bet from 10 to 500 to join. The first bet opens a 30-second
window, and the first seated player can deal early. Changing a bet preserves
seat order; leaving during betting refunds it. Players must opt in again for
each round. Free rebuys reset balances below 10 when no wager is active at any table.

Up to six players act simultaneously, each with an independent 30-second
timer per move. A player's own split hands remain sequential. Timeouts decline
insurance or stand on only that player's remaining hands; another player's
move neither resets their timer nor invalidates their controls. Closing the browser does not cancel
a wager or stop the timers. Reopening the same link with the same Discord
account restores the current table and available actions.

Insurance is a shared decision phase: everyone may decide in parallel, but
no one can play their main hand until every player has answered or timed out.
The dealer then checks for blackjack, and all unfinished players get a fresh
30 seconds. The dealer draws and settles exactly once, after all players finish.

The table uses animated dealing, a flipping dealer hole card, highlighted
turns, per-hand results, a countdown, and responsive seat layouts. Cards have
accessible names; reduced-motion preferences disable dealing/reveal effects.
The full rules are available below the game.

Tables and shoes are in memory and reset on server restart; account balances
are saved. Unviewed tables expire after
two hours once their active round finishes. There are at most 128 tables,
three owned tables per account, and 256 distinct players over a table's
lifetime. The card-room page links to your owned tables.

The current host can **Close table** and confirm to remove it for everyone.
Lobby wagers are refunded. A live round first declines pending insurance,
stands unfinished hands, and saves the settled results. Closing never deletes
account balances or lets a host cancel a losing hand. When the table
has no seats, its creator can close it. Closing frees an owned-table slot;
old links and pending actions no longer work.

## Saved balances

`State/BlackjackBalances.json` is a plain dictionary mapping Discord user IDs to
settled balances in integer half chips, including payouts and free rebuys.
Back it up with the other `State` files. Older builds never saved their per-table
balances, so those cannot be imported automatically.

Outstanding wagers are reserved in memory across every table. Only completed
rounds change the saved score; each round's results are written together.
After a restart, abandoned rounds disappear and their reserved chips become
available again. Closing or expiring an idle table does not reset a score.

The shared `SynchronizedLocalJsonStore` helper modifies balances in place under
its lock, flushes a temporary file, and atomically replaces the balance file.
Failed saves leave the updated balances in memory; there is no rollback.
A leftover `.tmp` is not used as a
recovered score. The store assumes one bot instance; the service lock and
JSON helper's semaphore protect in-process access. An unreadable or invalid
balance file fails startup instead of silently resetting accounts.

If saving a result fails, that table stays blocked with an explicit error
while saves retry without applying the round again. Settled wagers are released,
so other tables can use the updated balance. A failed rebuy also keeps its
in-memory balance and reports the save error. The next successful balance save
persists all in-memory changes; unsaved changes may be lost on restart.

On shorter desktop viewports, including at 125% zoom, the table uses a compact
layout. Seat columns respond to the available content width rather than the
window width. Expanded rules and unusually large split hands can still scroll
naturally; content is never clipped to hide scrollbars.

## Synchronization

`BrowserBlackjackService` owns the browser-only table instances. Mutations and
snapshot reads are serialized, including draws from the shared shoe. Each
player's controls carry their own revision, so concurrent moves from different
players are accepted while duplicate moves from the same player are rejected.
Dealing, the insurance-to-play transition, and settlement invalidate all
players' older revisions. Player identity comes
from the authenticated Discord claims, not a client-supplied player ID.

Blazor Server clients refresh their immutable snapshots every 500 ms; a hosted
timer advances unattended tables independently. Only visible card data crosses
the component boundary: the dealer hole card is represented by a rank-zero
placeholder, and no dealer total is included until settlement.

## Optional strategy coach

The **Strategy coach** checkbox is off by default and private to the browser
tab. It shows the Hi-Lo running count, true count, unseen decks, and a recommended
legal move for the viewer's pending hand, with its explanation always visible
alongside the suggestion. The ledger includes all publicly exposed cards
since the shuffle, even before the viewer joined. It never counts the dealer's
hole card before reveal. Splitting does not count the same card twice; shuffles
reset the ledger. True count divides the running count by unseen cards / 52,
including the hidden hole card.

Advice refreshes as any seat exposes cards. Another player's move can change
the recommendation without changing your hand or invalidating your controls.

Advice uses deck-specific S17/DAS/late-surrender basic strategy. Four-to-eight-deck tables add
Illustrious 18 and Fab 4 Hi-Lo index plays (floor the true count for comparison).
Insurance uses the exact fraction of unseen ten-valued cards: buy only when
it exceeds one third and the player can afford it. Other advice respects
available moves, split limits, split-ace restrictions, and uncommitted chips.

This is counting-strategy training advice, **not an exhaustive,
composition-dependent perfect-play solver** or a guarantee of winning. The
index set covers common deviations, not every possible composition exception.
Two-deck tables use their own basic strategy and exact insurance odds; the
multi-deck index set is not applied to them.

Rule references:
- [Wizard of Odds: double-deck basic strategy](https://wizardofodds.com/games/blackjack/strategy/2-decks/)
- [Wizard of Odds: 4-8 deck basic strategy](https://wizardofodds.com/games/blackjack/strategy/4-decks/)
- [Wizard of Odds: Hi-Lo and index plays](https://wizardofodds.com/games/blackjack/card-counting/high-low/)
