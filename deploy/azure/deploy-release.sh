#!/bin/bash
set -Eeuo pipefail
umask 077
[[ "$(id -u)" == 0 && "$#" == 4 ]] || { echo 'Expected root and subscription, release, package hash, revision.' >&2; exit 1; }
subscription=$1
release=$2
package_hash=$3
revision=$4
[[ "$subscription" == eb09d227-552f-4003-9129-c3f9cc36748d ]]
[[ "$release" =~ ^[0-9]{8}-[a-f0-9]{12}-[a-f0-9]{8}$ &&
   "$package_hash" =~ ^[a-f0-9]{64}$ && "$revision" =~ ^[a-f0-9]{40}$ ]]
mountpoint --quiet /srv/maple2
[[ "$(findmnt -n -o UUID --mountpoint /srv/maple2)" == e8d29738-429d-48c8-ba00-bf4fa902fa99 ]]
[[ "$(docker info --format '{{.DockerRootDir}}')" == /srv/maple2/docker ]]
exec 9>/run/maple2-operation.lock
flock -n 9 || { echo 'Another MS2 maintenance operation is active.' >&2; exit 1; }
previous=$(readlink -f /srv/maple2/current)
[[ "$previous" == /srv/maple2/releases/* && -f "$previous/release.json" && -f "$previous/.env" ]]
directory="/srv/maple2/releases/$release"
if [[ "$previous" == "$directory" ]]; then
  [[ "$(jq -er '.gitCommit' "$previous/release.json")" == "$revision" &&
     "$(jq -er '.packageSha256' "$previous/release.json")" == "$package_hash" ]]
  python3 "$previous/release.py" probe --directory "$previous"
  echo "MS2_RELEASE_DEPLOYED $revision $release $package_hash"
  exit 0
fi
[[ ! -L "$directory" ]]
install -d -m 0700 "$directory"
cd -- "$directory"
az login --identity --allow-no-subscriptions --output none
download() {
  az storage blob download --subscription "$subscription" --account-name stmaple2lx7rwls5nb4z2 \
    --container-name artifacts --auth-mode login --name "$release/$1" --file "$1" \
    --overwrite true --only-show-errors --output none
}
if [[ ! -f package.json ]]; then download package.json; fi
[[ "$(sha256sum package.json | cut -d' ' -f1)" == "$package_hash" ]]
[[ "$(jq -er '.release' package.json)" == "$release" &&
   "$(jq -er '.gitCommit' package.json)" == "$revision" ]]
required=$(jq -er '.imageArchiveBytes + .imageStorageBytes + ([.fileSizes[]] | add) + 1073741824' package.json)
[[ "$required" =~ ^[0-9]+$ ]]
for file in metadata.sql.gz navigation.tar.gz efbundle; do
  required=$((required + $(stat -c %s -- "$previous/$file")))
done
available=$(df --output=avail -B1 "$directory" | tail -n 1)
[[ "$available" =~ ^[[:space:]]*[0-9]+$ && "$available" -gt "$required" ]] ||
  { echo 'Insufficient space to download a candidate while retaining the live database and rollback release.' >&2; exit 1; }
while IFS=$'\t' read -r file hash; do
  [[ "$file" =~ ^[a-zA-Z0-9._/-]+$ && "$file" != /* && "$file" != *..* &&
     "$hash" =~ ^[a-f0-9]{64}$ && ! -L "$file" ]]
  mkdir -p -- "$(dirname -- "$file")"
  if [[ ! -f "$file" ]]; then download "$file"; fi
  [[ "$(sha256sum "$file" | cut -d' ' -f1)" == "$hash" ]]
done < <(jq -er '.files | to_entries[] | [.key, .value] | @tsv' package.json)
[[ -f release.py && -f backup-application.sh && -f start-application.sh ]]
python3 release.py prepare --directory "$directory" --previous "$previous" \
  --revision "$revision" --package-hash "$package_hash"
gzip -dc images.tar.gz | docker load >/dev/null
chmod 0700 start-application.sh backup-application.sh update-configuration.sh deploy-release.sh

apps_touched=false
promoted=false
cleanup() {
  result=$?
  trap - EXIT
  if [[ "$apps_touched" == true && "$promoted" == false ]]; then
    echo "MS2_RELEASE_ROLLBACK ${previous##*/}" >&2
    recovered=true
    ln -sfn "$previous" /srv/maple2/current || recovered=false
    printf '%s\n' "${previous##*/}" > /srv/maple2/state/initialized || recovered=false
    "$previous/start-application.sh" || recovered=false
    python3 "$directory/release.py" probe --directory "$previous" || recovered=false
    if [[ "$recovered" == false ]]; then
      echo "MS2_ROLLBACK_FAILED ${previous##*/}; operator recovery is required. No database was restored or deleted." >&2
      result=1
    else
      echo "MS2_ROLLBACK_VERIFIED ${previous##*/}" >&2
    fi
  fi
  exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
apps_touched=true
./backup-application.sh --deployment "$previous"
./start-application.sh
python3 release.py probe --directory "$directory"
ln -sfn "$directory" /srv/maple2/current
printf '%s\n' "$release" > /srv/maple2/state/initialized
promoted=true
echo "MS2_RELEASE_DEPLOYED $revision $release $package_hash"
