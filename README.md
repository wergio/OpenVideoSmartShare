# OpenVideoSmartShare

A small Windows tray application that shares your video folders as a DLNA server and streams videos to an LG TV (or any DLNA renderer).

Made as a replacement for LG SmartShare, which I believe is no longer maintained.

## Features

- Video list of the shared folders (title, duration, subtitles, size, date, folder),
  with search and sorting.
- **Play on TV**: double-click a video to play it on the default TV (set in
  Settings → Devices); right-click to choose any TV found on the network. **Stop** ends
  the playback while a TV is streaming a video.
- **DLNA server** visible from the TV's media player, with "All videos" and the folder
  tree.
- **External subtitles**: `movie.srt` (also `.ass`, `.ssa`, `.smi`, `.sub`, `.psb`,
  or language-tagged names such as `movie.ita.srt`) next to `movie.mkv` is sent to the
  TV, in the same way as SmartShare.
- Folders are rescanned **automatically at startup** and with **Refresh**: unlike the
  original SmartShare, there is no separate database refresh to run by hand.
- Available in 23 languages (the Windows language by default, English as fallback).

## Download

Get the latest version from the [Releases page](https://github.com/wergio/OpenVideoSmartShare/releases/latest).
There are two options:

- **Installer** (`OpenVideoSmartShare-Setup-<version>.exe`, recommended). It installs for
  the current user, without admin rights, into `%LOCALAPPDATA%\Programs\OpenVideoSmartShare`.
  If the .NET runtimes are missing, it downloads them from Microsoft and installs them
  (this step asks for admin rights once). Run a newer installer to update.
- **Portable** (`OpenVideoSmartShare.exe`). Nothing to install: put the file in a folder
  of your choice and start it. To update, replace the file and keep its name. The firewall
  permission and the "Start with Windows" entry refer to the file's path.

The files are not code-signed yet, so Windows SmartScreen may show "Windows protected your
PC": choose **More info → Run anyway**.

## Requirements

- Windows 10 or 11, 64-bit.
- [.NET 10 Desktop Runtime and ASP.NET Core Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)
  (or a later version). The installer takes care of them. The portable exe opens
  Microsoft's download page if one is missing, one runtime at a time. To install both
  at once:

  ```powershell
  winget install Microsoft.DotNet.DesktopRuntime.10 Microsoft.DotNet.AspNetCore.10
  ```

## Replacing LG SmartShare

SmartShare starts through the scheduled task `\SmartShare`. Once you are happy with
OpenVideoSmartShare, uninstall SmartShare, or disable only its autostart from an
elevated prompt:

```powershell
schtasks /Change /TN "SmartShare" /Disable
```

## Network

- HTTP on TCP 52100 (the next free port if busy), SSDP on UDP 1900.
- The TV must reach the PC: the app checks Windows Firewall at startup and offers to
  add an inbound rule for its executable. It can be added later from
  Settings → DLNA Server Settings.

## Privacy

OpenVideoSmartShare does not send any information to the internet. It only talks to
devices on your local network: it announces its DLNA server, serves the shared videos to
the TVs that ask for them, and controls the TV you choose for playback.

---

## Building it yourself

Everything from here on is for those who want to build OpenVideoSmartShare from source or
work on the code. To simply use the app, [Download](#download) is all you need.

### Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); for the
installer also [Inno Setup 7](https://jrsoftware.org/isinfo.php).

```powershell
dotnet test
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Install
```

`publish.ps1` builds a single `OpenVideoSmartShare.exe` into `dist\`. Options:

- `-Version 1.2.0` stamps a version into the exe (default: the one in the csproj).
- `-Installer` also builds `dist\OpenVideoSmartShare-Setup-<version>.exe` with
  `tools\build-installer.ps1`, which looks for `ISCC.exe` in the `ISCC` environment
  variable, the `PATH` and the default Inno Setup folders.
- `-Install` closes a running instance (`OpenVideoSmartShare.exe --exit`) and copies the
  exe to `%LOCALAPPDATA%\Programs\OpenVideoSmartShare`, the folder the installer uses.


### Translations

Language files are generated, do not edit them by hand:

```powershell
python tools\l10n\build-languages.py
```

The sources are `tools/l10n/smartshare-strings.json` (the original SmartShare
translations) and `tools/l10n/manual.json` (new strings and corrections).


## Other Files

| What | Where |
|---|---|
| Settings | `%APPDATA%\OpenVideoSmartShare\settings.json` |
| Log (useful to see what the TV requests) | `%LOCALAPPDATA%\OpenVideoSmartShare\log.txt` |
| Library cache | `%LOCALAPPDATA%\OpenVideoSmartShare\library-cache.json` |
| Autostart | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\OpenVideoSmartShare` |

## License

[MIT](LICENSE).
