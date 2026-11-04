#!/usr/bin/env python3
"""Build a distributable archive for one runtime identifier.

Usage: python packaging/package.py --rid <rid> --version <x.y.z> [--out artifacts/release]

Publishes a self-contained single-file build and wraps it per OS:
  win-*    AzureWorkbench-<v>-<rid>.zip
  linux-*  AzureWorkbench-<v>-<rid>.tar.gz (with install.sh)
  osx-*    AzureWorkbench-<v>-<rid>.zip containing "Azure Workbench.app"
Standard library only.
"""
import argparse
import hashlib
import io
import plistlib
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PKG = ROOT / "packaging"
PROJECT = ROOT / "src" / "AzureWorkbench"
NOTICES = PROJECT / "ThirdPartyNotices.txt"
LICENSE = ROOT / "LICENSE"
APP_NAME = "Azure Workbench"
BUNDLE_ID = "io.github.azureworkbench"

COMMON_SIGNIN = """\
SIGNING IN
Azure Workbench needs an Azure account that can see your resources. Either:
  - sign in with the Azure CLI first (run `az login` in a terminal; the app
    then picks it up), or
  - choose browser sign-in inside the app.
"""

README = {
    "win": """\
Azure Workbench {v} (Windows, {rid})
====================================

RUNNING
Extract the whole zip to a folder (for example C:\\Apps\\AzureWorkbench) and
double-click AzureWorkbench.exe. It is self-contained: you do NOT need to install
.NET. Keep the .dll files in the same folder as AzureWorkbench.exe; it needs them.

""" + COMMON_SIGNIN + """
UNSIGNED BUILD WARNING
This build is not code-signed, so Windows SmartScreen may show "Windows
protected your PC". Click "More info", then "Run anyway". If you downloaded a
zip, you can also right-click the zip > Properties > tick "Unblock" before
extracting.

Licence: MIT (LICENSE). Third-party licences: ThirdPartyNotices.txt
""",
    "linux": """\
Azure Workbench {v} (Linux, {rid})
==================================

RUNNING
Either run ./AzureWorkbench directly, or run ./install.sh to install it for
your user (~/.local/opt/azure-workbench, a launcher in ~/.local/bin and an
application-menu entry). It is a single self-contained executable: you do NOT
need to install .NET.

""" + COMMON_SIGNIN + """
KEYRING NOTE
To remember browser sign-ins between runs, the app stores tokens in the system
keyring through libsecret (for example gnome-keyring or KWallet with the Secret
Service enabled). Without one, browser sign-in works but is forgotten when the
app closes; `az login` sign-in is unaffected.

UNSIGNED BUILD
This build is unsigned and unpackaged. If it does not start, make sure the file
is executable (chmod +x AzureWorkbench) and that a desktop session is running.

Licence: MIT (LICENSE). Third-party licences: ThirdPartyNotices.txt
""",
    "osx": """\
Azure Workbench {v} (macOS, {rid})
==================================

RUNNING
Drag "Azure Workbench.app" to your Applications folder and open it. It is
self-contained: you do NOT need to install .NET.

""" + COMMON_SIGNIN + """
UNSIGNED BUILD WARNING (Gatekeeper)
This app is not notarised, so macOS blocks it the first time ("Apple could not
verify ... is free of malware", or that it "is damaged"). To open it, do ONE of:
  - macOS 15 (Sequoia) and later: double-click the app and dismiss the warning
    (click Done, not Move to Trash). Open System Settings > Privacy & Security,
    scroll to Security, click "Open Anyway" next to "Azure Workbench", confirm
    with your password, then click Open.
  - macOS 14 (Sonoma) and earlier: Control-click (right-click) the app > Open > Open.
  - Any version, in Terminal, remove the download quarantine flag:
      xattr -dr com.apple.quarantine "/Applications/Azure Workbench.app"
    (adjust the path to wherever you put the app).
You only need to do this once per downloaded version.

Browser sign-in tokens are remembered in the macOS Keychain.

Licence: MIT (LICENSE). Third-party licences: ThirdPartyNotices.txt
""",
}

DESKTOP = """\
[Desktop Entry]
Type=Application
Name=Azure Workbench
Comment=Azure Service Bus and App Service workbench
Exec=@EXEC@
Icon=@ICON@
Terminal=false
Categories=Development;
StartupWMClass=AzureWorkbench
"""

INSTALL_SH = """\
#!/usr/bin/env bash
# Per-user install of Azure Workbench. Safe to run again (upgrades in place).
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${HOME}/.local"
OPT="$PREFIX/opt/azure-workbench"
BIN="$PREFIX/bin"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons"

mkdir -p "$OPT" "$BIN" "$APPS" "$ICONS"

install -m 0755 "$SRC/AzureWorkbench" "$OPT/AzureWorkbench"
for lib in "$SRC"/*.so; do
  if [ -e "$lib" ]; then install -m 0755 "$lib" "$OPT/"; fi
done
install -m 0644 "$SRC/ThirdPartyNotices.txt" "$OPT/ThirdPartyNotices.txt"
install -m 0644 "$SRC/AppIcon.png" "$ICONS/azure-workbench.png"
echo "Installed program:    $OPT/AzureWorkbench"

ln -sfn "$OPT/AzureWorkbench" "$BIN/azure-workbench"
echo "Linked launcher:      $BIN/azure-workbench"

sed -e "s|@EXEC@|$OPT/AzureWorkbench|" -e "s|@ICON@|$ICONS/azure-workbench.png|" \\
    "$SRC/azure-workbench.desktop" > "$APPS/azure-workbench.desktop"
chmod 0644 "$APPS/azure-workbench.desktop"
echo "Installed menu entry: $APPS/azure-workbench.desktop"
echo "Installed icon:       $ICONS/azure-workbench.png"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPS" >/dev/null 2>&1 || true
fi

case ":$PATH:" in
  *":$BIN:"*) ;;
  *) echo "Note: $BIN is not on your PATH; add it to run 'azure-workbench' from a terminal." ;;
esac
echo "Done. To uninstall, delete the files listed above."
"""


def run(cmd):
    print("+", " ".join(str(c) for c in cmd), flush=True)
    subprocess.run([str(c) for c in cmd], check=True, cwd=ROOT)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def readme(os_key, version, rid):
    return README[os_key].format(v=version, rid=rid)


def zip_add(zf, arcname, data=None, src=None, mode=0o644):
    info = zipfile.ZipInfo(arcname, date_time=time.localtime()[:6])
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3  # unix, so external_attr permissions are honoured
    info.external_attr = (0o100000 | mode) << 16
    if data is None:
        data = Path(src).read_bytes()
    elif isinstance(data, str):
        data = data.encode("utf-8")
    zf.writestr(info, data)


def exe_path(pub, rid):
    exe = pub / ("AzureWorkbench.exe" if rid.startswith("win-") else "AzureWorkbench")
    if not exe.is_file():
        sys.exit(f"error: expected {exe} after publish")
    return exe


def native_libs(pub):
    """Native libraries published next to the executable (Skia, HarfBuzz, ANGLE); they must ship beside it."""
    return sorted(p for p in pub.iterdir() if p.is_file() and p.suffix.lower() in (".dll", ".so", ".dylib"))


def notices_path(pub):
    p = pub / "ThirdPartyNotices.txt"
    return p if p.is_file() else NOTICES


def package_windows(pub, rid, version, archive):
    with zipfile.ZipFile(archive, "w") as zf:
        zip_add(zf, "AzureWorkbench.exe", src=exe_path(pub, rid), mode=0o755)
        for lib in native_libs(pub):
            zip_add(zf, lib.name, src=lib)
        zip_add(zf, "ThirdPartyNotices.txt", src=notices_path(pub))
        zip_add(zf, "LICENSE.txt", src=LICENSE)
        zip_add(zf, "README-FIRST.txt", data=readme("win", version, rid).replace("\n", "\r\n"))


def package_linux(pub, rid, version, archive):
    top = f"AzureWorkbench-{version}"
    now = time.time()

    def add_bytes(tf, name, data, mode):
        if isinstance(data, str):
            data = data.encode("utf-8")
        ti = tarfile.TarInfo(f"{top}/{name}")
        ti.size, ti.mode, ti.mtime = len(data), mode, now
        ti.uname = ti.gname = "root"
        tf.addfile(ti, io.BytesIO(data))

    with tarfile.open(archive, "w:gz") as tf:
        d = tarfile.TarInfo(top)
        d.type, d.mode, d.mtime = tarfile.DIRTYPE, 0o755, now
        tf.addfile(d)
        add_bytes(tf, "AzureWorkbench", exe_path(pub, rid).read_bytes(), 0o755)
        for lib in native_libs(pub):
            add_bytes(tf, lib.name, lib.read_bytes(), 0o755)
        add_bytes(tf, "ThirdPartyNotices.txt", notices_path(pub).read_bytes(), 0o644)
        add_bytes(tf, "LICENSE", LICENSE.read_bytes(), 0o644)
        add_bytes(tf, "AppIcon.png", (PKG / "AppIcon.png").read_bytes(), 0o644)
        # Placeholders stay in the template: install.sh fills in absolute paths so the menu entry works without ~/.local/bin on PATH.
        add_bytes(tf, "azure-workbench.desktop", DESKTOP, 0o644)
        add_bytes(tf, "install.sh", INSTALL_SH, 0o755)
        add_bytes(tf, "README-FIRST.txt", readme("linux", version, rid), 0o644)


def package_macos(pub, rid, version, archive):
    app = f"{APP_NAME}.app"
    plist = plistlib.dumps({
        "CFBundleName": APP_NAME,
        "CFBundleDisplayName": APP_NAME,
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleExecutable": "AzureWorkbench",
        "CFBundleIconFile": "AppIcon",
        "CFBundlePackageType": "APPL",
        "CFBundleInfoDictionaryVersion": "6.0",
        # macOS bundle versions must be numeric (e.g. 0.1.0); keep the full pre-release label in the info string.
        "CFBundleShortVersionString": version.split("-")[0].split("+")[0],
        "CFBundleVersion": version.split("-")[0].split("+")[0],
        "CFBundleGetInfoString": f"Azure Workbench {version}",
        "LSMinimumSystemVersion": "12.0",
        "NSHighResolutionCapable": True,
        "LSApplicationCategoryType": "public.app-category.developer-tools",
    })
    with zipfile.ZipFile(archive, "w") as zf:
        zip_add(zf, f"{app}/Contents/Info.plist", data=plist)
        zip_add(zf, f"{app}/Contents/MacOS/AzureWorkbench", src=exe_path(pub, rid), mode=0o755)
        for lib in native_libs(pub):
            zip_add(zf, f"{app}/Contents/MacOS/{lib.name}", src=lib, mode=0o755)
        zip_add(zf, f"{app}/Contents/Resources/AppIcon.icns", src=PKG / "AppIcon.icns")
        zip_add(zf, "ThirdPartyNotices.txt", src=notices_path(pub))
        zip_add(zf, "LICENSE.txt", src=LICENSE)
        zip_add(zf, "README-FIRST.txt", data=readme("osx", version, rid))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--rid", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--out", default="artifacts/release")
    a = ap.parse_args()

    os_key = a.rid.split("-")[0]
    if os_key not in ("win", "linux", "osx"):
        sys.exit(f"error: unsupported rid '{a.rid}'")

    out = Path(a.out)
    if not out.is_absolute():
        out = Path.cwd() / out
    out.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="azwb-pub-") as tmp:
        pub = Path(tmp) / "publish"
        run(["dotnet", "publish", PROJECT, "-c", "Release", "-r", a.rid,
             f"-p:Version={a.version}", "-o", pub])
        ext = "tar.gz" if os_key == "linux" else "zip"
        archive = out / f"AzureWorkbench-{a.version}-{a.rid}.{ext}"
        if archive.exists():
            archive.unlink()
        {"win": package_windows, "linux": package_linux, "osx": package_macos}[os_key](
            pub, a.rid, a.version, archive)

    print(f"Archive: {archive}")
    print(f"Size:    {archive.stat().st_size / (1024 * 1024):.1f} MiB")
    print(f"SHA-256: {sha256(archive)}")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as e:
        sys.exit(f"error: command failed with exit code {e.returncode}")
