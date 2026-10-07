# sts2.fun Uploader

A Slay the Spire 2 mod that uploads your finished runs to [sts2.fun](https://sts2.fun),
the community stats site, so they count in the statistics and appear on your player page.

## What it does

- When the game starts and after each run, it looks at your run history
  (the game's `.run` files), asks sts2.fun which ones it doesn't have yet,
  and uploads those.
- Shows your sts2.fun account name on the main menu; click it to open your player page.

## What it sends

Only your `.run` files. Nothing else is read or sent, and it does not change gameplay
(`affects_gameplay: false`). The site identifies you from your run files, the same way
as the [upload page](https://sts2.fun/upload): your earliest solo run is included with
each upload so new runs go to the right account.

## Turning it off

Set `"Enabled": false` in `sts2fun_uploader/config.json` in the game's user data folder
(Linux: `~/.local/share/SlayTheSpire2/`, Windows: `%APPDATA%/SlayTheSpire2/`), or unsubscribe.

## Building

Requires the .NET 9 SDK and an installed copy of the game (the build references its assemblies).

```
dotnet build -c Release            # GameDir defaults to the Linux Steam path
dotnet build -c Release -p:GameDir="C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
```

Then copy `manifest.json` and `bin/Release/net9.0/sts2fun_uploader.dll` into
`<game folder>/mods/sts2fun_uploader/`.
