#!/bin/bash
set -Eeuo pipefail
umask 077
mountpoint --quiet /srv/maple2
keep_stopped=false
directory="$(dirname -- "$(readlink -f -- "$0")")"
if [[ "$#" == 2 && "$1" == --deployment ]]; then
  directory=$(readlink -f -- "$2")
  [[ "$directory" == /srv/maple2/releases/* && -f "$directory/release.json" ]]
  [[ "$(readlink /proc/self/fd/9)" == /run/maple2-operation.lock ]]
  flock -n 9 || { echo 'Deployment did not retain its maintenance lock.' >&2; exit 1; }
  keep_stopped=true
else
  [[ "$#" == 0 ]] || { echo 'Invalid backup arguments.' >&2; exit 1; }
  exec 9>/run/maple2-operation.lock
  flock -n 9 || { echo 'Another MS2 maintenance operation is active.' >&2; exit 1; }
fi
cd -- "$directory"
compose=(docker compose --env-file .env --file compose.yml --project-name maple2-azure)
subscription=$(jq -er '.subscription' release.json)
storage=$(jq -er '.storageAccount' release.json)
[[ "$storage" =~ ^stmaple2[a-z0-9]+$ ]]
stamp=$(date -u +%Y%m%dT%H%M%SZ)
temporary=$(mktemp -d /srv/maple2/backups/.snapshot.XXXXXX)
restart_needed=false
cleanup() {
  result=$?
  trap - EXIT
  if [[ "$restart_needed" == true && "$keep_stopped" == false ]]; then
    ./start-application.sh || result=1
  fi
  rm -f -- "$temporary/game.sql" "$temporary/web-data.tar.gz" "$temporary/web-keys.tar.gz" \
    "$temporary/tls-data.tar.gz" "$temporary/release.json"
  rmdir -- "$temporary"
  exit "$result"
}
trap cleanup EXIT

# Quiesce writers because uploads can replace files referenced by the database.
restart_needed=true
"${compose[@]}" stop game-ch0 game-ch1
"${compose[@]}" stop proxy web login world
for service in game-ch0 game-ch1 proxy web login world; do
  id=$("${compose[@]}" ps --all --quiet "$service")
  [[ "$(docker inspect --format '{{.State.ExitCode}}' "$id")" == 0 ]]
done
"${compose[@]}" exec -T mysql sh -c \
  'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysqldump --user=root --single-transaction --hex-blob --no-tablespaces --set-gtid-purged=OFF game-server' \
  > "$temporary/game.sql"
for entry in web-data web-keys tls-data; do
  docker run --rm --network none --read-only \
    --volume "maple2-azure_${entry}:/snapshot:ro" \
    --entrypoint tar "$(jq -er '.images.proxy.reference' release.json)" \
    -C /snapshot -czf - . > "$temporary/$entry.tar.gz"
done
cp -- release.json "$temporary/release.json"
archive="/srv/maple2/backups/ms2-${stamp}.tar.gz"
tar -C "$temporary" -czf "$archive" game.sql web-data.tar.gz web-keys.tar.gz tls-data.tar.gz release.json
(cd /srv/maple2/backups && sha256sum "$(basename "$archive")") > "$archive.sha256"
az login --identity --allow-no-subscriptions --output none
az storage blob upload --subscription "$subscription" --account-name "$storage" --container-name backups \
  --auth-mode login --name "application/$(basename "$archive")" --file "$archive" \
  --overwrite false --only-show-errors --output none
az storage blob upload --subscription "$subscription" --account-name "$storage" --container-name backups \
  --auth-mode login --name "application/$(basename "$archive").sha256" --file "$archive.sha256" \
  --overwrite false --only-show-errors --output none
echo "MS2_BACKUP_UPLOADED $(basename "$archive")"
if [[ "$keep_stopped" == true ]]; then
  echo 'Applications remain stopped under the deployment maintenance lock.'
else
  echo 'Applications are being restarted.'
fi
