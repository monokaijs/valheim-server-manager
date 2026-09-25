#!/usr/bin/env python3
"""Install managed BepInExPack files without touching mod plugins or config."""

import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import sys
import tempfile
from zipfile import ZipFile


PREFIX = "BepInExPack_Valheim/"
MAX_UNPACKED_BYTES = 100 * 1024 * 1024


def remove(path: Path) -> None:
    if path.is_dir() and not path.is_symlink():
        shutil.rmtree(path)
    elif path.exists() or path.is_symlink():
        path.unlink()


def install(archive: Path, server: Path, version: str) -> None:
    server.mkdir(parents=True, exist_ok=True)
    bepinex = server / "BepInEx"
    bepinex.mkdir(parents=True, exist_ok=True)
    workspace = Path(tempfile.mkdtemp(prefix=".vsm-bepinex-", dir=server))
    bepinex_workspace = Path(tempfile.mkdtemp(prefix=".vsm-bepinex-", dir=bepinex))
    replaced: list[tuple[Path, Path | None]] = []
    retain_workspace = False
    try:
        with ZipFile(archive) as package:
            manifest = json.loads(package.read("manifest.json"))
            if manifest.get("name") != "BepInExPack_Valheim" or manifest.get("version_number") != version:
                raise ValueError("Downloaded BepInExPack does not match the requested version")
            total = 0
            for member in package.infolist():
                if not member.filename.startswith(PREFIX):
                    continue
                relative = PurePosixPath(member.filename[len(PREFIX):])
                if relative.is_absolute() or ".." in relative.parts or "\\" in member.filename:
                    raise ValueError("Unsafe path in BepInExPack archive")
                if stat.S_IFMT(member.external_attr >> 16) == stat.S_IFLNK:
                    raise ValueError("BepInExPack archive contains a symbolic link")
                if member.is_dir() or str(relative) == ".":
                    continue
                total += member.file_size
                if total > MAX_UNPACKED_BYTES:
                    raise ValueError("BepInExPack archive is too large")
                destination = workspace / "new" / Path(*relative.parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
                with package.open(member) as source, destination.open("wb") as target:
                    shutil.copyfileobj(source, target)
                mode = (member.external_attr >> 16) & 0o777
                if mode:
                    destination.chmod(mode)

        source = workspace / "new"
        if not all((source / path).is_file() for path in (
            "BepInEx/core/BepInEx.dll", "start_server_bepinex.sh", "doorstop_libs/libdoorstop_x64.so"
        )):
            raise ValueError("BepInExPack archive is missing required server files")

        def replace(new: Path, destination: Path) -> None:
            nonlocal retain_workspace
            destination.parent.mkdir(parents=True, exist_ok=True)
            backup = None
            if destination.exists() or destination.is_symlink():
                volume_workspace = bepinex_workspace if destination.is_relative_to(bepinex) else workspace
                backup = volume_workspace / "old" / str(len(replaced))
                backup.parent.mkdir(parents=True, exist_ok=True)
                os.replace(destination, backup)
            try:
                os.replace(new, destination)
            except BaseException:
                if backup is not None:
                    try:
                        os.replace(backup, destination)
                    except BaseException:
                        retain_workspace = True
                        raise
                raise
            replaced.append((destination, backup))

        for entry in sorted(source.iterdir()):
            if entry.name != "BepInEx":
                replace(entry, server / entry.name)
        staged_core = bepinex_workspace / "new-core"
        shutil.copytree(source / "BepInEx" / "core", staged_core)
        replace(staged_core, bepinex / "core")
        config_source = source / "BepInEx" / "config"
        if config_source.is_dir():
            for entry in sorted(config_source.rglob("*")):
                if entry.is_file():
                    destination = bepinex / "config" / entry.relative_to(config_source)
                    if not destination.exists():
                        staged_config = bepinex_workspace / "new-config" / entry.relative_to(config_source)
                        staged_config.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(entry, staged_config)
                        replace(staged_config, destination)
        marker = bepinex_workspace / "version"
        marker.write_text(version + "\n", encoding="utf-8")
        replace(marker, bepinex / ".vsm-pack-version")
    except BaseException:
        try:
            for destination, backup in reversed(replaced):
                remove(destination)
                if backup is not None:
                    os.replace(backup, destination)
        except BaseException:
            retain_workspace = True
            raise
        raise
    finally:
        if retain_workspace:
            print(f"BepInExPack rollback needs manual recovery from {workspace} and {bepinex_workspace}", file=sys.stderr)
        else:
            shutil.rmtree(workspace, ignore_errors=True)
            shutil.rmtree(bepinex_workspace, ignore_errors=True)


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("Usage: install-bepinex-pack.py <archive.zip> <server-dir> <version>")
    install(Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3])
