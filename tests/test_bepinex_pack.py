import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile


INSTALLER = Path(__file__).resolve().parent.parent / "docker" / "install-bepinex-pack.py"
SPEC = importlib.util.spec_from_file_location("install_bepinex_pack", INSTALLER)
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


def make_pack(path: Path, version: str) -> None:
    with ZipFile(path, "w") as archive:
        archive.writestr("manifest.json", f'{{"name":"BepInExPack_Valheim","version_number":"{version}"}}')
        archive.writestr("BepInExPack_Valheim/BepInEx/core/BepInEx.dll", version)
        archive.writestr("BepInExPack_Valheim/BepInEx/config/BepInEx.cfg", "pack config")
        archive.writestr("BepInExPack_Valheim/start_server_bepinex.sh", "#!/bin/sh\n")
        archive.writestr("BepInExPack_Valheim/doorstop_libs/libdoorstop_x64.so", version)


class BepInExPackInstallTests(unittest.TestCase):
    def test_upgrade_replaces_managed_files_and_preserves_mods_and_config(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            server = root / "server"
            old, new = root / "old.zip", root / "new.zip"
            make_pack(old, "5.4.2350")
            make_pack(new, "5.4.2351")
            original_replace = module.os.replace

            def reject_cross_volume(source, destination):
                source_on_bepinex = Path(source).is_relative_to(server / "BepInEx")
                destination_on_bepinex = Path(destination).is_relative_to(server / "BepInEx")
                if source_on_bepinex != destination_on_bepinex:
                    raise OSError("cross-volume rename")
                return original_replace(source, destination)

            with patch.object(module.os, "replace", side_effect=reject_cross_volume):
                module.install(old, server, "5.4.2350")
            plugin = server / "BepInEx/plugins/MyMod/plugin.dll"
            plugin.parent.mkdir(parents=True)
            plugin.write_text("keep plugin")
            config = server / "BepInEx/config/BepInEx.cfg"
            config.write_text("keep config")

            with patch.object(module.os, "replace", side_effect=reject_cross_volume):
                module.install(new, server, "5.4.2351")

            self.assertEqual("5.4.2351", (server / "BepInEx/.vsm-pack-version").read_text().strip())
            self.assertEqual("5.4.2351", (server / "BepInEx/core/BepInEx.dll").read_text())
            self.assertEqual("5.4.2351", (server / "doorstop_libs/libdoorstop_x64.so").read_text())
            self.assertEqual("keep plugin", plugin.read_text())
            self.assertEqual("keep config", config.read_text())

    def test_wrong_version_does_not_change_installation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            server = root / "server"
            old, wrong = root / "old.zip", root / "wrong.zip"
            make_pack(old, "5.4.2350")
            make_pack(wrong, "5.4.2352")
            module.install(old, server, "5.4.2350")

            with self.assertRaises(ValueError):
                module.install(wrong, server, "5.4.2351")

            self.assertEqual("5.4.2350", (server / "BepInEx/.vsm-pack-version").read_text().strip())
            self.assertEqual("5.4.2350", (server / "BepInEx/core/BepInEx.dll").read_text())

    def test_failed_upgrade_restores_previous_pack(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            server = root / "server"
            old, new = root / "old.zip", root / "new.zip"
            make_pack(old, "5.4.2350")
            make_pack(new, "5.4.2351")
            module.install(old, server, "5.4.2350")
            original_replace = module.os.replace
            failed = False

            def fail_once(source, destination):
                nonlocal failed
                if not failed and Path(destination) == server / "BepInEx/core":
                    failed = True
                    raise OSError("simulated copy failure")
                return original_replace(source, destination)

            with patch.object(module.os, "replace", side_effect=fail_once):
                with self.assertRaises(OSError):
                    module.install(new, server, "5.4.2351")

            self.assertEqual("5.4.2350", (server / "BepInEx/.vsm-pack-version").read_text().strip())
            self.assertEqual("5.4.2350", (server / "BepInEx/core/BepInEx.dll").read_text())
            self.assertEqual("5.4.2350", (server / "doorstop_libs/libdoorstop_x64.so").read_text())


if __name__ == "__main__":
    unittest.main()
