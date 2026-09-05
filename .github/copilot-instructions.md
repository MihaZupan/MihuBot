# MihuBot

A single ASP.NET Core app (`MihuBot/MihuBot.csproj`, `net11.0`, `LangVersion=preview`) that is at once a
Discord bot, a Blazor Server web app, a set of API controllers, and a pile of background services
(GitHub data ingestion, dotnet/runtime "runtime-utils" jobs, Telegram relay, Molly, storage, URL shortener).
`MihuBot.Tests` is a small xUnit project. Solution file is `MihuBot.slnx` (XML solution format).

The `runtime-utils` runner (the other half of the RuntimeUtils feature) lives in a **separate repo**, usually
cloned next to this one at `../runtime-utils` - see "Companion repo: runtime-utils" below.

## Build / test

```powershell
dotnet build MihuBot.slnx                 # TreatWarningsAsErrors is on - warnings fail the build
dotnet test MihuBot.Tests
dotnet test MihuBot.Tests --filter "FullyQualifiedName~TryGetSshSignaturePublicKey_ExtractsTheSigningKey"
```

- Requires a .NET 11 preview SDK; both projects are `SelfContained` (the test project must be, because it
  references the self-contained app).
- Lint/style is enforced in-build: `EnforceCodeStyleInBuild` + `.editorconfig` + `CodeAnalysis.globalconfig`
  (several CA/SYSLIB rules are `error`, e.g. CA1309/CA1310/CA1311 - always pass an explicit `StringComparison`).
- There is no lint-only command; a build is the lint.
- Running the app locally needs at minimum `Discord:AuthToken-dev` (see "Configuration" below); most work
  can be validated with a build + targeted tests instead.

## Architecture

- **`Program.cs`** is the whole composition root: Kestrel setup (5000 HTTP, 5001 H2C), Key Vault/config,
  every `services.Add...` call, auth policies, and the ASP.NET pipeline. Any new service, page area, or
  integration is wired here.
- **Discord** (`Discord/`): `MihuBotService` (an `IHostedService`) reflects over the assembly at startup and
  instantiates every public non-abstract `CommandBase` / `NonCommandHandler` via `ActivatorUtilities`.
  Adding a command = adding a class deriving from `CommandBase` (override `Command`, optional `Aliases`,
  `ExecuteAsync`); no registration needed. Commands are matched from a `CompactPrefixTree` against messages
  starting with `!`, `/` or `-`. `INonCommandHandler.HandleAsync` sees every message.
- **Web UI** (`Components/`): Blazor Server. Pages live in `Components/Pages`, auth via
  `@attribute [Authorize("Admin")]` / `[Authorize(Policy = "Discord")]` / `"GitHub"` policies defined in `Program.cs`.
- **APIs** (`API/`): plain MVC controllers.
- **Data** (`DB/`): five `DbContext`s. Four are SQLite files under `State/` (`MihuBotDbContext`,
  `LogsDbContext`, `StorageDbContext`, `MollyDbContext`), one is PostgreSQL (`GitHubDbContext`, only when
  `GitHub-PostgreSQL:ConnectionString` is set). All are registered as *pooled context factories* in
  `DB/DbServiceCollectionExtensions.cs`, which also runs migrations at startup. Migrations live in
  `Migrations/<ContextName>Db/` (except `MihuBotDbContext`, whose migrations are directly in `Migrations/`).
- **RuntimeUtils** (`RuntimeUtils/`): dotnet/runtime automation. Jobs derive from `JobBase` (`Jobs/`),
  provisioning Azure/Hetzner/Helix runners; `DataIngestion.GitHub/` mirrors GitHub issues/PRs into the
  Postgres DB and Qdrant, and `AI/` provides triage/search/MCP on top of it. The code that actually runs
  *on* those runners is not in this repo - see "Companion repo: runtime-utils".
- **Self-update** (`SelfUpdateService.cs`, `deploy/`): the running bot polls `main`, verifies the commit is
  SSH-signed by a trusted key and committed by `MihaZupan`, builds a tarball via `deploy/build-latest.sh`
  and exits; `deploy/run.sh` swaps the artifacts in and restarts. See `deploy/README.md` for the full
  deployment/volume layout - keep it in sync when changing update, storage, or config behavior.

## Companion repo: runtime-utils

`MihaZupan/runtime-utils` (usually cloned next to this repo at `../runtime-utils`) holds the *runner* side of
the RuntimeUtils feature: the code that actually executes on the Azure/Hetzner/Helix/GitHub-Actions machines
MihuBot provisions. When a task touches jobs, job arguments, logs, artifacts or the runner API, assume both
repos are in scope and check `..\runtime-utils` before concluding something is missing. Read its
`.github\copilot-instructions.md` for runner-side build requirements, lifecycle, and wire contracts.

- **Layout:** one project, `Runner/Runner.csproj` (`net11.0`, `Nullable` *enabled*, solution `Runner.slnx`).
  `Runner/Program.cs` is the entry point, `Runner/JobBase.cs` the shared job infrastructure, `Runner/Jobs/`
  the job implementations, `Runner/Helpers/` the utilities (jitdiff, NuGet, git, core root, ...).
  Global usings live in `Runner/Usings.cs`. The runner requires a current daily .NET 11 SDK for its in-box
  ZStandard APIs; an older .NET 11 preview SDK may not suffice. There are no tests; build with
  `dotnet build ..\runtime-utils\Runner\Runner.slnx`.
- **Jobs are paired 1:1 by class name.** `MihuBot/RuntimeUtils/Jobs/XJob.cs` (orchestration: provisioning,
  GitHub comment, metadata) has a counterpart `Runner/Jobs/XJob.cs` (execution). `Runner/Program.cs`
  dispatches on the `JobType` metadata value, which is the MihuBot job class name - so adding, renaming or
  removing a job means changing **both** repos, and the runner-side `switch` must be updated too.
- **Contract between them** is HTTP against `https://mihubot.xyz/api/RuntimeUtils/`
  (`MihuBot/API/RuntimeUtilsController.cs`): `Jobs/Metadata`, `Jobs/Logs`, `Jobs/SystemInfo`,
  `Jobs/Artifact`, `Jobs/Complete`, `Jobs/Progress`, `Jobs/AnnounceRunner`; core root archives go through
  `API/CoreRootController.cs` ↔ `Runner/Helpers/CoreRootAPI.cs`. Everything the runner knows about a job
  arrives as the string-to-string metadata dictionary built by MihuBot's `JobBase` (`BaseRepo`, `PrBranch`,
  `CustomArguments`, `PersistentStateSasUri`, ...). New per-job inputs are added as metadata entries or as
  `CustomArguments` flags (`-flag` / `-arg value`, read via `TryGetFlag`/`TryGetArgument` in the runner).
  Keep server-side argument validation and usage text in sync.
- **Hosted diff examples:** The runner produces diff artifacts and MihuBot displays them in its web UI.
  Changes to diff reporting may require updating both repositories.
- **VM and Helix startup scripts clone unpinned runner source.** The scripts in
  `MihuBot\RuntimeUtils\JobBase.cs` clone the default branch of `MihaZupan/runtime-utils` and build/run it
  from a separate sibling `runner-work` directory. Never use the project directory as scratch space:
  the runner clones dotnet/runtime and generates artifacts there. MihuBot and the runner, including
  prepared runners, are assumed to use matching, up-to-date versions.
- **Linux Helix work items require Helix-specific prerequisite images.** `GetHelixDockerImage` defaults to
  `mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-24.04-helix-amd64` or
  `mcr.microsoft.com/dotnet-buildtools/prereqs:ubuntu-24.04-helix-arm64v8`, selected from the actual queue.
  The `helixbot` user, Helix scripts, and access to the mounted commands are required; a plain runner/build
  image is not interchangeable. Defaults can be overridden through `GetConfigFlag` with
  `HelixDockerImageamd64` / `HelixDockerImagearm64`, or by an admin's `-docker <image>`.
  This is separate from `mihazupan/runtime-utils:runner`, which the companion repo builds for prepared
  Docker runners. Helix uses the container as its environment and clones/builds fresh runner source.
- **GitHub Actions path:** jobs can also run via `.github/workflows/run-script.yml` in
  `MihuBot/runtime-utils`, triggered by an issue whose body contains `RUN_AS_GITHUB_ACTION_<ExternalId>`.
  Job reports and AI triage issues are filed in `MihuBot/runtime-utils` (`JobBase.IssueRepositoryName`).

## Key conventions

- **Optional integrations are the central pattern.** Each integration is an `OptionalFeature` in
  `Configuration/OptionalFeatures.cs` (a description + the config keys it needs). Services are only
  registered when `configuration.IsConfigured(OptionalFeatures.X)`. Consumers must then degrade gracefully:
  - Discord commands/handlers and API controllers whose constructor parameters can't be resolved are silently
    skipped (`Configuration/OptionalDependencies.cs`, `RemoveUnavailableControllersConvention`).
  - Blazor pages are checked in `Components/Routes.razor` and render `FeatureUnavailable` instead; a page that
    needs a feature must `@inject` that service (even if unused) for this to work.
  - Nav links / UI use `AvailableFeatures` (`Configuration/AvailableFeatures.cs`).
  When adding an integration: add the `OptionalFeature`, register conditionally in `Program.cs`, add it to
  `OptionalFeatures.All`, and document it in the `deploy/README.md` table.
- **Two kinds of configuration.** Startup secrets come from `IConfiguration` (Azure Key Vault →
  `credentials.json` → environment variables, colon keys like `GitHub:Token`). Values that must change without
  a redeploy come from `IConfigurationService` (`Configuration/ConfigurationService.cs`), which is keyed by an
  optional guild/user context and uses dot-separated keys like `Molly.AlertEmailTo`.
- **Dev vs prod credentials:** `Constants.DevSuffix` is `""` on Linux and `"-dev"` everywhere else, so Discord
  and OAuth keys are suffixed off-Linux. `OperatingSystem.IsLinux()` is used throughout as "this is the real
  deployment".
- **State lives in `State/`** (`Constants.StateDirectory`), relative to the working directory: SQLite DBs,
  `SynchronizedLocalJsonStore<T>` JSON stores, and `FileBackedHashSet` text files. Bulk uploads go to
  `Constants.StorageDirectory` (`MIHUBOT_STORAGE_DIRECTORY`, default `State/Files`).
- **Global usings** are in `Usings.cs` - `Discord`, `Discord.WebSocket`, `MihuBot.Helpers`, common `System.*`.
  Don't re-add those usings.
- Well-known Discord guild/channel/user ids and emotes are constants in `Helpers/Constants.cs`; use them
  rather than inlining ids.
- The main project does not enable `Nullable`; the test project does.
- Logging to Discord/the debug channel goes through the custom `Logger` (`Logger.cs`), not just `ILogger`.
- Commits containing the word `docker` on `main` trigger the Docker Hub image publish workflow.
