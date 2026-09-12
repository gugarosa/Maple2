#!/bin/bash
set -Eeuo pipefail
[[ "$(id -u)" == 0 ]] || { echo 'Run as root.' >&2; exit 1; }
mountpoint --quiet /srv/maple2
[[ "$(docker info --format '{{.DockerRootDir}}')" == /srv/maple2/docker ]]
if ! command -v az >/dev/null; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update --quiet
  apt-get install --yes --no-install-recommends ca-certificates curl gnupg
  install -d -m 0755 /etc/apt/keyrings
  curl --fail --silent --show-error https://packages.microsoft.com/keys/microsoft.asc |
    gpg --dearmor --yes -o /etc/apt/keyrings/microsoft-azure-cli.gpg
  chmod 0644 /etc/apt/keyrings/microsoft-azure-cli.gpg
  . /etc/os-release
  [[ "$ID" == ubuntu && "$VERSION_CODENAME" == noble ]]
  printf 'deb [arch=amd64 signed-by=/etc/apt/keyrings/microsoft-azure-cli.gpg] https://packages.microsoft.com/repos/azure-cli/ noble main\n' \
    > /etc/apt/sources.list.d/azure-cli.list
  apt-get update --quiet
  apt-get install --yes --no-install-recommends azure-cli
fi
az login --identity --allow-no-subscriptions --output none
echo 'MS2 application prerequisites and managed-identity authentication are ready.'
