"""Builds src/OpenVideoSmartShare/Localization/Languages/<code>.json.

Each string comes from manual.json when present (own translations and corrections),
otherwise from smartshare-strings.json (base strings).
Usage: python tools/l10n/build-languages.py
"""
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE.parent.parent / "src" / "OpenVideoSmartShare" / "Localization" / "Languages"

# Key order of the generated files (and the full set every language must provide).
KEYS = [
    "tray.open", "tray.exit", "tray.stillRunning",
    "main.refresh", "main.playTo", "main.play", "main.stop", "main.notFound",
    "main.settings", "main.search", "main.searchHint",
    "col.title", "col.duration", "col.subtitles", "col.size", "col.resolution", "col.date", "col.folder",
    "ctx.playOnDefault", "ctx.openFolder",
    "status.server", "status.serverError", "status.videos", "status.scanning", "status.updatedAt",
    "status.sending", "status.sent", "status.unreachable",
    "msg.noTv", "msg.defaultTvMissing", "msg.playError", "msg.stopError", "msg.noFolders",
    "list.empty", "list.noMatch",
    "settings.title", "settings.tabShared", "settings.tabServer", "settings.tabDevices", "settings.tabGeneral",
    "settings.add", "settings.remove", "settings.refresh", "settings.colFolder", "settings.includeSubfolders",
    "settings.yes", "settings.no", "settings.addFolderMsg", "settings.removeFolderConfirm",
    "settings.folderAlreadyShared", "settings.serverName", "settings.deviceNameHint", "settings.deviceNameInvalid",
    "settings.language", "settings.languageAuto", "settings.startWithWindows", "settings.serverAddress",
    "settings.defaultTv", "settings.defaultTvHint", "settings.defaultTvNone", "settings.accessTitle",
    "settings.allowAll", "settings.blockAll", "settings.accessHint", "settings.allowed", "settings.blocked",
    "settings.colDevice", "settings.colIp", "settings.colMac", "settings.colStatus", "settings.saveChanges",
    "settings.firewall", "settings.firewallAllowed", "settings.firewallMissing", "settings.firewallOff",
    "settings.firewallUnknown", "settings.firewallAdd",
    "firewall.prompt", "firewall.failed",
    "ok", "cancel", "apply",
    "dlna.allVideos",
]


def main() -> int:
    manual = json.loads((HERE / "manual.json").read_text(encoding="utf-8"))
    base = json.loads((HERE / "smartshare-strings.json").read_text(encoding="utf-8"))["strings"]
    OUT.mkdir(parents=True, exist_ok=True)
    errors = []
    for code in sorted(base):
        table = {}
        for key in KEYS:
            own = manual.get(code, {}).get(key)
            value = own or base[code].get(key)
            if not value:
                errors.append(f"{code}: missing {key}")
                continue
            # Base strings sometimes contain stray spaces or line breaks;
            # line breaks in our own strings are intentional.
            lines = value.split("\n") if own else [value]
            table[key] = "\n".join(" ".join(line.split()) for line in lines)
        (OUT / f"{code}.json").write_text(json.dumps(table, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for error in errors:
        print(error, file=sys.stderr)
    print(f"Written {len(base)} languages to {OUT}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
