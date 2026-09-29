#!/bin/sh
set -eu

data_dir="${CS2_HUB_DATA_DIR:-/data}"
mkdir -p "$data_dir"
chown -R app:app "$data_dir"

exec gosu app dotnet /app/CS2AccountHub.Backend.dll
