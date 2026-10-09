# YouTube Archive for Brave

Archive only material you own or have permission to download, subject to applicable
law and YouTube's terms. This archives individual videos, not playlists or live streams.

## Backend setup

Set startup configuration (or equivalent environment variables):

| Key | Value |
| --- | --- |
| `YoutubeArchive:Directory` | Absolute path to the writable archive mount, e.g. `/youtube-archive` |

Configure one or more dedicated random tokens through global runtime configuration:

```text
!config set global YoutubeArchive.SharedSecrets <token-one>,<token-two>
```

Each token must contain 32-2048 characters. Give each extension user a separate
token; additions, rotations and revocations take effect on the next request,
without restarting the bot. Send the configuration command only in a private,
trusted Discord context. No configured tokens means the API rejects all requests.

Tokens are runtime-only. Removing or clearing `YoutubeArchive.SharedSecrets`
revokes all tokens. Invalid token lengths are rejected when setting or reloading
global configuration, not during authentication.

If browser cookie sharing is disabled, an optional server-side cookies file can
be selected through global runtime `YoutubeArchive.CookiesFile` (an absolute
path to a writable mounted Netscape-format file). Changes apply to the next
lookup/download; startup `YoutubeArchive:CookiesFile` is not used.

For example, generate a secret locally with
`[Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`
in PowerShell. Put each user's token in the backend list and that user's extension settings.
Do not reuse Discord, Jellyfin, or other integration credentials.

The Docker image includes yt-dlp, ffmpeg, and Deno. Rebuild the image to install
updated native tooling; bot self-updates alone do not update these tools.
Outside Docker install current yt-dlp (including its bundled EJS components),
ffmpeg/ffprobe and Deno on PATH. YouTube may require server-side cookies or may
reject downloads from a datacenter IP. These failures are shown in job status;
the extension does not bypass access restrictions.

Mount the same archive into Jellyfin and configure its libraries to scan `Audio`
and `Video`. Archival does not trigger Jellyfin library refreshes; use Jellyfin's
own monitoring/scheduled scans or refresh manually. Jellyfin is optional.

## Install in Brave on Windows

1. Open `brave://extensions`, enable **Developer mode**, and click **Load unpacked**.
2. Select this directory (`extensions\youtube-archive`), then pin its toolbar button.
3. Open the extension's **Settings**. Enter your MihuBot HTTPS base URL and shared
   secret; **Save and verify connection** grants access to that server only.
4. Open a YouTube watch page, Short, or YouTube Music video, and click the toolbar
   button. Pick audio-only or video, optionally select maximum resolution, and
   click **Archive**.

Audio is the default on YouTube Music and videos categorized as Music by YouTube.
If category lookup fails, an explicit warning lets you choose manually.
The extension remembers maximum video resolution across browser restarts, not
mode: each new popup defaults mode based on the current video's category.
Resolution is a ceiling; a lower resolution is used if the selected height is
unavailable. **Best available** has no ceiling.

The secret and quality preference stay in `chrome.storage.local`, not browser
sync or page scripts. Only HTTPS is accepted except for HTTP on localhost during
development. Serve the API over HTTPS through your existing reverse proxy;
do not expose an unencrypted remote API or disable TLS validation.

## Optional browser cookies

Enable **Share this browser's YouTube cookies with MihuBot** in Settings to
grant cookie access. This is off by default. Only unpartitioned `youtube.com`
cookies from the active tab's cookie store are submitted; Google/other sites'
cookies and other profiles' cookies are not read. HttpOnly, secure and session
cookie attributes are preserved. Cookies are fetched again for each lookup and
submission, so signing in/out is reflected without reinstalling the extension.

Only enable this for your trusted backend: YouTube session cookies may grant
account access. Cookies are sent in authenticated HTTPS POST bodies, never in
URLs, and are not stored in extension settings or returned in API job responses.
Browser-supplied cookies override the runtime file, including an empty jar.
Disable the setting to revoke the extension's cookie permission.

Queued cookie data is encrypted with ASP.NET Data Protection; retain and protect
`State/YoutubeArchiveKeys` alongside the queue so interrupted jobs can resume.
These keys rely on the State directory's filesystem permissions. Cookie data is
removed from job history on completion or failure. yt-dlp receives an isolated,
owner-only temporary Netscape file on Linux, deleted when its process finishes,
including on cancellation or failure. A process/container crash can leave a
private `mihubot-youtube-cookies-<guid>.txt` file in the OS temporary directory;
delete such stale files only while the bot is stopped.
Raw info-JSON sidecars are disabled when any cookies file is used, since they
can contain credentials. Media tags and thumbnail sidecars are still retained.

## Archive behavior and API

Audio uses the best audio-only stream and lossless extraction/remuxing to its
native audio container; video uses best video plus audio and lossless MKV
remuxing. Neither mode intentionally re-encodes media. Metadata and JPEG
thumbnail sidecars are retained, and media tags are embedded where supported.

Files live in `Audio/<video-id>/` or `Video/<video-id>-<height-or-best>/`, with
readable title/ID filenames. Work in progress stays under `.incomplete` until
the entire job succeeds; keep that directory out of Jellyfin libraries.
The durable queue lives in `State/YoutubeArchive.json`. One job runs at a time,
with a four-hour timeout and up to 100 queued/running jobs. Queued and interrupted
jobs resume after restart, including yt-dlp's partial downloads. Failed jobs can
be retried by clicking Archive again. Successful requests for the same video,
mode and quality are deduplicated. Missing previously completed media produces
an explicit error rather than reporting a successful duplicate. History is retained; do not manually remove
completed media unless you also remove its queue entry while the bot is stopped.
Failed jobs can leave partial files in their own `.incomplete/<job-id>` folder;
remove those specific folders only when no longer needed.

All API endpoints require `Authorization: Bearer <shared-secret>`:

| Endpoint (under `/api/YoutubeArchive/`) | Result |
| --- | --- |
| `GET Metadata?url=<encoded-video-url>` | Title, music category and live state |
| `POST Metadata` | Same lookup, accepting `{ "url": "...", "cookies": [...] }` without putting cookies in the URL |
| `POST Jobs` | HTTP 202 and queued/existing job |
| `GET Jobs` | Newest 100 jobs |
| `GET Jobs/<guid>` | A single job, including state, relative media path and errors/warnings |

Example POST body:

```json
{"url":"https://www.youtube.com/watch?v=abcdefghijk","mode":"video","maxHeight":1080}
```

`mode` is `audio` or `video`; `maxHeight` must be null for audio, and null (best),
360, 480, 720, 1080, 1440, 2160 or 4320 for video. Only recognized YouTube video
URLs are accepted and canonicalized; the API never passes an arbitrary URL or
user-supplied downloader flags to yt-dlp.
An optional `cookies` array accepts objects with `domain`, `path`, `name`, `value`,
`secure`, `httpOnly`, and nullable Unix-second `expires` fields. Only YouTube
domains are accepted; requests are capped at 256 KiB and 200 cookies.

Extension logic checks: `node --test extensions\youtube-archive\tests\*.test.mjs`.
