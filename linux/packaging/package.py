#!/usr/bin/env python3
"""Build the portable tarball and Debian arm64 package from a published app."""
from __future__ import annotations

import os
import shutil
import tarfile
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PUBLISH = ROOT / "dist" / "Periphora-linux-arm64"
PACKAGING = ROOT / "linux" / "packaging"
OUT = ROOT / "dist"

APP_NAME = "Periphora"
PKG_NAME = "periphora"
VERSION = "1.1.0"
ARCH = "arm64"


# Files that must stay executable even when the build host has no POSIX
# permission bits to copy from (a Windows checkout, for instance).
EXECUTABLE_NAMES = {"Periphora", "install.sh", "postinst", "prerm"}


def set_mode(tarinfo: tarfile.TarInfo) -> tarfile.TarInfo:
    tarinfo.uid = 0
    tarinfo.gid = 0
    tarinfo.uname = "root"
    tarinfo.gname = "root"
    if tarinfo.isdir():
        tarinfo.mode = 0o755
    elif tarinfo.issym():
        # dpkg warns about symlinks that are not 0777.
        tarinfo.mode = 0o777
    elif tarinfo.isreg():
        name = os.path.basename(tarinfo.name)
        is_exec = name in EXECUTABLE_NAMES or name.endswith(".so")
        # Keep the caller's intent too: postinst/prerm are chmod 755 before being
        # packed, and dpkg refuses maintainer scripts that are not executable.
        if is_exec or tarinfo.mode & 0o111:
            tarinfo.mode = 0o755
        else:
            tarinfo.mode = 0o644
    return tarinfo


def build_portable() -> Path:
    target = OUT / "Periphora-linux-arm64.tar.gz"
    top = "Periphora-linux-arm64"
    with tarfile.open(target, "w:gz", format=tarfile.PAX_FORMAT) as archive:
        archive.add(PUBLISH, arcname=top, filter=set_mode)
        archive.add(PACKAGING / "install.sh", arcname=f"{top}/install.sh", filter=set_mode)
        archive.add(PACKAGING / "99-periphora-hidraw.rules",
                    arcname=f"{top}/99-periphora-hidraw.rules", filter=set_mode)
    return target


def tar_directory(source: Path, target: Path) -> None:
    with tarfile.open(target, "w:gz", format=tarfile.PAX_FORMAT) as archive:
        archive.add(source, arcname=".", filter=set_mode)


def ar_member(name: str, data: bytes, mode: int = 0o100644) -> bytes:
    header = (
        f"{name:<16}"
        f"{int(time.time()):<12}"
        f"{0:<6}"
        f"{0:<6}"
        f"{format(mode, 'o'):<8}"
        f"{len(data):<10}"
        "`\n"
    ).encode("ascii")
    payload = header + data
    if len(data) % 2:
        payload += b"\n"
    return payload


def write_control(stage: Path, installed_kb: int) -> None:
    control = (
        f"Package: {PKG_NAME}\n"
        f"Version: {VERSION}\n"
        "Section: utils\n"
        "Priority: optional\n"
        f"Architecture: {ARCH}\n"
        "Maintainer: Periphora contributors\n"
        "Installed-Size: " + str(installed_kb) + "\n"
        "Depends: libc6 (>= 2.28), libstdc++6, libx11-6, libxext6, libxrandr2, "
        "libxi6, libxcursor1, libice6, libsm6, libfontconfig1, libfreetype6, "
        "libxkbcommon0, libgl1, libssl1.1 | libssl3\n"
        "Description: Wireless peripheral battery monitor\n"
        " Shows battery levels for supported 2.4G wireless mice, keyboards and\n"
        " headsets in a compact tray panel. Uses Linux hidraw directly and does\n"
        " not need vendor software.\n"
    )
    (stage / "control").write_bytes(control.encode("utf-8"))
    postinst = """#!/bin/sh
set -e
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
    udevadm trigger --subsystem-match=hidraw || true
fi
exit 0
"""
    postinst_path = stage / "postinst"
    postinst_path.write_bytes(postinst.encode("utf-8"))
    postinst_path.chmod(0o755)
    prerm = """#!/bin/sh
set -e
if command -v udevadm >/dev/null 2>&1; then
    udevadm control --reload-rules || true
fi
exit 0
"""
    prerm_path = stage / "prerm"
    prerm_path.write_bytes(prerm.encode("utf-8"))
    prerm_path.chmod(0o755)


def build_deb() -> Path:
    with tempfile.TemporaryDirectory(prefix="periphora-deb-") as temp:
        temp_path = Path(temp)
        data_root = temp_path / "data"
        control_root = temp_path / "control"
        app_root = data_root / "usr" / "lib" / "periphora"
        app_root.mkdir(parents=True)

        for source in PUBLISH.iterdir():
            destination = app_root / source.name
            shutil.copy2(source, destination)
            if source.name == "Periphora" or source.suffix == ".so":
                destination.chmod(0o755)

        bin_root = data_root / "usr" / "bin"
        bin_root.mkdir(parents=True)
        launcher = bin_root / "periphora"
        try:
            os.symlink("../lib/periphora/Periphora", launcher)
        except (OSError, NotImplementedError):
            # Windows without Developer Mode cannot create symlinks. A two-line
            # wrapper keeps the package usable instead of failing the build.
            launcher.write_bytes(
                b"#!/bin/sh\nexec /usr/lib/periphora/Periphora \"$@\"\n"
            )
            launcher.chmod(0o755)

        desktop_root = data_root / "usr" / "share" / "applications"
        desktop_root.mkdir(parents=True)
        shutil.copy2(PACKAGING / "periphora.desktop", desktop_root / "periphora.desktop")

        icon_root = data_root / "usr" / "share" / "icons" / "hicolor" / "256x256" / "apps"
        icon_root.mkdir(parents=True)
        shutil.copy2(ROOT / "linux" / "Assets" / "periphora.png", icon_root / "periphora.png")

        rules_root = data_root / "usr" / "lib" / "udev" / "rules.d"
        rules_root.mkdir(parents=True)
        shutil.copy2(PACKAGING / "99-periphora-hidraw.rules",
                     rules_root / "99-periphora-hidraw.rules")

        control_root.mkdir(parents=True)
        installed_kb = sum(p.stat().st_size for p in data_root.rglob("*") if p.is_file()) // 1024
        write_control(control_root, installed_kb)

        control_tar = temp_path / "control.tar.gz"
        data_tar = temp_path / "data.tar.gz"
        tar_directory(control_root, control_tar)
        tar_directory(data_root, data_tar)

        binary = b"2.0\n"
        output = OUT / f"{PKG_NAME}_{VERSION}_{ARCH}.deb"
        output.write_bytes(
            b"!<arch>\n"
            + ar_member("debian-binary/", binary)
            + ar_member("control.tar.gz/", control_tar.read_bytes())
            + ar_member("data.tar.gz/", data_tar.read_bytes())
        )
        return output


def main() -> None:
    if not (PUBLISH / "Periphora").exists():
        raise SystemExit(f"Published app not found: {PUBLISH}")
    OUT.mkdir(parents=True, exist_ok=True)
    portable = build_portable()
    deb = build_deb()
    for path in (portable, deb):
        print(f"{path}  {path.stat().st_size / (1024 * 1024):.1f} MB")


if __name__ == "__main__":
    main()
