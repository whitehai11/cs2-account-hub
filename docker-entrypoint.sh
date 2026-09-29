#!/bin/sh
set -eu

data_dir="${CS2_HUB_DATA_DIR:-/data}"
mkdir -p "$data_dir"
chown -R cs2hub:cs2hub "$data_dir"

exec gosu cs2hub dotnet /app/CS2AccountHub.Backend.dll
