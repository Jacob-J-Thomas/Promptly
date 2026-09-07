#!/usr/bin/env bash

set -euo pipefail

mode="base64"
if [[ "${1:-}" == "--legacy-raw" ]]; then
  mode="legacy-raw"
  shift
fi

env_file="${1:-docker/.env}"
if [[ ! -f "$env_file" ]]; then
  echo "$env_file does not exist" >&2
  exit 1
fi

jwt_key_count=$(awk '/^JWT__Key=/{count++} END{print count+0}' "$env_file")
if [[ "$jwt_key_count" -ne 1 ]]; then
  echo "$env_file must contain exactly one JWT__Key entry; found $jwt_key_count" >&2
  exit 1
fi

retired_jwt_key=$(sed -n 's/^JWT__Key=//p' "$env_file")
if [[ -z "$retired_jwt_key" ]]; then
  echo "$env_file has an empty JWT__Key value" >&2
  exit 1
fi

if [[ "$mode" == "legacy-raw" ]]; then
  printf '%s' "$retired_jwt_key" |
    openssl dgst -sha256 -r |
    awk '{print $1}'
else
  canonical_jwt_key=$(
    printf '%s' "$retired_jwt_key" |
      openssl base64 -d -A |
      openssl base64 -A
  )
  if [[ "$canonical_jwt_key" != "$retired_jwt_key" ]]; then
    echo "$env_file JWT__Key is not canonical base64" >&2
    unset retired_jwt_key canonical_jwt_key
    exit 1
  fi
  unset canonical_jwt_key

  printf '%s' "$retired_jwt_key" |
    openssl base64 -d -A |
    openssl dgst -sha256 -r |
    awk '{print $1}'
fi
unset retired_jwt_key
