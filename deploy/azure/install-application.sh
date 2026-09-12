#!/bin/bash
set -Eeuo pipefail
umask 077
trap 'echo "MS2 installation failed at line $LINENO." >&2' ERR
[[ "$(id -u)" == 0 ]] || { echo 'Run as root.' >&2; exit 1; }
subscription=$1
release=$2
manifest_hash=$3
[[ "$subscription" =~ ^[a-f0-9-]{36}$ && "$release" =~ ^[0-9]{8}-[a-f0-9]{12}-[a-f0-9]{8}$ && "$manifest_hash" =~ ^[a-f0-9]{64}$ ]]
storage=stmaple2lx7rwls5nb4z2
vault=kv-maple2-lx7rwls5nb4z2
mountpoint --quiet /srv/maple2
[[ "$(findmnt -n -o UUID --mountpoint /srv/maple2)" == e8d29738-429d-48c8-ba00-bf4fa902fa99 ]]
[[ "$(docker info --format '{{.DockerRootDir}}')" == /srv/maple2/docker ]]
exec 9>/run/maple2-operation.lock
flock -n 9 || { echo 'Another MS2 maintenance operation is active.' >&2; exit 1; }

# ponytail: initial-pilot installer; upgrades need an audited backup/migration procedure.
[[ ! -f /srv/maple2/state/initialized ]] ||
  { echo 'An application is already initialized; use its restart/backup procedure, not this installer.' >&2; exit 1; }
install -d -m 0700 /srv/maple2/state /srv/maple2/backups
if [[ ! -f /srv/maple2/state/bootstrap-owner ]]; then
  if docker volume inspect maple2-azure_mysql >/dev/null 2>&1; then
    echo 'An unclaimed player volume already exists; refusing to initialize over it.' >&2
    exit 1
  fi
  printf '%s\n' "$release" > /srv/maple2/state/bootstrap-owner
fi
directory="/srv/maple2/releases/$release"
install -d -m 0700 "$directory"
cd "$directory"
az login --identity --allow-no-subscriptions --output none
download() {
  az storage blob download --subscription "$subscription" --account-name "$storage" \
    --container-name artifacts --auth-mode login --name "$release/$1" --file "$1" \
    --overwrite true --only-show-errors --output none
}
if [[ ! -f release.json || "$(sha256sum release.json | cut -d' ' -f1)" != "$manifest_hash" ]]; then
  download release.json
fi
[[ "$(sha256sum release.json | cut -d' ' -f1)" == "$manifest_hash" ]]
[[ "$(jq -er '.release' release.json)" == "$release" ]]
while IFS=$'\t' read -r file hash; do
  [[ "$file" =~ ^[a-zA-Z0-9._/-]+$ && "$file" != /* && "$file" != *..* && "$hash" =~ ^[a-f0-9]{64}$ ]]
  mkdir -p -- "$(dirname -- "$file")"
  if [[ ! -f "$file" || "$(sha256sum "$file" | cut -d' ' -f1)" != "$hash" ]]; then
    download "$file"
  fi
  [[ "$(sha256sum "$file" | cut -d' ' -f1)" == "$hash" ]] ||
    { echo "Artifact fingerprint mismatch: $file" >&2; exit 1; }
done < <(jq -er '.files | to_entries[] | [.key, .value] | @tsv' release.json)

gzip -dc images.tar.gz | docker load
while IFS=$'\t' read -r image manifest_id config_id; do
  actual=$(docker image inspect "$image" --format '{{.Id}}')
  [[ "$actual" == "$manifest_id" || "$actual" == "$config_id" ]] ||
    { echo "Loaded image fingerprint mismatch: $image" >&2; exit 1; }
done < <(jq -er '.images[] | [.reference, .id, .configId] | @tsv' release.json)

root_password=$(az keyvault secret show --subscription "$subscription" --vault-name "$vault" \
  --name ms2-db-root-password --query value --output tsv --only-show-errors)
app_password=$(az keyvault secret show --subscription "$subscription" --vault-name "$vault" \
  --name ms2-db-runtime-password --query value --output tsv --only-show-errors)
[[ "$root_password" =~ ^[a-f0-9]{64}$ && "$app_password" =~ ^[a-f0-9]{64}$ && "$root_password" != "$app_password" ]]
{
  printf 'MYSQL_ROOT_PASSWORD=%s\nDB_PASSWORD=%s\n' "$root_password" "$app_password"
  printf 'PUBLIC_IP=20.226.79.46\nMS2_DOMAIN=play.ms2.mapletime.dev\n'
  jq -r '.images | to_entries[] | "\(.key | ascii_upcase)_IMAGE=\(.value.reference)"' release.json
} > .env
chmod 0600 .env
compose=(docker compose --env-file .env --file compose.yml --project-name maple2-azure)
"${compose[@]}" up --detach --no-build --pull never --wait --wait-timeout 300 mysql
"${compose[@]}" stop game-ch0 game-ch1
"${compose[@]}" stop proxy web login world
# Bulk JSON import needs more memory than normal runtime queries; no apps run yet.
docker update --memory 2g --memory-swap 2g "$("${compose[@]}" ps --quiet mysql)" >/dev/null
"${compose[@]}" exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --user=root' <<'SQL'
SET GLOBAL innodb_redo_log_capacity=536870912;
CREATE DATABASE IF NOT EXISTS `maple-data`;
CREATE DATABASE IF NOT EXISTS `game-server`;
SQL
gzip -dc metadata.sql.gz | "${compose[@]}" exec -T mysql sh -c \
  'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --user=root --database=maple-data'
"${compose[@]}" exec -T mysql sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql --user=root' <<SQL
CREATE USER IF NOT EXISTS 'ms2_runtime'@'%' IDENTIFIED BY '$app_password';
GRANT SELECT ON \`maple-data\`.* TO 'ms2_runtime'@'%';
GRANT SELECT, INSERT, UPDATE, DELETE ON \`game-server\`.* TO 'ms2_runtime'@'%';
SQL
{
  printf 'DB_IP=mysql\nDB_PORT=3306\nDB_USER=root\nGAME_DB_NAME=game-server\n'
  printf 'DB_PASSWORD=%s\n' "$root_password"
} > migration.env
trap 'rm -f -- migration.env' EXIT
chmod 0700 efbundle
docker run --rm --network maple2-azure_default --env-file migration.env \
  --mount "type=bind,source=$directory/efbundle,target=/migrations/efbundle,readonly" \
  --entrypoint /migrations/efbundle "$(jq -er '.images.world.reference' release.json)"
rm -f -- migration.env
unset root_password app_password
"${compose[@]}" stop mysql
"${compose[@]}" up --detach --no-build --pull never --no-deps --force-recreate --wait --wait-timeout 300 mysql

docker volume create maple2-azure_navmeshes >/dev/null
docker run --rm --network none --read-only \
  --volume maple2-azure_navmeshes:/navigation \
  --mount "type=bind,source=$directory/navigation.tar.gz,target=/navigation.tar.gz,readonly" \
  --entrypoint tar "$(jq -er '.images.proxy.reference' release.json)" \
  -C /navigation -xzf /navigation.tar.gz
chmod 0700 start-application.sh backup-application.sh
./start-application.sh
if [[ -e /srv/maple2/current && ! -L /srv/maple2/current ]]; then
  echo 'The current application path is not an owned release link.' >&2
  exit 1
fi
ln -sfn "$directory" /srv/maple2/current
jq -r '.files["metadata.sql.gz"]' release.json > /srv/maple2/state/metadata.sha256
printf '%s\n' "$release" > /srv/maple2/state/initialized
echo "MS2 application installed from $release. External HTTPS/game probes are still required."
