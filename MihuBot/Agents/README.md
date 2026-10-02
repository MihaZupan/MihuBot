# Discord agent

`agent <task>` (or `!agent <task>`) runs a fresh, full Copilot runtime on the bot
machine. It can use the web, shell, Python, and a `read_channel_history` tool that
reads up to 1000 messages at a time from the invoking channel's logs DB, including
edits, deletion markers, and attachment metadata, like `tldr`. History ends before
the request message and can be paginated by message ID. Command authorization
is sufficient; channel-history permissions are not checked again. There is no
conversation history between invocations.

`search_channel_messages` combines optional author ID, literal text, inclusive
start/exclusive end dates, attachment presence, and deleted-message filters.
Filters run in the logs DB before the 1-1000 result limit. Text matches the latest
logged content, not an older edit, using SQLite's ASCII-case-insensitive literal
substring matching (`%` and `_` are not wildcards). Results are chronological;
use the oldest returned message ID as the exclusive cursor for the next page.
Searches remain scoped to the invoking channel and precede the agent request.

`find_discord_users` resolves usernames, display names, nicknames, IDs, or mentions
against the current server's member cache, returning up to 100 candidates with
exact matches first. Returned IDs can be used with the author filter. Former
members absent from the cache must be identified from history or by a known ID.

The `upload_file` tool sends generated workspace files to the invoking channel
before cleanup. It accepts relative or absolute workspace paths, rejects paths
outside the workspace and symbolic links, and checks the server's upload limit.
Instructions prohibit remote/shared-state changes such as git pushes, publishing,
deployments, and API writes, except for this explicit artifact-upload tool.
These instructions are not an OS-level restriction.

**This is arbitrary code execution with the bot's OS permissions, not a
sandbox.** Separate directories, a minimal child environment, and instructions
reduce accidental interference; they do not prevent access to bot files,
processes, credentials, private networks, or cloud metadata. Treat `agent.run`
as equivalent to shell access to MihuBot. Web/Discord content can contain prompt
injection, and anything given to the agent can potentially be sent to the web.

Bot administrators have access by default. Grant or revoke other trusted users
through the existing permissions command:

```text
!permissions add agent.run <userId>
!permissions remove agent.run <userId>
```

One run is allowed at a time, with a thirty-minute timeout. Use the existing red X
reaction on the original request message to cancel, for both plain and prefixed
commands. The requester or a bot administrator can cancel the run.
Replies suppress mentions; responses longer than Discord's message limit are
attached as text files. Generated files are uploaded only when the agent calls
`upload_file`.

Progress edits wait three seconds after the previous edit completes, including
any Discord rate-limit delay. The embed shows elapsed time, up to four running
tools, six recent tools, and 2500 characters of the latest streamed public
assistant message, split across fields to respect Discord's limits. Running tools
each get a full-width field with a short tool/status heading and normal-text
descriptions and targets on separate paragraphs, so wrapped lines stay grouped.
Completed tools use compact rows in one recent-activity field, showing the target
or task description without repeating generic execution descriptions.
They show their names, task descriptions, and relevant search queries, URLs, or paths,
rather than just generic execution titles. Raw shell commands, other tool
arguments, raw results, and private reasoning are not rendered. Progress
edits stop before the final response replaces the embed.

The live footer accumulates reported input/output tokens across the parent and
subagents, updating after model calls complete. USD estimates reuse TLDR's token
formatting and model prices, pricing each call separately for cache discounts
and long-context thresholds. USD values are API estimates, not Copilot charges.
Missing counts or unknown pricing are shown as unavailable rather than
zero. The reported usage summary remains with the final response, including on
cancellation or failure.

The agent is instructed to wrap ordinary links in angle brackets to avoid Discord
previews unless a preview is explicitly intended. Markdown is sent as generated,
without link post-processing.

Experimental mode is enabled. File-based hooks (`.github/hooks/`) are disabled to avoid discovering additional
commands on disk. This does not disable file tools or SDK-registered callbacks.

Each run gets separate working, home, temporary, and Copilot-state directories.
On completion, failure, cancellation, or graceful bot shutdown, MihuBot force-stops
the SDK-owned runtime process tree before deleting the run directory. Startup
removes leftover run directories under the exclusively locked workspace root
before accepting requests, and logs how many were recovered. Cleanup honors
startup cancellation and leaves unrelated directories untouched.
If runtime shutdown fails, the directory is retained and the error is logged.
All Discord command and message-handler cancellation tokens are signaled as soon
as host shutdown begins, including self-update restarts. New work is rejected
during shutdown, and Discord stays connected while tracked commands finish their
cleanup, up to the host's shutdown deadline. Reaction-cancelled commands remain
tracked until they finish. Plain-message handlers share one cancellation token
and are tracked as a group only if their dispatch has unfinished work; shutdown
waits for all handlers in that group.
This is best-effort lifecycle cleanup, not containment: deliberately detached
processes, writes outside the workspace, and abrupt host termination cannot be
reliably cleaned up this way.

The global runtime configuration key `Agent.Model` optionally selects a model;
otherwise it uses `OpenAIService.DefaultAgentModel` (`gpt-6.1-sol`). Sessions use
`high` reasoning effort:

```text
!config set global Agent.Model <modelId>
```

Deployment prerequisites and credentials are described in
[the deployment guide](../../deploy/README.md#local-copilot-agent).
