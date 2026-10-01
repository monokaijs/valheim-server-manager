"""Exercise registry publication guards without contacting or writing a registry."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "mirror-container.sh"
DIGEST = "sha256:" + "a" * 64
OTHER_DIGEST = "sha256:" + "b" * 64
MOCK_SKOPEO = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
log = pathlib.Path(os.environ["MOCK_LOG"])
history = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
with log.open("a") as out:
    out.write(json.dumps(args) + "\n")
if "inspect" in args:
    if args[-1].endswith(":latest"):
        copies = sum("copy" in call for call in history)
        print(os.environ.get("MOCK_LATEST_AFTER" if copies else "MOCK_LATEST", os.environ["IMAGE_DIGEST"]))
    else:
        print(os.environ.get("MOCK_VERSION", os.environ["RELEASE_VERSION"]))
elif "copy" in args:
    if os.environ.get("MOCK_COPY_FAIL"):
        sys.exit(42)
    pathlib.Path(args[args.index("--digestfile") + 1]).write_text(os.environ.get("MOCK_COPY_DIGEST", os.environ["IMAGE_DIGEST"]))
else:
    sys.exit(2)
'''


class MirrorContainerTests(unittest.TestCase):
    def run_mirror(self, **overrides):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "skopeo").write_text(MOCK_SKOPEO)
            (root / "skopeo").chmod(0o755)
            auth = root / "auth.json"
            auth.write_text("{}")  # No credential is created or used by these tests.
            env = dict(os.environ, PATH=directory + os.pathsep + os.environ["PATH"],
                       GITHUB_REPOSITORY="Example/Server", RELEASE_VERSION="2.7.3",
                       IMAGE_DIGEST=DIGEST,
                       UPDATE_LATEST="false", REGISTRY_AUTH_FILE=str(auth),
                       GITHUB_STEP_SUMMARY=str(root / "summary"), MOCK_LOG=str(root / "calls"))
            env.update(overrides)
            result = subprocess.run(["bash", str(SCRIPT)], env=env, capture_output=True, text=True)
            calls = [json.loads(line) for line in (root / "calls").read_text().splitlines()] if (root / "calls").exists() else []
            return result, calls

    def test_invalid_inputs_do_not_contact_registries(self):
        for overrides in [{"RELEASE_VERSION": "2.7.3; touch /tmp/unsafe"}, {"RELEASE_VERSION": "v2.7.3"},
                          {"IMAGE_DIGEST": "latest"}, {"IMAGE_DIGEST": "sha256:" + "A" * 64},
                          {"GITHUB_REPOSITORY": "../server"}, {"UPDATE_LATEST": "yes"},
                          {"REGISTRY_AUTH_FILE": "/definitely/missing/auth.json"}]:
            with self.subTest(overrides=overrides):
                result, calls = self.run_mirror(**overrides)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

    def test_mismatched_version_cannot_publish(self):
        result, calls = self.run_mirror(MOCK_VERSION="2.7.2")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any("copy" in call for call in calls))

    def test_environment_cannot_redirect_the_approved_destination(self):
        result, calls = self.run_mirror(DOCKERHUB_IMAGE="unapproved/other")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call[-1] for call in calls if "copy" in call],
                         ["docker://docker.io/monokaijs/valheim-server-manager:2.7.3",
                          "docker://docker.io/monokaijs/valheim-server-manager:v2.7.3"])

    def test_latest_mismatch_cannot_publish_any_tag(self):
        result, calls = self.run_mirror(UPDATE_LATEST="true", MOCK_LATEST=OTHER_DIGEST)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any("copy" in call for call in calls))

    def test_new_latest_during_mirror_is_not_overwritten(self):
        result, calls = self.run_mirror(UPDATE_LATEST="true", MOCK_LATEST_AFTER=OTHER_DIGEST)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual([call[-1].split(":")[-1] for call in calls if "copy" in call], ["2.7.3", "v2.7.3"])

    def test_copy_failure_and_changed_digest_stop_remaining_tags(self):
        for overrides in [{"MOCK_COPY_FAIL": "true"}, {"MOCK_COPY_DIGEST": OTHER_DIGEST}]:
            with self.subTest(overrides=overrides):
                result, calls = self.run_mirror(**overrides)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(sum("copy" in call for call in calls), 1)

    def test_pinned_index_and_expected_tags_are_preserved_on_repeat(self):
        for latest, tags in [("false", ["2.7.3", "v2.7.3"]), ("true", ["2.7.3", "v2.7.3", "latest"])]:
            for repeat in range(2):
                with self.subTest(latest=latest, repeat=repeat):
                    result, calls = self.run_mirror(UPDATE_LATEST=latest)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    copies = [call for call in calls if "copy" in call]
                    self.assertEqual([call[-1] for call in copies], ["docker://docker.io/monokaijs/valheim-server-manager:" + tag for tag in tags])
                    for call in copies:
                        self.assertEqual(call[-2], "docker://ghcr.io/example/server@" + DIGEST)
                        for flag in ["--all", "--preserve-digests", "--src-no-creds", "--dest-authfile"]:
                            self.assertIn(flag, call)
                        self.assertNotIn("--dest-creds", call)


if __name__ == "__main__":
    unittest.main()
