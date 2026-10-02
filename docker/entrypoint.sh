#!/bin/sh
# Load secrets from files (Docker secrets) so they never appear in the image,
# the compose file, or `docker inspect` output.
set -eu

load_secret() {
  var="$1"
  file_var="${var}_FILE"
  eval "file=\${$file_var:-}"
  if [ -n "$file" ]; then
    [ -r "$file" ] || { echo "xstorage: cannot read $file_var" >&2; exit 1; }
    value=$(cat "$file")
    export "$var=$value"
    unset "$file_var"
  fi
}

load_secret XSTORAGE_ADMIN_TOKEN
load_secret XSTORAGE_APP_ENCRYPTION_KEY
load_secret XSTORAGE_CONTINUATION_TOKEN_KEY

exec dotnet /app/XStorage.Server.dll "$@"
