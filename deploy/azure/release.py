"""Build and verify source-bound, data-compatible MS2 application releases."""

import argparse
import datetime
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import shlex
import shutil
import socket
import struct
import subprocess
import sys
import tarfile
import tempfile
import time

APP_ROLES = ("game", "world", "login", "web")
VENDOR_ROLES = ("mysql", "proxy")
SUBSCRIPTION = "eb09d227-552f-4003-9129-c3f9cc36748d"
STORAGE = "stmaple2lx7rwls5nb4z2"
PACKAGE_FILES = {
    "compose.yml": "deploy/azure/compose.application.yml",
    "Caddyfile": "deploy/azure/Caddyfile",
    "config.yaml": "config.yaml",
    "start-application.sh": "deploy/azure/start-application.sh",
    "backup-application.sh": "deploy/azure/backup-application.sh",
    "update-configuration.sh": "deploy/azure/update-configuration.sh",
    "deploy-release.sh": "deploy/azure/deploy-release.sh",
    "release.py": "deploy/azure/release.py",
}
DATA_FILES = ("metadata.sql.gz", "navigation.tar.gz", "efbundle")
SOURCE_ROOT_FILES = {
    ".dockerignore", ".editorconfig", ".gitattributes", "global.json",
    "config.yaml", "LICENSE", "README.md", "DEVELOPMENT_STATUS.md", "CLIENT_SETUP.md",
}
REVIEW_PREFIXES = (
    "Maple2.Server.World/Migrations/", "Maple2.Database/Context/",
    "Maple2.Database/Model/", "Maple2.Database/Extensions/",
    "Maple2.Database/Storage/Metadata/", "Maple2.File.Ingest/",
    # ponytail: gate the whole model project; narrow only with a verified EF/JSON type inventory.
    "Maple2.Model/",
)
REVIEW_FILES = {
    ".config/dotnet-tools.json", "Maple2.Database/Maple2.Database.csproj",
    "Maple2.Model/Maple2.Model.csproj",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def run(arguments, **kwargs):
    try:
        return subprocess.run(arguments, check=True, capture_output=True, **kwargs).stdout
    except subprocess.CalledProcessError as error:
        if error.stderr:
            print(error.stderr if isinstance(error.stderr, str) else error.stderr.decode(errors="replace"), file=sys.stderr)
        raise


def digest(path):
    with Path(path).open("rb") as stream:
        value = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
        return value.hexdigest()


def json_bytes(value):
    return (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()


def unique_object(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, f"Duplicate JSON key: {name}")
        result[name] = value
    return result


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)


def safe_name(name):
    path = PurePosixPath(name)
    require(
        isinstance(name, str) and name and "\\" not in name
        and not path.is_absolute() and all(part not in ("", ".", "..") for part in name.split("/")),
        "Unsafe artifact path.",
    )
    return path.parts


def source_file(name):
    path = PurePosixPath(name)
    if name == "Maple2.Server.Game/Navmeshes":
        return False
    allowed = (
        name.startswith(("Maple2.", ".config/", ".github/workflows/", "deploy/azure/", "scripts/"))
        or name in SOURCE_ROOT_FILES
        or (len(path.parts) == 1 and path.suffix in (".sln", ".props", ".targets"))
    )
    if not allowed:
        return False
    require(
        path.name not in (".env", ".mcp.json", "CLAUDE.local.md")
        and path.suffix.lower() not in (".dpapi", ".clixml", ".pfx", ".key", ".pem", ".m2d", ".m2h")
        and not any(part in ("bin", "obj", "node_modules") for part in path.parts),
        f"Private or generated file in source input: {name}",
    )
    if name.startswith("Maple2.Server.Web/Data/"):
        require(name.startswith("Maple2.Server.Web/Data/system/"), "Player uploads cannot enter a release.")
    return True


def make_source(repo, revision, destination):
    require(re.fullmatch(r"[a-f0-9]{40}", revision), "A complete Git revision is required.")
    require(run(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip() == revision,
            "Checkout does not match the requested revision.")
    require(not run(["git", "-C", str(repo), "status", "--porcelain"], text=True).strip(),
            "CI releases require a clean committed checkout.")
    entries = run(["git", "-C", str(repo), "ls-files", "--stage", "-z"]).decode().split("\0")
    names = []
    for entry in filter(None, entries):
        header, name = entry.split("\t", 1)
        mode, _, stage = header.split()
        safe_name(name)
        if not source_file(name):
            continue
        require(stage == "0" and mode in ("100644", "100755"), f"Unexpected source entry: {name}")
        path = repo.joinpath(*PurePosixPath(name).parts)
        require(path.is_file() and not path.is_symlink(), f"Source is not a regular file: {name}")
        names.append(name)
    require("LICENSE" in names and "global.json" in names, "Incomplete source allowlist.")
    with destination.open("wb") as output, gzip.GzipFile(fileobj=output, filename="", mode="wb", mtime=0) as zipped:
        with tarfile.open(fileobj=zipped, mode="w") as archive:
            for name in sorted(names):
                data = repo.joinpath(*PurePosixPath(name).parts).read_bytes()
                member = tarfile.TarInfo(name)
                member.size, member.mode = len(data), 0o644
                archive.addfile(member, io.BytesIO(data))
            member = tarfile.TarInfo("Maple2.Server.Game/Navmeshes")
            member.type, member.mode = tarfile.DIRTYPE, 0o755
            archive.addfile(member)


def data_contract(source):
    result = {}
    with tarfile.open(source) as archive:
        seen = set()
        for member in archive:
            safe_name(member.name)
            require(member.name not in seen and (member.isfile() or member.isdir()), "Invalid source archive entry.")
            seen.add(member.name)
            if member.isfile() and (member.name.startswith(REVIEW_PREFIXES) or member.name in REVIEW_FILES):
                data = archive.extractfile(member).read()
                if PurePosixPath(member.name).suffix.lower() in (".cs", ".csproj", ".json", ".xml", ".props", ".targets"):
                    data = data.decode("utf-8-sig").replace("\r\n", "\n").encode()
                result[member.name] = hashlib.sha256(data).hexdigest()
    require(any(name.startswith("Maple2.Server.World/Migrations/") for name in result), "Source has no migration contract.")
    return result


def image_manifest(archive_path, references, source_hash, revision):
    images = {}
    with tarfile.open(archive_path) as archive:
        records = json.load(archive.extractfile("manifest.json"))
        descriptors = None
        if "index.json" in archive.getnames():
            index = json.load(archive.extractfile("index.json"))
            descriptors = {
                item.get("annotations", {}).get("io.containerd.image.name", "").removeprefix("docker.io/"): item
                for item in index["manifests"]
            }
        for role, reference in references.items():
            matches = [entry for entry in records if reference in entry.get("RepoTags", [])]
            require(len(matches) == 1, f"Missing or ambiguous saved image: {role}")
            raw = archive.extractfile(matches[0]["Config"]).read()
            config = json.loads(raw)
            require(config["os"] == "linux" and config["architecture"] == "amd64", "Wrong image platform.")
            labels = config["config"].get("Labels", {})
            require(labels.get("org.mapletime.source.sha256") == source_hash
                    and labels.get("org.opencontainers.image.revision") == revision,
                    f"Image source provenance is incorrect: {role}")
            require(not any(value.split("=", 1)[0] in ("DB_PASSWORD", "MYSQL_ROOT_PASSWORD", "AZURE_CLIENT_SECRET")
                            for value in config["config"].get("Env", [])), "Image contains deployment-secret configuration.")
            config_id = "sha256:" + hashlib.sha256(raw).hexdigest()
            identity = config_id
            if descriptors is not None:
                require(reference in descriptors, "OCI image reference is missing.")
                identity = descriptors[reference]["digest"]
                require(re.fullmatch(r"sha256:[a-f0-9]{64}", identity), "Invalid OCI digest.")
                raw_manifest = archive.extractfile("blobs/sha256/" + identity.split(":")[1]).read()
                require("sha256:" + hashlib.sha256(raw_manifest).hexdigest() == identity, "OCI manifest changed.")
                require(json.loads(raw_manifest).get("config", {}).get("digest") == config_id,
                        "OCI manifest does not identify the saved image configuration.")
            require(reference.rsplit("-", 1)[-1] in (identity.split(":")[1], config_id.split(":")[1]),
                    "Image tag is not bound to its content identity.")
            images[role] = {"reference": reference, "id": identity, "configId": config_id}
    return images


def build(repo, output, revision):
    require(not output.exists(), "Release output already exists; refusing to overwrite it.")
    output.mkdir(parents=True)
    (output / "source").mkdir()
    source = output / "source" / "server-source.tar.gz"
    make_source(repo, revision, source)
    source_hash = digest(source)
    references = {}
    storage_bytes = 0
    with tempfile.TemporaryDirectory(prefix="maple2-ci-build-") as temporary:
        context = Path(temporary)
        with tarfile.open(source) as archive:
            archive.extractall(context, filter="data")
        for role in APP_ROLES:
            temporary_reference = f"maple2/ci-{role}:{revision}"
            run([
                "docker", "build", "--quiet", "--platform", "linux/amd64", "--provenance=false",
                "--build-arg", "BUILD_CONFIGURATION=Release",
                "--label", f"org.mapletime.source.sha256={source_hash}",
                "--label", f"org.opencontainers.image.revision={revision}",
                "--file", str(context / f"Maple2.Server.{role.title()}" / "Dockerfile"),
                "--tag", temporary_reference, str(context),
            ])
            identity = run(["docker", "image", "inspect", temporary_reference, "--format", "{{.Id}}"], text=True).strip()
            require(re.fullmatch(r"sha256:[a-f0-9]{64}", identity), "Docker did not report a content-addressed image.")
            references[role] = temporary_reference + "-" + identity.split(":")[1]
            run(["docker", "tag", temporary_reference, references[role]])
            storage_bytes += int(run(["docker", "image", "inspect", references[role], "--format", "{{.Size}}"], text=True))
        saved = context / "images.tar"
        run(["docker", "save", "--output", str(saved), *references.values()])
        images = image_manifest(saved, references, source_hash, revision)
        unpacked_size = saved.stat().st_size
        with saved.open("rb") as data, (output / "images.tar.gz").open("wb") as target:
            with gzip.GzipFile(fileobj=target, filename="", mode="wb", mtime=0) as zipped:
                shutil.copyfileobj(data, zipped)
        for name, relative in PACKAGE_FILES.items():
            content = context.joinpath(*PurePosixPath(relative).parts).read_bytes()
            (output / name).write_bytes(content.replace(b"\r\n", b"\n"))
    files = {path.relative_to(output).as_posix(): digest(path) for path in sorted(output.rglob("*")) if path.is_file()}
    package_hash = hashlib.sha256(json_bytes({"files": files, "images": images})).hexdigest()
    release = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d") + "-" + source_hash[:12] + "-" + package_hash[:8]
    package = {
        "formatVersion": 1, "release": release, "gitCommit": revision, "sourceSha256": source_hash,
        "configuration": "Release", "uncommittedSource": False, "images": images, "files": files,
        "imageArchiveBytes": unpacked_size,
        "imageStorageBytes": storage_bytes,
        "fileSizes": {name: output.joinpath(*PurePosixPath(name).parts).stat().st_size for name in files},
    }
    (output / "package.json").write_bytes(json_bytes(package))
    print(json.dumps({"release": release, "packageSha256": digest(output / "package.json"), "gitCommit": revision}))


def validate_package(directory, expected_revision, expected_hash):
    require(digest(directory / "package.json") == expected_hash, "Deployment package fingerprint differs.")
    package = read_json(directory / "package.json")
    require(package.get("formatVersion") == 1 and package.get("configuration") == "Release"
            and package.get("uncommittedSource") is False, "Only committed Release packages are accepted.")
    require(re.fullmatch(r"[a-f0-9]{40}", expected_revision) and package["gitCommit"] == expected_revision,
            "Deployment revision differs from the trusted workflow.")
    require(re.fullmatch(r"[0-9]{8}-[a-f0-9]{12}-[a-f0-9]{8}", package["release"]), "Invalid release identifier.")
    require(set(package["images"]) == set(APP_ROLES), "Incorrect application image set.")
    require(isinstance(package["imageArchiveBytes"], int) and package["imageArchiveBytes"] > 0,
            "Invalid image archive size.")
    require(isinstance(package["imageStorageBytes"], int) and package["imageStorageBytes"] >= 0,
            "Invalid image storage size.")
    require(set(package["files"]) == set(PACKAGE_FILES) | {"source/server-source.tar.gz", "images.tar.gz"},
            "Deployment file allowlist differs.")
    require(set(package["fileSizes"]) == set(package["files"]), "Artifact size allowlist differs.")
    for name, value in package["files"].items():
        path = directory.joinpath(*safe_name(name))
        require(re.fullmatch(r"[a-f0-9]{64}", value) and path.is_file() and not path.is_symlink()
                and digest(path) == value, f"Artifact changed or is missing: {name}")
        require(isinstance(package["fileSizes"][name], int) and package["fileSizes"][name] == path.stat().st_size,
                f"Artifact size differs: {name}")
    require(package["sourceSha256"] == package["files"]["source/server-source.tar.gz"], "Source identity differs.")
    references = {role: package["images"][role]["reference"] for role in APP_ROLES}
    for role, reference in references.items():
        require(re.fullmatch(f"maple2/ci-{role}:{expected_revision}-[a-f0-9]{{64}}", reference),
                "Mutable or unrelated application image reference.")
    require(image_manifest(directory / "images.tar.gz", references, package["sourceSha256"], expected_revision)
            == package["images"], "Saved image identities differ from the package.")
    return package


def compose(directory):
    return ["docker", "compose", "--env-file", str(directory / ".env"),
            "--file", str(directory / "compose.yml"), "--project-name", "maple2-azure"]


def retained_compose(config, directory):
    services = config["services"]
    require(set(services) == {"mysql", "world", "login", "web", "game-ch0", "game-ch1", "proxy"},
            "Topology changes need an operator-reviewed rollout.")
    require(sum(service.get("mem_limit", 0) for service in services.values()) <= 3584 * 1024 * 1024,
            "Release exceeds the existing VM memory budget.")
    retained = {"volumes": config["volumes"], "services": {}}
    for name, service in services.items():
        volumes = []
        for value in service.get("volumes", []):
            value = dict(value)
            if value["type"] == "bind":
                source = Path(value["source"])
                require(source in (directory / "config.yaml", directory / "Caddyfile", directory / "source")
                        and value.get("read_only"), "Unexpected writable or external bind mount.")
                value["source"] = source.name
            volumes.append(value)
        env = service.get("environment", {})
        keys = ("DB_IP", "DB_PORT", "DB_USER", "DB_PASSWORD", "MYSQL_ROOT_PASSWORD",
                "DATA_DB_NAME", "GAME_DB_NAME", "LOGIN_IP", "GAME_IP", "WEB_IP", "WEB_PORT",
                "WEB_BIND_PORT", "REQUIRE_HTTPS_REGISTRATION")
        retained["services"][name] = {
            **{key: service.get(key) for key in (
                "command", "entrypoint", "user", "privileged", "cap_add", "cap_drop",
                "security_opt", "devices", "pid", "ipc", "network_mode", "networks",
                "restart", "stop_grace_period",
            )},
            "ports": service.get("ports", []), "volumes": volumes,
            "environment": env if name in VENDOR_ROLES else {key: env[key] for key in keys if key in env},
        }
    return retained


def prepare(directory, previous, revision, package_hash):
    package = validate_package(directory, revision, package_hash)
    current = read_json(previous / "release.json")
    require(current["subscription"] == SUBSCRIPTION and current["storageAccount"] == STORAGE, "Wrong deployment target.")
    old_source = previous / "source" / "server-source.tar.gz"
    require(digest(old_source) == current["sourceSha256"], "Current corresponding source has changed.")
    before, after = data_contract(old_source), data_contract(directory / "source" / "server-source.tar.gz")
    changed = sorted(name for name in before.keys() | after.keys() if before.get(name) != after.get(name))
    require(not changed, "Schema/metadata changes require an approved data release before promotion: " + ", ".join(changed[:10]))
    images = {**package["images"], **{role: current["images"][role] for role in VENDOR_ROLES}}
    env_lines = (previous / ".env").read_text().splitlines()
    env_lines = [line for line in env_lines if line.split("=", 1)[0] not in {role.upper() + "_IMAGE" for role in images}]
    env_lines.extend(role.upper() + "_IMAGE=" + value["reference"] for role, value in images.items())
    (directory / ".env").write_text("\n".join(env_lines) + "\n")
    os.chmod(directory / ".env", 0o600)
    before_config = json.loads(run([*compose(previous), "config", "--format", "json"]))
    after_config = json.loads(run([*compose(directory), "config", "--format", "json"]))
    require(retained_compose(before_config, previous) == retained_compose(after_config, directory),
            "Ports, persistent volumes, database identity or HTTPS policy changed; operator review is required.")
    required_space = package["imageArchiveBytes"] + package["imageStorageBytes"] + sum(
        (previous / name).stat().st_size for name in DATA_FILES) + 1024**3
    require(shutil.disk_usage(directory).free > required_space, "Insufficient free space to retain the previous release.")
    files = dict(package["files"])
    for name in DATA_FILES:
        require(digest(previous / name) == current["files"][name], f"Retained static artifact changed: {name}")
        shutil.copyfile(previous / name, directory / name)
        files[name] = current["files"][name]
    manifest = {
        **package, "deploymentKind": "ci", "packageSha256": package_hash,
        "subscription": SUBSCRIPTION, "storageAccount": STORAGE, "gitBase": revision,
        "vendorImages": current["vendorImages"], "images": images, "files": files,
        "previousRelease": previous.name,
    }
    (directory / "release.json").write_bytes(json_bytes(manifest))


def deliver(directory, revision, package_hash):
    package = validate_package(directory, revision, package_hash)
    group = read_json_output(["az", "group", "show", "--subscription", SUBSCRIPTION,
                              "--name", "rg-maple2-brazilsouth", "--only-show-errors", "--output", "json"])
    require(group.get("tags", {}).get("project") == "maple2"
            and group["tags"].get("application") == "private-pilot", "Azure target is not the owned MS2 pilot.")
    blob = ["az", "storage", "blob"]
    common = ["--subscription", SUBSCRIPTION, "--account-name", STORAGE,
              "--container-name", "artifacts", "--auth-mode", "login", "--only-show-errors"]
    for name in sorted([*package["files"], "package.json"]):
        path = directory.joinpath(*safe_name(name))
        expected = digest(path)
        remote = package["release"] + "/" + name
        exists = read_json_output([*blob, "exists", *common, "--name", remote, "--output", "json"])["exists"]
        if exists:
            properties = read_json_output([*blob, "show", *common, "--name", remote, "--output", "json"])
            require(properties.get("metadata", {}).get("sha256") == expected,
                    "An immutable release blob already exists with different provenance.")
        else:
            run([*blob, "upload", *common, "--name", remote, "--file", str(path),
                 "--metadata", "sha256=" + expected, "--overwrite", "false", "--output", "none"])
    head = read_json_output(["gh", "api", "repos/gugarosa/Maple2/git/ref/heads/master"])["object"]["sha"]
    require(head == revision, "A newer master revision superseded this run; the old revision was not deployed.")
    arguments = [SUBSCRIPTION, package["release"], package_hash, revision]
    script_hash = package["files"]["deploy-release.sh"]
    script = (
        "#!/bin/bash\n"
        '[ -n "${BASH_VERSION:-}" ] || exec /bin/bash "$0" "$@"\n'
        "set -Eeuo pipefail\numask 077\n"
        "temporary=$(mktemp /tmp/maple2-delivery.XXXXXX)\n"
        "trap 'rm -f -- \"$temporary\"' EXIT\n"
        "az login --identity --allow-no-subscriptions --output none\n"
        f"az storage blob download --subscription {SUBSCRIPTION} --account-name {STORAGE} "
        "--container-name artifacts --auth-mode login --overwrite true --only-show-errors --output none "
        f"--name {shlex.quote(package['release'] + '/deploy-release.sh')} --file \"$temporary\"\n"
        f"[[ \"$(sha256sum \"$temporary\" | cut -d' ' -f1)\" == {shlex.quote(script_hash)} ]]\n"
        "bash \"$temporary\" " + " ".join(shlex.quote(value) for value in arguments) + "\n"
    )
    with tempfile.TemporaryDirectory(prefix="maple2-delivery-") as temporary:
        path = Path(temporary) / "deploy.sh"
        path.write_text(script, encoding="utf-8", newline="\n")
        result = read_json_output([
            "az", "vm", "run-command", "invoke", "--subscription", SUBSCRIPTION,
            "--resource-group", "rg-maple2-brazilsouth", "--name", "vm-maple2-brs",
            "--command-id", "RunShellScript", "--scripts", "@" + str(path),
            "--only-show-errors", "--output", "json",
        ])
    messages = "\n".join(value.get("message", "") for value in result.get("value", []))
    expected = "MS2_RELEASE_DEPLOYED " + revision + " " + package["release"] + " " + package_hash
    if expected not in messages.splitlines():
        print(messages, file=sys.stderr)
    require(expected in messages.splitlines(),
            "Azure did not report verified deployment success; inspect the VM deployment/rollback output.")
    print(expected)


def read_json_output(arguments):
    return json.loads(run(arguments), object_pairs_hook=unique_object)


def probe(directory):
    manifest = read_json(directory / "release.json")
    deadline = time.monotonic() + 90
    while True:
        ids = run([*compose(directory), "ps", "--all", "--quiet"], text=True).split()
        states = json.loads(run(["docker", "inspect", *ids])) if ids else []
        healthy = len(states) == 7 and all(
            item["State"]["Running"] and item["State"].get("Health", {}).get("Status") == "healthy"
            and not item["State"].get("OOMKilled") for item in states
        )
        if healthy:
            response = run([
                "curl", "--fail", "--silent", "--show-error", "--max-time", "5", "--http2-prior-knowledge",
                "-H", "content-type: application/grpc", "-H", "te: trailers", "--data-binary", "@-",
                "http://127.0.0.1:21001/maple2.server.world.service.World/Channels",
            ], input=b"\0\0\0\0\0")
            if response == b"\0\0\0\0\3\x0a\x01\x01":
                break
        require(time.monotonic() < deadline, "Services or World normal-channel registration did not recover.")
        time.sleep(2)
    for port in (20001, 20002, 20003):
        with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
            connection.settimeout(5)
            data = bytearray()
            while len(data) < 25:
                part = connection.recv(25 - len(data))
                require(part, "Native endpoint closed before its handshake completed.")
                data.extend(part)
            require(struct.unpack_from("<H", data, 6)[0] == 1
                    and struct.unpack_from("<I", data, 8)[0] == 12, "Wrong native client protocol.")
    domain = "play.ms2.mapletime.dev"
    request = ["curl", "--fail", "--silent", "--show-error", "--max-time", "30",
               "--resolve", f"{domain}:443:127.0.0.1"]
    page = run([*request, f"https://{domain}/account"])
    require(b"__RequestVerificationToken" in page and b"/account/register" in page, "Registration page is not ready.")
    source = run([*request, f"https://{domain}/source/server-source.tar.gz"])
    require(hashlib.sha256(source).hexdigest() == manifest["sourceSha256"], "Served source is not the deployed revision.")
    status = run([
        "curl", "--silent", "--show-error", "--max-time", "5", "--output", os.devnull,
        "--write-out", "%{http_code}", "-H", "X-Forwarded-Proto: https", "http://127.0.0.1:4000/account",
    ], text=True)
    require(status in ("403", "404"), "Native HTTP exposed registration.")
    print("MS2_RELEASE_HEALTHY " + manifest.get("gitCommit", manifest["gitBase"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    build_parser = commands.add_parser("build")
    build_parser.add_argument("--repo", type=Path, required=True)
    build_parser.add_argument("--output", type=Path, required=True)
    build_parser.add_argument("--revision", required=True)
    prepare_parser = commands.add_parser("prepare")
    prepare_parser.add_argument("--directory", type=Path, required=True)
    prepare_parser.add_argument("--previous", type=Path, required=True)
    prepare_parser.add_argument("--revision", required=True)
    prepare_parser.add_argument("--package-hash", required=True)
    deliver_parser = commands.add_parser("deliver")
    deliver_parser.add_argument("--directory", type=Path, required=True)
    deliver_parser.add_argument("--revision", required=True)
    deliver_parser.add_argument("--package-hash", required=True)
    probe_parser = commands.add_parser("probe")
    probe_parser.add_argument("--directory", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "build":
        build(args.repo.resolve(), args.output.resolve(), args.revision)
    elif args.command == "prepare":
        prepare(args.directory.resolve(), args.previous.resolve(), args.revision, args.package_hash)
    elif args.command == "deliver":
        deliver(args.directory.resolve(), args.revision, args.package_hash)
    else:
        probe(args.directory.resolve())


if __name__ == "__main__":
    main()
