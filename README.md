# Stillwave

A small internet radio player for background listening while you work. It comes in two versions:

**[Open the web player](https://grayjfiii.github.io/stillwave/)** (no install, any browser) or **[download the Windows app](https://github.com/grayjfiii/stillwave/releases/latest)**.

| | Windows app | Web version |
|---|---|---|
| Runs on | Windows 10 / 11 | Any modern browser (Windows, Mac, Linux, phone) |
| Where | [`windows/`](windows) | [`docs/index.html`](docs/index.html) |
| Station search (~60,000 via [radio-browser.info](https://www.radio-browser.info/)) | yes | yes |
| Favorites, Random, Add your own stream | yes | yes |
| Live spectrum | whatever your PC is playing | the station's own audio, only where the station allows it |
| Song title, album art, artist profile | yes | no (browsers can't read most stream metadata) |
| Radio Garden, Internet Archive, podcasts | yes | no |
| Mini always-on-top mode, taskbar controls | yes | no |

## Web version

Use it online at https://grayjfiii.github.io/stillwave/, open `docs/index.html` in a browser, or host the `docs` folder on GitHub Pages (Settings, Pages, deploy from branch, `/docs`). Favorites are stored in your browser.

Notes: on an https page, browsers block plain `http` streams, so those stations are hidden there. Some stations don't allow the spectrum in a browser; they still play.

## Windows app

Download `Stillwave-win-x64.zip` from the Releases page, unzip it anywhere and run `Stillwave.exe`. Nothing else needs to be installed (.NET is bundled).

Windows may show a "Windows protected your PC" warning because the app isn't code-signed. Click **More info**, then **Run anyway**.

Features: station search by name, country, state or genre; Radio Garden (with a random-place button); the Internet Archive (old-time radio, live concerts, audiobooks, 78 rpm records); podcasts; add your own stream or import an M3U / PLS playlist; a "Now playing" column; song title, album art and a short artist profile; a spectrum analyzer with several styles and colors (click to cycle, right-click for options); favorites; Random; a small Mini mode; taskbar right-click controls.

### Optional: ffmpeg

Windows' built-in player handles most stations. A few stream formats need `ffplay` from ffmpeg as a fallback. If a station says it can't play, install it and restart the app:

```
winget install ffmpeg
```

You can also drop `ffplay.exe` next to `Stillwave.exe`.

### Build from source

Requires the .NET 8 SDK on Windows 10/11. From the `windows` folder:

```
dotnet build -c Release
```

Single-file release exe:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none
```

Favorites and settings are stored in `%APPDATA%\Stillwave`. The app deliberately doesn't remember its window position or size.

## Credits and data

Station listings come from radio-browser.info, Radio Garden and the Internet Archive; song and artist info from stream metadata, iTunes Search, Wikipedia and TheAudioDB. Their data belongs to them and is subject to their terms.
