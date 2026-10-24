# Patched Discord.Net packages

These four NuGet packages are built from Discord.Net commit
`4e2e8782d39eb8504d0a2e10aac458bfb8df2c25`, the pinned head of
[discord-net/Discord.Net#3288](https://github.com/discord-net/Discord.Net/pull/3288).
The patch fixes DAVE MLS roster validation by including the bot's own user ID
and null-terminating native user-ID strings.

`nuget.config` includes this directory so fresh deployment builds can restore
the packages without a sibling checkout or a pre-populated NuGet cache.
WebSocket and Dave are referenced directly; Core and Rest are their dependencies.
The custom version avoids collisions with official packages. No native libdave
change is needed.

To reproduce from the MihuBot repository root with a .NET SDK supporting the
upstream target frameworks:

```powershell
git clone https://github.com/discord-net/Discord.Net.git ..\Discord.Net
git -C ..\Discord.Net fetch origin pull/3288/head
git -C ..\Discord.Net checkout --detach 4e2e8782d39eb8504d0a2e10aac458bfb8df2c25

$projects = @(
    'src\Discord.Net.Core'
    'src\Discord.Net.Rest'
    'tools\Discord.Net.Dave'
    'src\Discord.Net.WebSocket'
)

foreach ($project in $projects) {
    dotnet pack "..\Discord.Net\$project" -c Release `
        -p:Version=3.20.3-mihubot.1 -o .\packages --verbosity quiet

    if ($LASTEXITCODE -ne 0) {
        throw "Packing $project failed."
    }
}
```

Keep the four `.nupkg` files; generated `.snupkg` files are not required.
Discord.Net is MIT-licensed; its license metadata is included in each package.

When an official release includes the patch, update the `Discord.Net` reference,
remove the WebSocket/Dave overrides and local package source, and delete these
packages.
