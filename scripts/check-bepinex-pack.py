#!/usr/bin/env python3
"""Keep the published dependency aligned with the bundled pack baseline."""

import argparse
from pathlib import Path
import re
import tomllib
from urllib.request import Request, urlopen
import json


ROOT = Path(__file__).resolve().parent.parent
PACKAGE_URL = "https://thunderstore.io/api/experimental/package/denikson/BepInExPack_Valheim/"


def version(value: str) -> tuple[int, int, int]:
    if not re.fullmatch(r"\d+\.\d+\.\d+", value):
        raise ValueError(f"Invalid BepInExPack version: {value}")
    return tuple(map(int, value.split(".")))


def check(latest: bool) -> None:
    pinned = (ROOT / "bepinex-pack.version").read_text(encoding="utf-8").strip()
    version(pinned)
    manifest = tomllib.loads((ROOT / "thunderstore.toml").read_text(encoding="utf-8"))
    published = manifest["package"]["dependencies"]["denikson-BepInExPack_Valheim"]
    if pinned != published:
        raise ValueError(f"Thunderstore declares BepInExPack {published}; expected {pinned}")
    if latest:
        request = Request(PACKAGE_URL, headers={"User-Agent": "ValheimServerManager-BepInExPack-check"})
        with urlopen(request, timeout=15) as response:
            available = json.load(response)["latest"]["version_number"]
        if version(pinned) < version(available):
            raise ValueError(f"BepInExPack {available} is available; update bepinex-pack.version and thunderstore.toml before publishing")
    print(f"BepInExPack baseline {pinned} is aligned")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--latest", action="store_true", help="also check Thunderstore's latest release")
    check(parser.parse_args().latest)
