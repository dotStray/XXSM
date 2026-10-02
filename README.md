# XXSM

XXSM is a mod manager for the games [XXMI](https://github.com/SpectrumQT/XXMI-Launcher) loads mods into. Made with
Avalonia, for Linux first, and on Windows too.

I was using [JASM](https://github.com/Jorixon/JASM) on Windows to manage my mods for a long time, but it stopped
receiving updates. Then I moved to Linux, where JASM does not run at all. I switched to
[XX-Mod-Manager](https://github.com/XiaoLinXiaoZhu/XX-Mod-Manager), but sorting and keeping track of everything by
hand got tedious, so I made this: JASM's way of working, on Linux, with the sorting done for you.

The app's game data (characters, portraits and hashes) is pulled from a separate source that updates itself, the
[xxsm-presets](https://github.com/dotStray/xxsm-presets) repository, so I don't have to maintain the app.

This app was vibe coded: written with AI assistance.

**Remember to make backups ⚠️**

Logs are written to `~/.local/state/xxsm/logs` on Linux, and `%LOCALAPPDATA%\xxsm\logs` on Windows.

## Features
- A grid of characters, each with its own mods. Pin your favourites to the top.
- Automatically sort mods into their character's folder, by the hashes in their INI files. Shows what it would move
  before moving anything.
- Turn mods on and off, one at a time or with saved profiles
- Drag and drop folders and archives (zip, 7z, rar) directly into the app
- Install from a GameBanana link, with the name, author, description and picture filled in for you
- Check GameBanana for newer versions of your mods
- Move mods between characters
- Edit a mod's key bindings
- Export (copy) your mods to another folder
- Turn on one random mod per character
- Import what JASM or XX-Mod-Manager already knows about your mods
- Fix a character yourself, or add one the pack doesn't have yet. Pack updates leave your changes alone.
- Pack Studio: make a Game Pack for a game nobody has made one for yet
- Nothing is ever deleted outright. Removed mods go to the trash and can be put back.
- A command line, `xxsm`, for everything the window does, when built from source (see the warning below)

## Hotkeys
- "F5" - Refresh the page, wherever it has a refresh button
- "CTRL + F" - Jump to the search box, in the character overview and character view
- "SPACE" - In character view, toggles selected mods on/off
- "DELETE" - In character view, moves selected mods to the trash
- "ESCAPE" - In character view, go back to the character overview
- "CTRL + V" - Paste a picture or a GameBanana link where the app asks for one
- "CTRL + Z" / "CTRL + Y" - Undo and redo in Pack Studio

## Download
Get the latest version from the [Releases](https://github.com/dotStray/XXSM/releases/latest) page.

- **Linux:** download the `.AppImage`, make it runnable (right-click → Properties → *Allow executing as program*, or
  `chmod +x` in a terminal), then open it.
- **Windows:** download the `.exe` and double-click it. See the warning below.

The downloads are the app itself; the command line is not included in them. To use it, build from source (below).

**Windows warning ⚠️**

Windows may show "Windows protected your PC" when you first run XXSM, and some antivirus programs may flag it. That's
because the app isn't code-signed: Microsoft charges a yearly fee for signing, which this project doesn't pay. Click
**More info** → **Run anyway** to start it, or build it yourself from source.

## Requirements
- Linux or Windows, 64-bit.
- XXMI, to actually load the mods into the game. XXSM only manages the files; it does not start the game or XXMI.

### Limitations and Acknowledgements
- **The command line (`xxsm`) is entirely untested by me.** I'm not technical, so I've only ever used the app's
  window. The command line is there, but use it at your own risk.
- There may be bugs. Make backups.
- Settings are stored in `~/.config/xxsm`, and packs and your character edits in `~/.local/share/xxsm`. On Windows,
  they are in `%APPDATA%\xxsm` and `%LOCALAPPDATA%\xxsm`.
- Mod specific details are stored in a `.xxsm` folder inside each mod, and profiles in a `.xxsm` folder inside the
  Mods folder, so they travel with your mods. When exporting mods, these can be left out.
- Inspired by [JASM](https://github.com/Jorixon/JASM), and it can import from JASM and
  [XX-Mod-Manager](https://github.com/XiaoLinXiaoZhu/XX-Mod-Manager).
- The default Game Packs are built from the model importer asset repositories; their sources are listed in
  [xxsm-presets](https://github.com/dotStray/xxsm-presets).
- Built with [Avalonia](https://avaloniaui.net/), [SharpCompress](https://github.com/adamhathcock/sharpcompress) for
  archives, [Serilog](https://serilog.net/) for logs and
  [System.CommandLine](https://github.com/dotnet/command-line-api) for the command line.

### Building from source
- Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Clone this repository.
- Inside the cloned folder, run `dotnet run --project src/Xxsm.Desktop -c Release` to start the app, or
  `dotnet run --project src/Xxsm.Cli -c Release -- --help` for the command line.
- `dotnet publish src/Xxsm.Desktop -c Release` builds it into `src/Xxsm.Desktop/bin/Release/net10.0/publish/`.

## Making your own Game Pack
Pack Studio, in the app, is the usual way to make one. If you want to build packs outside the app, the way
[xxsm-presets](https://github.com/dotStray/xxsm-presets) does, the format is described in
[`PRESET_SCHEMA.md`](PRESET_SCHEMA.md).

## Licence
[MIT](LICENSE). The libraries it uses and their licences are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
