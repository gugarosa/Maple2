#!/bin/bash
set -Eeuo pipefail
umask 077
subscription=$1
release=$2
manifest_hash=$3
[[ "$subscription" =~ ^[a-f0-9-]{36}$ && "$release" =~ ^[0-9]{8}-[a-f0-9]{12}-[a-f0-9]{8}$ && "$manifest_hash" =~ ^[a-f0-9]{64}$ ]]
current=$(readlink -f /srv/maple2/current)
[[ "$current" == /srv/maple2/releases/* && -f "$current/release.json" ]]
[[ "$(jq -r '.deploymentKind // "legacy"' "$current/release.json")" != ci ]] ||
  { echo 'CI-managed releases must be updated through the validated delivery workflow.' >&2; exit 1; }
directory="/srv/maple2/releases/$release"
[[ "$directory" != "$current" ]]
install -d -m 0700 "$directory"
cd "$directory"
az login --identity --allow-no-subscriptions --output none
download() {
  az storage blob download --subscription "$subscription" --account-name stmaple2lx7rwls5nb4z2 \
    --container-name artifacts --auth-mode login --name "$release/$1" --file "$1" \
    --overwrite true --only-show-errors --output none
}
download release.json
[[ "$(sha256sum release.json | cut -d' ' -f1)" == "$manifest_hash" ]]
[[ "$(jq -er '.release' release.json)" == "$release" ]]
for query in '.sourceSha256' '.images' '.files["metadata.sql.gz"]' '.files["navigation.tar.gz"]' '.files.efbundle'; do
  [[ "$(jq -S "$query" release.json)" == "$(jq -S "$query" "$current/release.json")" ]] ||
    { echo 'This updater permits configuration changes only, not code, images or database changes.' >&2; exit 1; }
done
while IFS=$'\t' read -r file hash; do
  [[ "$file" =~ ^[a-zA-Z0-9._/-]+$ && "$file" != /* && "$file" != *..* && "$hash" =~ ^[a-f0-9]{64}$ ]]
  mkdir -p -- "$(dirname -- "$file")"
  if [[ -f "$current/$file" && "$(sha256sum "$current/$file" | cut -d' ' -f1)" == "$hash" ]]; then
    cp --reflink=auto -- "$current/$file" "$file"
  else
    download "$file"
  fi
  [[ "$(sha256sum "$file" | cut -d' ' -f1)" == "$hash" ]]
done < <(jq -er '.files | to_entries[] | [.key, .value] | @tsv' release.json)
"$current/backup-application.sh"
exec 9>/run/maple2-operation.lock
flock -n 9 || { echo 'Another MS2 maintenance operation is active.' >&2; exit 1; }
[[ "$(readlink -f /srv/maple2/current)" == "$current" ]] ||
  { echo 'The active release changed while preparing this update; retry from the new baseline.' >&2; exit 1; }
cp -- "$current/.env" .env
chmod 0700 start-application.sh backup-application.sh update-configuration.sh
apps_touched=false
promoted=false
cleanup() {
  result=$?
  trap - EXIT
  if [[ "$apps_touched" == true && "$promoted" == false ]]; then
    recovered=true
    ln -sfn "$current" /srv/maple2/current || recovered=false
    printf '%s\n' "${current##*/}" > /srv/maple2/state/initialized || recovered=false
    # Legacy bundles use this helper's image checks and --wait health checks.
    "$current/start-application.sh" || recovered=false
    if [[ "$recovered" == true ]]; then
      echo "MS2_CONFIGURATION_ROLLBACK_VERIFIED ${current##*/}" >&2
    else
      echo "MS2_CONFIGURATION_ROLLBACK_FAILED ${current##*/}; operator recovery is required." >&2
      result=1
    fi
  fi
  exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
old=(docker compose --env-file "$current/.env" --file "$current/compose.yml" --project-name maple2-azure)
apps_touched=true
"${old[@]}" stop game-ch0 game-ch1
"${old[@]}" stop proxy web login world
for service in game-ch0 game-ch1 proxy web login world; do
  id=$("${old[@]}" ps --all --quiet "$service")
  [[ "$(docker inspect --format '{{.State.ExitCode}}' "$id")" == 0 ]]
done
./start-application.sh
ln -sfn "$directory" /srv/maple2/current
printf '%s\n' "$release" > /srv/maple2/state/initialized
promoted=true
echo "MS2_CONFIGURATION_UPDATED $release"
