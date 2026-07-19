# Running MihuBot in Docker

MihuBot updates itself: it polls GitHub for new commits on `main` and, when it
detects one, invokes `build-latest.sh` locally to produce an `artifacts.tar.gz`
in `next_update/` and then exits. A **runner loop** applies the pending update
and relaunches the app. `run.sh` is that loop, and this directory packages it
into a container so the bot can be hosted anywhere (not just on the Azure VM).

## How it works

Everything lives under `/data` (a persistent volume):

| Path                   | Purpose                                            |
| ---------------------- | -------------------------------------------------- |
| `/data/artifacts/`     | Current build; **replaced** on every update        |
| `/data/State/`         | Persistent state (SQLite DBs, logs, JSON stores, TLS certs) |
| `/data/next_update/`   | Incoming `artifacts.tar.gz` produced by the app    |

The app resolves `State/` and `next_update/` relative to its working directory,
so the runner starts it from `/data` and pins ASP.NET's content root to
`artifacts/` (which drives `wwwroot`/appsettings). This keeps the data and the
replaceable build separate without any symlinks.

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

## Notes

- MihuBot publishes `linux-x64`, so the image is `linux/amd64`.
- Ports 80/443 are exposed; port 80 must be reachable for LettuceEncrypt (ACME).
  TLS certs are persisted under `State/certs` (on the `/data` volume).
- `MIHUBOT_EXECUTABLE` overrides the executable name (default `MihuBot`).
