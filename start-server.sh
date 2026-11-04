#!/bin/sh
set -eu
cd "$(dirname "$0")"
export MIKUSB_OFFLINE=1
exec dotnet run --project MikuSB --no-launch-profile
