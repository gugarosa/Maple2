import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
import uuid
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("release", ROOT / "deploy" / "azure" / "release.py")
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)
REVISION = "a" * 40
SOURCE = "b" * 64


def tar_write(path, files):
    with tarfile.open(path, "w:gz" if path.name.endswith(".gz") else "w") as archive:
        for name, data in files.items():
            data = data.encode() if isinstance(data, str) else data
            member = tarfile.TarInfo(name)
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))


def image_archive(path, oci=True, source=SOURCE, revision=REVISION):
    files, entries, descriptors, expected = {}, [], [], {}
    references = {}
    for role in release.APP_ROLES:
        config = release.json_bytes({
            "architecture": "amd64", "os": "linux",
            "config": {"Labels": {
                "org.mapletime.source.sha256": source,
                "org.opencontainers.image.revision": revision,
                "fixture.role": role,
            }, "Env": []},
        })
        config_hash = hashlib.sha256(config).hexdigest()
        files[config_hash + ".json"] = config
        identity = "sha256:" + config_hash
        if oci:
            manifest = release.json_bytes({"config": {"digest": identity}})
            manifest_hash = hashlib.sha256(manifest).hexdigest()
            files["blobs/sha256/" + manifest_hash] = manifest
            identity = "sha256:" + manifest_hash
        reference = f"maple2/ci-{role}:{REVISION}-" + identity.split(":")[1]
        references[role] = reference
        entries.append({"Config": config_hash + ".json", "RepoTags": [reference], "Layers": []})
        if oci:
            descriptors.append({"digest": identity, "annotations": {"io.containerd.image.name": "docker.io/" + reference}})
        expected[role] = {"reference": reference, "id": identity, "configId": "sha256:" + config_hash}
    files["manifest.json"] = release.json_bytes(entries)
    if oci:
        files["index.json"] = release.json_bytes({"manifests": descriptors})
    tar_write(path, files)
    return references, expected


def package_fixture(directory, model="public class Model {}", game_model="public class SkillBook {}"):
    directory.mkdir()
    (directory / "source").mkdir()
    tar_write(directory / "source" / "server-source.tar.gz", {
        "Maple2.Server.World/Migrations/001.cs": model,
        "Maple2.Database/Context/Game.cs": "context",
        "Maple2.Model/Game/SkillBook.cs": game_model,
        "Maple2.Server.Game/Runtime.cs": "runtime",
    })
    source_hash = release.digest(directory / "source" / "server-source.tar.gz")
    _, images = image_archive(directory / "images.tar.gz", source=source_hash)
    for name in release.PACKAGE_FILES:
        (directory / name).write_text("fixture")
    package = {
        "formatVersion": 1, "configuration": "Release", "uncommittedSource": False,
        "release": "20260912-" + source_hash[:12] + "-12345678",
        "gitCommit": REVISION, "sourceSha256": source_hash, "images": images,
        "imageArchiveBytes": 10240,
        "imageStorageBytes": 10240,
        "files": {path.relative_to(directory).as_posix(): release.digest(path)
                  for path in directory.rglob("*") if path.is_file()},
    }
    package["fileSizes"] = {name: directory.joinpath(*name.split("/")).stat().st_size for name in package["files"]}
    (directory / "package.json").write_bytes(release.json_bytes(package))
    return package, release.digest(directory / "package.json")


def compose_fixture(directory):
    config = {"volumes": {"mysql": {"name": "maple2-azure_mysql"}}, "services": {}}
    for name in ("mysql", "world", "login", "web", "game-ch0", "game-ch1", "proxy"):
        config["services"][name] = {
            "mem_limit": 128 * 1024 * 1024,
            "environment": {"DB_PASSWORD": "synthetic", "GAME_DB_NAME": "game-server"},
            "ports": [{"host_ip": "127.0.0.1", "published": "20001", "target": 20001}],
            "volumes": [{"type": "bind", "source": str(directory / "config.yaml"),
                         "target": "/app/config.yaml", "read_only": True}],
        }
    return config


class ReleaseTests(unittest.TestCase):
    def test_paths_and_source_allowlist(self):
        for name in ("../secret", "/etc/passwd", "source/../.env", "source\\other", "a//b"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                release.safe_name(name)
        self.assertFalse(release.source_file(".env"))
        self.assertFalse(release.source_file("client/MapleStory2.exe"))
        self.assertFalse(release.source_file("Maple2.Server.Game/Navmeshes"))
        self.assertTrue(release.source_file("Maple2.Server.Web/Data/system/banner.png"))
        self.assertTrue(release.source_file("website/theme.css"))
        for name in ("website/index.html", "website/styles.css", "website/ms2-logo.png",
                     "website/ms2-logo.webp", "website/.env", ".impeccable/design.json",
                     "tools/WebsitePreview/Program.cs"):
            with self.subTest(name=name):
                self.assertFalse(release.source_file(name))
        for name in ("Maple2.Server.Web/.env", "Maple2.Server.Web/Data/profile/avatar.png",
                     "Maple2.Tools/key.pfx", "Maple2.File.Ingest/client.m2d", "Maple2.Model/bin/private.dll"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                release.source_file(name)

    def test_clean_source_and_archive_contents(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ("LICENSE", "global.json", ".env", "MapleStory2.exe"):
                (root / name).write_text("fixture")
            tracked = b"100644 abc 0\tLICENSE\0" + b"100644 def 0\tglobal.json\0" + b"100644 bad 0\t.env\0"
            with patch.object(release, "run", side_effect=[REVISION + "\n", "", tracked]):
                release.make_source(root, REVISION, root / "source.tar.gz")
            with tarfile.open(root / "source.tar.gz") as archive:
                self.assertEqual(set(archive.getnames()), {"LICENSE", "global.json", "Maple2.Server.Game/Navmeshes"})
            with patch.object(release, "run", side_effect=[REVISION + "\n", " M file"]):
                with self.assertRaisesRegex(ValueError, "clean committed"):
                    release.make_source(root, REVISION, root / "dirty.tar.gz")
            self.assertFalse((root / "dirty.tar.gz").exists())
            with patch.object(release, "run", return_value="c" * 40):
                with self.assertRaisesRegex(ValueError, "requested revision"):
                    release.make_source(root, REVISION, root / "wrong.tar.gz")

    def test_complete_builder_preserves_snapshot_and_immutable_tags(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo, output = root / "repo", root / "output"
            names = set(release.PACKAGE_FILES.values()) | {"LICENSE", "global.json"}
            for name in names:
                path = repo.joinpath(*name.split("/"))
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b"committed fixture\r\n")
            saved = root / "saved-images.tar"
            expected = {}

            def command(arguments, **kwargs):
                if arguments[0] == "git":
                    if "rev-parse" in arguments:
                        return REVISION + "\n"
                    if "status" in arguments:
                        return ""
                    return "".join("100644 abc 0\t" + name + "\0" for name in sorted(names)).encode()
                if arguments[1] == "build":
                    self.assertIn("BUILD_CONFIGURATION=Release", arguments)
                    self.assertIn("--progress=plain", arguments)
                    self.assertNotIn("--quiet", arguments)
                    source_hash = release.digest(output / "source" / "server-source.tar.gz")
                    _, values = image_archive(saved, source=source_hash)
                    expected.update(values)
                    return b""
                if arguments[1:3] == ["image", "inspect"]:
                    if arguments[-1] == "{{.Size}}":
                        return "1024"
                    role = arguments[3].split("ci-", 1)[1].split(":", 1)[0]
                    return expected[role]["id"]
                if arguments[1] == "save":
                    shutil.copyfile(saved, arguments[arguments.index("--output") + 1])
                return b""

            with patch.object(release, "run", side_effect=command), patch("sys.stdout", new=io.StringIO()) as console:
                release.build(repo, output, REVISION)
            metadata = json.loads(console.getvalue())
            package = release.validate_package(output, REVISION, metadata["packageSha256"])
            self.assertEqual(package["images"], expected)
            self.assertEqual(package["imageStorageBytes"], 4096)
            for name in release.PACKAGE_FILES:
                self.assertEqual((output / name).read_bytes(), b"committed fixture\n")
            with self.assertRaisesRegex(ValueError, "overwrite"):
                release.build(repo, output, REVISION)

    def test_real_web_embedded_resources_survive_filtered_source(self):
        project = ROOT / "Maple2.Server.Web" / "Maple2.Server.Web.csproj"
        names = {"LICENSE", "global.json", project.relative_to(ROOT).as_posix()}
        resources = set()
        for item in ET.parse(project).iter("EmbeddedResource"):
            resource = (project.parent / item.attrib["Include"].replace("\\", "/")).resolve()
            name = resource.relative_to(ROOT).as_posix()
            self.assertTrue(resource.is_file(), name)
            self.assertTrue(release.source_file(name), f"Embedded build resource excluded from source: {name}")
            resources.add(name)
        self.assertIn("website/theme.css", resources)
        names.update(resources)
        excluded = {"website/index.html", "website/ms2-logo.png", ".impeccable/design.json"}
        tracked = "".join(f"100644 abc 0\t{name}\0" for name in sorted(names | excluded)).encode()
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "source.tar.gz"
            with patch.object(release, "run", side_effect=[REVISION + "\n", "", tracked]):
                release.make_source(ROOT, REVISION, output)
            with tarfile.open(output) as archive:
                self.assertEqual(set(archive.getnames()), names | {"Maple2.Server.Game/Navmeshes"})
                for name in resources:
                    self.assertEqual(archive.extractfile(name).read(), (ROOT / name).read_bytes())
            missing = "".join(f"100644 abc 0\t{name}\0" for name in sorted(names - resources)).encode()
            with patch.object(release, "run", side_effect=[REVISION + "\n", "", missing]):
                with self.assertRaisesRegex(ValueError, "Missing shared Web build input"):
                    release.make_source(ROOT, REVISION, Path(temporary) / "missing.tar.gz")

    def test_compatibility_normalizes_text_but_not_runtime_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tar_write(root / "before.gz", {
                "Maple2.Server.World/Migrations/001.cs": b"\xef\xbb\xbfline\r\n",
                "Maple2.Server.Game/Runtime.cs": "old runtime",
            })
            tar_write(root / "after.gz", {
                "Maple2.Server.World/Migrations/001.cs": "line\n",
                "Maple2.Server.Game/Runtime.cs": "new runtime",
            })
            self.assertEqual(release.data_contract(root / "before.gz"), release.data_contract(root / "after.gz"))
            tar_write(root / "changed.gz", {"Maple2.Server.World/Migrations/001.cs": "changed schema"})
            self.assertNotEqual(release.data_contract(root / "before.gz"), release.data_contract(root / "changed.gz"))

    def test_owned_and_json_model_types_are_in_the_data_contract(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            files = {
                "Maple2.Server.World/Migrations/001.cs": "unchanged migration",
                "Maple2.Model/Game/SkillBook.cs": "public int MaxSkillTabs { get; set; }",
                "Maple2.Model/Common/Stats.cs": "public int Value { get; set; }",
                "Maple2.Model/Enum/EquipSlot.cs": "enum EquipSlot { Head = 1 }",
            }
            tar_write(root / "before.gz", files)
            for name in files:
                if not name.startswith("Maple2.Model/"):
                    continue
                tar_write(root / "after.gz", {**files, name: files[name] + "\npublic int Added { get; set; }"})
                with self.subTest(name=name):
                    self.assertNotEqual(release.data_contract(root / "before.gz"), release.data_contract(root / "after.gz"))

    def test_saved_image_formats_and_source_provenance(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "images.tar.gz"
            for oci in (False, True):
                references, expected = image_archive(path, oci=oci)
                self.assertEqual(release.image_manifest(path, references, SOURCE, REVISION), expected)
                with self.assertRaisesRegex(ValueError, "provenance"):
                    release.image_manifest(path, references, "d" * 64, REVISION)
                with self.assertRaisesRegex(ValueError, "provenance"):
                    release.image_manifest(path, references, SOURCE, "d" * 40)

    def test_rebuilding_the_same_commit_cannot_replace_rollback_image_tags(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first, _ = image_archive(root / "first.tar.gz", source=SOURCE)
            second, _ = image_archive(root / "second.tar.gz", source="c" * 64)
            self.assertTrue(all(first[role] != second[role] for role in release.APP_ROLES))

    def test_package_fingerprints_and_allowlist(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / "package"
            package, fingerprint = package_fixture(root)
            self.assertEqual(release.validate_package(root, REVISION, fingerprint), package)
            with self.assertRaisesRegex(ValueError, "trusted workflow"):
                release.validate_package(root, "c" * 40, fingerprint)
            with self.assertRaisesRegex(ValueError, "fingerprint"):
                release.validate_package(root, REVISION, "c" * 64)
            (root / "config.yaml").write_text("tampered")
            with self.assertRaisesRegex(ValueError, "Artifact changed"):
                release.validate_package(root, REVISION, fingerprint)

    def test_retained_resources_and_memory_budget(self):
        before, after = Path("previous"), Path("candidate")
        first, second = compose_fixture(before), compose_fixture(after)
        self.assertEqual(release.retained_compose(first, before), release.retained_compose(second, after))
        for service in second["services"].values():
            service["mem_limit"] = str(service["mem_limit"])
        self.assertEqual(release.retained_compose(first, before), release.retained_compose(second, after))
        for invalid in (None, False, 0, -1, "unlimited", "0"):
            second["services"]["web"]["mem_limit"] = invalid
            with self.subTest(memory=invalid), self.assertRaisesRegex(ValueError, "memory limit"):
                release.retained_compose(second, after)
        second["services"]["web"]["mem_limit"] = str(128 * 1024 * 1024)
        second["services"]["web"]["privileged"] = True
        self.assertNotEqual(release.retained_compose(first, before), release.retained_compose(second, after))
        del second["services"]["web"]["privileged"]
        second["services"]["web"]["ports"][0]["host_ip"] = "0.0.0.0"
        self.assertNotEqual(release.retained_compose(first, before), release.retained_compose(second, after))
        second["services"]["web"]["volumes"][0]["read_only"] = False
        with self.assertRaisesRegex(ValueError, "writable"):
            release.retained_compose(second, after)
        second = compose_fixture(after)
        second["services"]["world"]["mem_limit"] = 4 * 1024**3
        with self.assertRaisesRegex(ValueError, "memory budget"):
            release.retained_compose(second, after)

    def test_prepare_preserves_player_configuration_and_blocks_schema_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            previous, directory = root / "previous", root / "candidate"
            package, fingerprint = package_fixture(directory)
            previous.mkdir()
            (previous / "source").mkdir()
            (previous / "source" / "server-source.tar.gz").write_bytes(
                (directory / "source" / "server-source.tar.gz").read_bytes())
            current = {
                "subscription": release.SUBSCRIPTION, "storageAccount": release.STORAGE,
                "sourceSha256": package["sourceSha256"], "files": {}, "vendorImages": {},
                "images": {role: {"reference": "vendor/" + role, "id": "a", "configId": "b"}
                           for role in release.VENDOR_ROLES},
            }
            for name in release.DATA_FILES:
                (previous / name).write_text("retained static artifact")
                current["files"][name] = release.digest(previous / name)
            (previous / "release.json").write_bytes(release.json_bytes(current))
            env = "DB_PASSWORD=synthetic-private\nPUBLIC_IP=192.0.2.10\nGAME_IMAGE=old\n"
            (previous / ".env").write_text(env)
            configs = [release.json_bytes(compose_fixture(previous)), release.json_bytes(compose_fixture(directory))]
            with patch.object(release, "run", side_effect=configs):
                release.prepare(directory, previous, REVISION, fingerprint)
            self.assertEqual((previous / ".env").read_text(), env)
            self.assertIn("DB_PASSWORD=synthetic-private", (directory / ".env").read_text())
            self.assertIn("GAME_IMAGE=maple2/ci-game:" + REVISION, (directory / ".env").read_text())
            self.assertEqual(release.read_json(directory / "release.json")["previousRelease"], previous.name)
            for name in release.DATA_FILES:
                self.assertEqual(release.digest(previous / name), release.digest(directory / name))
            changed = root / "changed"
            _, changed_hash = package_fixture(changed, model="changed schema")
            with self.assertRaisesRegex(ValueError, "Schema/metadata changes"):
                release.prepare(changed, previous, REVISION, changed_hash)
            self.assertFalse((changed / ".env").exists())
            owned = root / "changed-owned"
            _, owned_hash = package_fixture(owned, game_model="public class SkillBook { public int Added { get; set; } }")
            with self.assertRaisesRegex(ValueError, "Schema/metadata changes"):
                release.prepare(owned, previous, REVISION, owned_hash)
            self.assertFalse((owned / ".env").exists())

    def test_azure_wrapper_never_accepts_success_shaped_failure(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            package = {"release": "20260912-" + "b" * 12 + "-12345678",
                       "files": {"deploy-release.sh": "c" * 64}}
            (root / "deploy-release.sh").write_text("fixture")
            (root / "package.json").write_text("{}")
            calls = [
                {"tags": {"project": "maple2", "application": "private-pilot"}},
                {"exists": False}, {"exists": False},
                {"object": {"sha": REVISION}},
                {"value": [{"message": "Enable succeeded\nMS2_ROLLBACK_VERIFIED previous"}]},
            ]
            with patch.object(release, "validate_package", return_value=package), \
                    patch.object(release, "run", return_value=b""), \
                    patch.object(release, "read_json_output", side_effect=calls), \
                    patch("sys.stderr", new=io.StringIO()):
                with self.assertRaisesRegex(ValueError, "verified deployment success"):
                    release.deliver(root, REVISION, SOURCE)

    def test_workflow_trust_and_release_boundaries(self):
        workflow = (ROOT / ".github" / "workflows" / "deploy.yml").read_text()
        for marker in ("branches: [\"master\"]", "needs: [tests, formatting]", "cancel-in-progress: false",
                       "github.repository == 'gugarosa/Maple2'", "github.ref == 'refs/heads/master'",
                       "vars.MS2_CD_ENABLED == 'true'", "environment: maple2-pilot", "id-token: write",
                       "persist-credentials: false", "--federated-token", "retention-days: 1"):
            self.assertIn(marker, workflow)
        self.assertNotIn("pull_request_target", workflow)
        self.assertNotIn("CLIENT_SECRET", workflow)
        deployment = (ROOT / "deploy" / "azure" / "deploy-release.sh").read_text()
        self.assertLess(deployment.index("release.py prepare"), deployment.index("apps_touched=true\n"))
        self.assertLess(deployment.index("./backup-application.sh --deployment"), deployment.index("./start-application.sh\n"))
        self.assertLess(deployment.index("release.py probe --directory \"$directory\""),
                        deployment.index("ln -sfn \"$directory\" /srv/maple2/current"))
        for marker in ("flock -n 9", "MS2_ROLLBACK_VERIFIED", "MS2_ROLLBACK_FAILED", "trap cleanup EXIT"):
            self.assertIn(marker, deployment)
        self.assertNotIn("docker volume rm", deployment)
        self.assertNotIn("./efbundle", deployment)
        self.assertNotIn("--entrypoint /migrations/efbundle", deployment)
        self.assertNotIn("mysql --", deployment)


@unittest.skipUnless(os.name == "posix" and shutil.which("bash") and shutil.which("jq"),
                     "Linux bash/jq recovery checks run in the deployment-contracts CI job.")
class DeploymentControlFlowTests(unittest.TestCase):
    def test_azure_wrapper_reexecutes_bash_when_started_by_sh(self):
        with tempfile.TemporaryDirectory(prefix="maple2-shell-check-") as temporary:
            root = Path(temporary)
            commands = root / "bin"
            commands.mkdir()
            deployment = root / "deploy-release.sh"
            deployment.write_text(
                '#!/bin/bash\nset -Eeuo pipefail\n[[ "$#" == 4 ]]\n'
                'echo "MS2_RELEASE_DEPLOYED $4 $2 $3"\n'
            )
            (root / "package.json").write_text("{}")
            package = {
                "release": "20260912-" + "b" * 12 + "-12345678",
                "files": {"deploy-release.sh": release.digest(deployment)},
            }
            shim = commands / "az"
            shim.write_text(
                '#!/bin/bash\nset -e\n[[ "$1" != login ]] || exit 0\n'
                'while [[ "$#" -gt 0 ]]; do\n'
                '  if [[ "$1" == --file ]]; then output=$2; shift; fi\n'
                '  shift\ndone\ncp -- "$MS2_SCRIPT_FIXTURE" "$output"\n'
            )
            shim.chmod(0o700)
            environment = {
                **os.environ, "PATH": str(commands) + os.pathsep + os.environ["PATH"],
                "MS2_SCRIPT_FIXTURE": str(deployment),
            }
            environment.pop("BASH_VERSION", None)

            def query(arguments):
                if arguments[:3] == ["az", "group", "show"]:
                    return {"tags": {"project": "maple2", "application": "private-pilot"}}
                if arguments[:4] == ["az", "storage", "blob", "exists"]:
                    return {"exists": False}
                if arguments[0] == "gh":
                    return {"object": {"sha": REVISION}}
                if arguments[:3] == ["az", "vm", "run-command"]:
                    script = arguments[arguments.index("--scripts") + 1][1:]
                    self.assertTrue(Path(script).read_text().startswith("#!/bin/bash\n"))
                    result = subprocess.run(["sh", script], env=environment, text=True,
                                            capture_output=True, check=True, timeout=10)
                    return {"value": [{"message": result.stdout}]}
                self.fail("Unexpected delivery command.")

            with patch.object(release, "validate_package", return_value=package), \
                    patch.object(release, "run", return_value=b""), \
                    patch.object(release, "read_json_output", side_effect=query), \
                    patch("sys.stdout", new=io.StringIO()) as console:
                release.deliver(root, REVISION, SOURCE)
            self.assertIn("MS2_RELEASE_DEPLOYED " + REVISION, console.getvalue())

    def scenario(self, failure="none", legacy=False):
        with tempfile.TemporaryDirectory(prefix="maple2-deployment-check-") as temporary:
            base = Path(temporary)
            root, remote, commands = base / "srv", base / "remote", base / "bin"
            previous_name = "20260912-" + "1" * 12 + "-" + "1" * 8
            candidate_name = "20260912-" + "2" * 12 + "-" + "2" * 8
            previous = root / "releases" / previous_name
            candidate = root / "releases" / candidate_name
            package = remote / candidate_name
            for directory in (previous, package, commands, root / "state", root / "backups"):
                directory.mkdir(parents=True, exist_ok=True)
            (root / "current").symlink_to(previous)
            (root / "state" / "initialized").write_text(previous_name + "\n")
            manifest = {
                "subscription": release.SUBSCRIPTION, "storageAccount": release.STORAGE,
                "gitBase": "f" * 40, "sourceSha256": SOURCE,
                "images": {role: {"reference": "fixture-image", "id": "sha256:" + "d" * 64,
                                  "configId": "sha256:" + "d" * 64}
                           for role in (*release.APP_ROLES, *release.VENDOR_ROLES)},
            }
            (previous / ".env").write_text("DB_PASSWORD=synthetic-only\n")
            (previous / "compose.yml").write_text("fixture")
            for name in release.DATA_FILES:
                (previous / name).write_text("retained static artifact")
            manifest["files"] = {name: release.digest(previous / name) for name in release.DATA_FILES}
            (previous / "release.json").write_bytes(release.json_bytes(manifest))
            lock = base / "maintenance.lock"
            for name in ("deploy-release.sh", "start-application.sh", "backup-application.sh", "update-configuration.sh"):
                text = (ROOT / "deploy" / "azure" / name).read_text().replace(
                    "/run/maple2-operation.lock", str(lock)).replace("/srv/maple2", str(root))
                (package / name).write_text(text)
                (package / name).chmod(0o700)
            (previous / "start-application.sh").write_bytes((package / "start-application.sh").read_bytes())
            (previous / "start-application.sh").chmod(0o700)
            (previous / "backup-application.sh").write_bytes((package / "backup-application.sh").read_bytes())
            (previous / "backup-application.sh").chmod(0o700)
            (package / "release.py").write_text("inert fixture; intercepted by the test command shim")
            tar_write(package / "images.tar.gz", {"fixture": "inert image archive"})
            descriptor = {
                "release": candidate_name, "gitCommit": REVISION,
                "imageArchiveBytes": 10240,
                "imageStorageBytes": 10240,
                "files": {path.name: release.digest(path) for path in package.iterdir() if path.is_file()},
            }
            descriptor["fileSizes"] = {name: (package / name).stat().st_size for name in descriptor["files"]}
            (package / "package.json").write_bytes(release.json_bytes(descriptor))
            fingerprint = release.digest(package / "package.json")
            script_name = "deploy-release.sh"
            if legacy:
                for name in release.DATA_FILES:
                    shutil.copyfile(previous / name, package / name)
                candidate_manifest = {
                    **manifest, "release": candidate_name,
                    "sourceSha256": "c" * 64 if failure == "incompatible" else SOURCE,
                    "files": {**descriptor["files"], **manifest["files"]},
                }
                (package / "release.json").write_bytes(release.json_bytes(candidate_manifest))
                fingerprint = release.digest(package / "release.json")
                script_name = "update-configuration.sh"
            config = {
                "root": str(root), "remote": str(remote), "previous": str(previous),
                "candidate": str(candidate), "manifest": manifest, "failure": failure,
                "log": str(base / "commands.log"), "legacy": legacy,
                "candidate_started": str(base / "candidate-started"),
            }
            config_path = base / "fixture.json"
            config_path.write_bytes(release.json_bytes(config))
            shim = commands / "shim"
            shim.write_text("#!" + sys.executable + "\n" + r'''
import gzip, io, json, os
from pathlib import Path
import shutil, sys, tarfile
config = json.loads(Path(os.environ["MS2_FIXTURE"]).read_text())
command, args = Path(sys.argv[0]).name, sys.argv[1:]
with open(config["log"], "a") as log:
    log.write(command + " " + " ".join(args) + "\n")
def value(flag):
    return args[args.index(flag) + 1]
if command == "id":
    print("0")
elif command == "mountpoint":
    pass
elif command == "findmnt":
    print("wrong" if config["failure"] == "wrong-mount" else "e8d29738-429d-48c8-ba00-bf4fa902fa99")
elif command == "df":
    print("Avail\n" + ("1" if config["failure"] == "low-space" else "999999999999"))
elif command == "az":
    if args[:3] == ["storage", "blob", "download"]:
        shutil.copyfile(Path(config["remote"]) / value("--name"), value("--file"))
    elif args[:3] == ["storage", "blob", "upload"] and config["failure"] == "backup-failure":
        sys.exit(18)
elif command == "docker":
    if args[0] == "info":
        print(config["root"] + "/docker")
    elif args[:2] == ["image", "inspect"]:
        print("sha256:" + "d" * 64)
    elif args[0] == "load":
        sys.stdin.buffer.read()
    elif args[0] == "inspect":
        print("0")
    elif args[0] == "run":
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w"):
            pass
        sys.stdout.buffer.write(gzip.compress(stream.getvalue()))
    elif args[0] == "compose":
        if "exec" in args:
            print("-- consistent synthetic player snapshot")
        elif "ps" in args and "--quiet" in args:
            print("fixture-" + args[-1])
        elif "up" in args:
            candidate = str(Path(value("--file")).resolve().parent) == config["candidate"]
            if candidate:
                Path(config["candidate_started"]).touch()
            if candidate and config["failure"] == "startup-failure":
                sys.exit(17)
            if config["legacy"] and config["failure"] == "rollback-failure" and Path(config["candidate_started"]).exists():
                sys.exit(18)
elif command == "python3":
    operation = args[1]
    directory = Path(value("--directory"))
    if operation == "prepare":
        if config["failure"] == "incompatible":
            sys.exit(19)
        manifest = {**config["manifest"], "gitCommit": "a" * 40}
        (directory / "release.json").write_text(json.dumps(manifest))
        shutil.copyfile(Path(config["previous"]) / ".env", directory / ".env")
        (directory / "compose.yml").write_text("fixture")
    elif operation == "probe":
        if config["failure"] == "rollback-failure" or (
            config["failure"] == "health-failure" and str(directory) == config["candidate"]
        ):
            sys.exit(20)
        print("MS2_RELEASE_HEALTHY fixture")
''')
            shim.chmod(0o700)
            for command in ("az", "docker", "python3", "mountpoint", "findmnt", "df", "id"):
                (commands / command).symlink_to(shim)
            env = {**os.environ, "PATH": str(commands) + os.pathsep + os.environ["PATH"], "MS2_FIXTURE": str(config_path)}
            arguments = ["bash", str(package / script_name), release.SUBSCRIPTION, candidate_name, fingerprint]
            if not legacy:
                arguments.append(REVISION)
            result = subprocess.run(
                arguments,
                text=True, capture_output=True, env=env, timeout=30,
            )
            output = result.stdout + result.stderr
            log = (base / "commands.log").read_text()
            self.assertEqual((previous / ".env").read_text(), "DB_PASSWORD=synthetic-only\n")
            if failure == "none":
                self.assertEqual(result.returncode, 0, output)
                self.assertEqual((root / "current").resolve(), candidate)
                self.assertIn("MS2_CONFIGURATION_UPDATED" if legacy else "MS2_RELEASE_DEPLOYED " + REVISION, output)
                self.assertIn("MS2_BACKUP_UPLOADED", output)
                backups = list((root / "backups").glob("ms2-*.tar.gz"))
                self.assertEqual(len(backups), 1)
                with tarfile.open(backups[0]) as archive:
                    self.assertIn(b"consistent synthetic player snapshot", archive.extractfile("game.sql").read())
                first_start = log.index(" up ")
                self.assertLess(log.index("az storage blob upload"), first_start)
            else:
                self.assertNotEqual(result.returncode, 0, output)
                self.assertEqual((root / "current").resolve(), previous)
                self.assertNotIn("MS2_RELEASE_DEPLOYED", output)
                self.assertNotIn("MS2_CONFIGURATION_UPDATED", output)
                if failure in ("incompatible", "wrong-mount", "low-space"):
                    self.assertNotIn(" stop ", log)
                elif failure == "rollback-failure":
                    self.assertIn("MS2_CONFIGURATION_ROLLBACK_FAILED" if legacy else "MS2_ROLLBACK_FAILED", output)
                else:
                    self.assertIn("MS2_CONFIGURATION_ROLLBACK_VERIFIED" if legacy else "MS2_ROLLBACK_VERIFIED", output)
            self.assertNotIn("volume rm", log)
            self.assertNotIn("database=game-server", log)
            self.assertNotIn("efbundle", log)

    def test_promotion_retains_a_backup_and_commits_after_health(self):
        self.scenario()

    def test_data_and_mount_rejection_never_stop_players(self):
        for failure in ("incompatible", "wrong-mount", "low-space"):
            with self.subTest(failure=failure):
                self.scenario(failure)

    def test_backup_startup_and_health_failures_restore_previous_application(self):
        for failure in ("backup-failure", "startup-failure", "health-failure"):
            with self.subTest(failure=failure):
                self.scenario(failure)

    def test_failed_rollback_never_reports_success(self):
        self.scenario("rollback-failure")

    def test_legacy_configuration_updates_recover_failed_startup(self):
        for failure in ("none", "incompatible", "startup-failure", "rollback-failure"):
            with self.subTest(failure=failure):
                self.scenario(failure, legacy=True)


@unittest.skipUnless(os.environ.get("MS2_CICD_DOCKER_TESTS") == "1",
                     "Set MS2_CICD_DOCKER_TESTS=1 for an inert, real Docker archive round-trip.")
class DockerArchiveTests(unittest.TestCase):
    def test_real_compose_memory_output_is_accepted(self):
        with tempfile.TemporaryDirectory(prefix="maple2-compose-check-") as temporary:
            env_file = Path(temporary) / "fixture.env"
            env_file.write_text(
                "DB_PASSWORD=synthetic-test-only\nMYSQL_ROOT_PASSWORD=synthetic-root-only\n"
                "PUBLIC_IP=192.0.2.10\nMS2_DOMAIN=ms2.example.org\n"
                "GAME_IMAGE=fixture/game\nWORLD_IMAGE=fixture/world\nLOGIN_IMAGE=fixture/login\n"
                "WEB_IMAGE=fixture/web\nMYSQL_IMAGE=fixture/mysql\nPROXY_IMAGE=fixture/proxy\n"
            )
            directory = ROOT / "deploy" / "azure"
            config = json.loads(release.run([
                "docker", "compose", "--env-file", str(env_file), "--file", str(directory / "compose.application.yml"),
                "--project-name", "maple2-cicd-fixture", "config", "--format", "json",
            ]))
            retained = release.retained_compose(config, directory)
            self.assertEqual(len(retained["services"]), 7)

    def test_real_image_store_provenance_round_trip(self):
        tags = []
        try:
            with tempfile.TemporaryDirectory(prefix="maple2-image-check-") as temporary:
                root = Path(temporary)
                (root / "fixture.txt").write_text("Inert image provenance fixture; no executable payload.\n")
                (root / "Dockerfile").write_text(
                    "FROM scratch\nCOPY fixture.txt /fixture.txt\nARG ROLE\n"
                    f'LABEL org.mapletime.source.sha256="{SOURCE}"\n'
                    f'LABEL org.opencontainers.image.revision="{REVISION}"\n'
                    'LABEL fixture.role="$ROLE"\n'
                )
                references = {}
                prefix = "maple2/cicd-fixture-" + uuid.uuid4().hex
                for role in release.APP_ROLES:
                    tag = prefix + ":" + role
                    release.run(["docker", "build", "--quiet", "--platform", "linux/amd64", "--provenance=false",
                                 "--build-arg", "ROLE=" + role, "--tag", tag, str(root)])
                    tags.append(tag)
                    identity = release.run(["docker", "image", "inspect", tag, "--format", "{{.Id}}"], text=True).strip()
                    reference = tag + "-" + identity.split(":")[1]
                    release.run(["docker", "tag", tag, reference])
                    tags.append(reference)
                    references[role] = reference
                archive = root / "images.tar"
                release.run(["docker", "save", "--output", str(archive), *references.values()])
                images = release.image_manifest(archive, references, SOURCE, REVISION)
                self.assertEqual(set(images), set(release.APP_ROLES))
                for role, image in images.items():
                    identity = release.run(["docker", "image", "inspect", references[role],
                                            "--format", "{{.Id}}"], text=True).strip()
                    self.assertIn(identity, (image["id"], image["configId"]))
        finally:
            if tags:
                release.run(["docker", "image", "rm", *tags])


if __name__ == "__main__":
    unittest.main()
