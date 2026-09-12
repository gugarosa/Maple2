#!/bin/bash
set -Eeuo pipefail
cd -- "$(dirname -- "$(readlink -f -- "$0")")"
mountpoint --quiet /srv/maple2
while IFS=$'\t' read -r image manifest_id config_id; do
  actual=$(docker image inspect "$image" --format '{{.Id}}')
  [[ "$actual" == "$manifest_id" || "$actual" == "$config_id" ]] ||
    { echo "Image fingerprint differs from the release manifest: $image" >&2; exit 1; }
done < <(jq -er '.images[] | [.reference, .id, .configId] | @tsv' release.json)
compose=(docker compose --env-file .env --file compose.yml --project-name maple2-azure)
"${compose[@]}" up --detach --no-build --pull never --wait --wait-timeout 300 mysql world login web
"${compose[@]}" up --detach --no-build --pull never --no-deps --force-recreate --wait --wait-timeout 300 game-ch0
"${compose[@]}" up --detach --no-build --pull never --no-deps --force-recreate --wait --wait-timeout 300 game-ch1
"${compose[@]}" up --detach --no-build --pull never --no-deps --force-recreate --wait --wait-timeout 300 proxy
"${compose[@]}" ps
