# Running MihuBot in Docker

MihuBot updates itself: it polls GitHub for new commits on `main` and, when it
detects one, invokes `build-latest.sh` locally to produce an `artifacts.tar.gz`
in `next_update/` and then exits. A **runner loop** applies the pending update
and relaunches the app. `run.sh` is that loop, and this directory packages it
into a container so the bot can be hosted anywhere (not just on the Azure VM).

A new commit is only built if it passes verification: it must carry a signature
GitHub reports as verified, its committer must be `MihaZupan` and the signature
must have been made with one of the trusted SSH signing keys (the committer is
hardcoded in `SelfUpdateService`; the keys default to a hardcoded value but can be
overridden at runtime via the `SelfUpdate.TrustedSigningKeys` config key - a
comma-separated list of `ssh-...`/`sk-ssh-...` public keys, to allow key rotation.
Note that commits created through the GitHub web UI are signed by `web-flow` and
are therefore rejected),
and it must be newer than the commit currently running - either a descendant of it,
or, if history was rewritten (force push), a commit with a newer commit date - so
downgrades are never deployed. The verified SHA is passed to
`build-latest.sh` as `MIHUBOT_COMMIT` so the build can't pick up a newer,
unverified branch tip.

## How it works

Everything lives under `/data` (a persistent volume), except the bulk file
storage which gets its own volume at `/storage`:

| Path                   | Purpose                                            |
| ---------------------- | -------------------------------------------------- |
| `/data/artifacts/`     | Current build; **replaced** on every update        |
| `/data/State/`         | Persistent state (SQLite DBs, logs, JSON stores)   |
| `/data/next_update/`   | Incoming `artifacts.tar.gz` produced by the app    |
| `/storage/`            | `StorageService` file blobs (uploaded files)       |

The app resolves `State/` and `next_update/` relative to its working directory,
so the runner starts it from `/data` and pins ASP.NET's content root to
`artifacts/` (which drives `wwwroot`/appsettings). This keeps the data and the
replaceable build separate without any symlinks.

The storage location is controlled by `MIHUBOT_STORAGE_DIRECTORY` (set to
`/storage` by the image); when unset the app falls back to `State/Files`.

## Browser assets

Frontend dependencies are pinned directly in source, not restored through npm:
Bootstrap 5.3.8 (including Popper 2.11.8) and jQuery Slim 4.0.0 in
`MihuBot/Components/App.razor`, Bootstrap Icons 1.13.1 in `MihuBot/wwwroot/app.css`,
and highlight.js 11.12.0 (core and C# module) in
`MihuBot/Components/CodeHighlight.razor.js`. The matching VS2015 theme is vendored
in `MihuBot/wwwroot/vs2015.css`. Blazor's JavaScript comes from the .NET build.
Browsers need access to `cdn.jsdelivr.net` and `cdnjs.cloudflare.com`.

When updating these assets, update script/stylesheet integrity hashes alongside
their URLs and keep highlight.js and its theme aligned. Bootstrap controls use
the v5 `data-bs-*` attributes and native JavaScript API; `wwwroot/app.js` delegates
tooltips to support dynamically rendered Blazor controls and disposes them when
controls are removed. The global `data-bs-theme="dark"` and Bootstrap CSS variables
preserve dark controls, close buttons, and table cells.
Tables must explicitly wrap body rows in `<tbody>`: interactive Blazor creates
DOM nodes directly, whereas Bootstrap 5's cell selectors require a table section.

Run the dependency-free JavaScript regression checks with
`node --test MihuBot.Tests/FrontendTests.mjs` from the repository root, in addition
to the .NET tests. For browser smoke checks, exercise dropdowns, modal close/static
backdrop behavior, advanced-options collapse, dynamically added tooltips, syntax
highlighting, and the narrow-screen navigation.

The `/regex` GUI uses the regex source generator from the project's resolved .NET
reference pack. Normal builds and publishes copy the analyzer DLL next to the app
(outside the single-file bundle), so local runs need no manual setup. Additional
versions can be placed in `State/RegexSourceGenerators/<version>.dll`. The core
methods view falls back to full source when a pattern has no core methods to show.

## Volumes

`docker-compose.yml` declares two named volumes, `mihubot-data` (mounted at
`/data`) and `mihubot-storage` (mounted at `/storage`), so state and file
storage can be backed up, sized, or relocated independently.

Either can be pointed at a host directory (or a pre-created named volume)
without editing the compose file:

```bash
MIHUBOT_STORAGE_VOLUME=/mnt/bigdisk/mihubot-storage \
MIHUBOT_DATA_VOLUME=/srv/mihubot-data \
  docker compose up -d --build
```

## Run it

```bash
docker compose up -d --build
```

On first boot (or if the build is ever missing), the runner bootstraps by
running `build-latest.sh` itself, which clones the source, fetches the .NET SDK
into a temporary directory, and produces `State/artifacts.tar.gz`. After that,
the app takes over: it detects new commits on `main` and shells out to the same
`build-latest.sh` to prepare each subsequent update.

The first build takes a few minutes (SDK download + compile); follow it with
`docker compose logs -f mihubot`.

### Supplying a build manually (optional)

To skip the in-container build, drop a prebuilt tarball into the State directory
and the runner uses it instead:

```bash
# Reproduce the build locally:
#   dotnet publish MihuBot -c Release -r linux-x64 --self-contained true \
#     -p:PublishSingleFile=true -o artifacts && tar -czf artifacts.tar.gz artifacts
docker compose cp artifacts.tar.gz mihubot:/data/State/artifacts.tar.gz
docker compose restart mihubot
```

## Secrets when running outside Azure

Key Vault is still used, but authentication no longer requires running inside
Azure (see `MihuBot/Program.cs`). Provide an Azure service principal either as
environment variables (`Azure__TenantId`, `Azure__ClientId`, `Azure__ClientSecret`)
in `docker-compose.yml`, or as a `credentials.json` placed at
`/data/credentials.json` (the runner copies it in and it survives updates).

## Optional integrations

Only Discord (`Discord:AuthToken`) is required for the bot itself. These
integrations register themselves only when their configuration is present, and
the features depending on them (commands, handlers, API controllers, pages,
background services) are disabled instead of failing startup when it isn't. On
startup the missing ones are written to the console and posted to the Discord
debug channel (see `MihuBot/Configuration/OptionalFeatures.cs`):

| Configuration | Disabled without it |
| --- | --- |
| `AppInsights:ConnectionString` | Azure Monitor / OpenTelemetry export |
| `AzureOpenAI:Key` | Everything AI: `!chatgpt`, `!imagine`, `!duplicates`, GitHub search/triage pages, auto-triage, area label detection, semantic ingestion, the MCP endpoint, and `!magic8ball` prompt similarity |
| `AzureOpenAI:ImageKey` | Image generation (`!imagine`) |
| `AzureOpenAI:SecondaryChat:Endpoint` + `:Key` | Secondary chat endpoint (falls back to the primary one) |
| `AzureOpenAI:SecondaryEmbedding:Endpoint` + `:Key` | Secondary embedding endpoint (falls back to the primary one) |
| `AzureStorage:ConnectionString` | Archiving Discord attachments to blob storage (files stay on disk) |
| `AzureStorage:ConnectionString-RuntimeUtils` | Fuzzing coverage reports and jitdiff extra assemblies |
| `GitHub:Token` | All GitHub API access: runtime-utils jobs and their API/pages, data ingestion, notifications, self-update, `!runtimeutils` |
| `GitHub:ClientId` + `GitHub:ClientSecret` *(`-dev` suffixed outside Linux)* | Signing in with GitHub (the login link is hidden and the endpoint 404s) |
| `Discord:ClientSecret` *(`-dev` suffixed outside Linux)* | Signing in with Discord — this is the OAuth secret, separate from the bot token |
| `GitHub-PostgreSQL:ConnectionString` | The GitHub data database: ingestion, search, triage, duplicate detection, issue data page |
| `OpenWeather:ApiKey` | `!weather` and location lookups |
| `Qdrant:Host` (+`:Port`) | Vector search + semantic ingestion (FTS still works) |
| `Hetzner:ApiKey` | Hetzner runner VMs → jobs fall back to Azure VMs |
| `GoogleMaps:ApiKey` | Static map image on relayed Telegram locations |
| `Molly:AlertEmailConnectionString`+`:AlertEmailFrom` | Emails Molly alerts via Azure Communication Services. Recipients come from the runtime `Molly.AlertEmailTo` config key (comma-separated). Every recipient must have a Proton Mail key |
| `Youtube:ApiKey` | YouTube API search/playlists (scraping fallback remains) |
| `Spotify:ClientId`+`ClientSecret` | Spotify links in `!play` |
| `TelegramBot:ApiKey` | Telegram relay + webhook endpoint (404) |
| `Tenor:ApiKey` | Tenor links in `!emote` |
| `Minecraft:Host`+`RconPassword` | `!mc`, Minecraft remote page + nav link |
| `QBittorrent:Host`/`Username`/`Password` | `!pirate` |
| `Jellyfin:Host`+`ApiKey` | `!pirate` |

## Runtime-utils permissions

Set `RuntimeUtils.Admin.<GitHubLogin>` to `true` in global runtime configuration
to grant both admin privileges and job-submission authorization:

```text
!config set global RuntimeUtils.Admin.<GitHubLogin> true
```

This applies to the web GUI, GitHub comments, and authenticated API/MCP job
submissions; no separate `RuntimeUtils.AuthorizedUser.<GitHubLogin>` flag is needed.
Explicit `RuntimeUtils.BlockedUser.<GitHubLogin>` or `RuntimeUtils.BlockedUser.<GitHubId>`
flags still deny submission authorization, including for admins.
Reload an open runtime-utils page after changing permissions.

## Notes

- MihuBot publishes `linux-x64`, so the image is `linux/amd64`.
- Ports 5000/5001 are exposed; 5001 is H2C.
- `MIHUBOT_EXECUTABLE` overrides the executable name (default `MihuBot`).
- `MIHUBOT_STORAGE_DIRECTORY` overrides where the `StorageService` keeps files
  (default `/storage` in the image, `State/Files` outside of it). Existing
  deployments can migrate by moving `State/Files` onto the new volume.
