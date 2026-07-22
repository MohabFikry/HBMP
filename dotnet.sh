#!/usr/bin/env bash
# Wrapper so the user-local .NET 8 SDK (~/.dotnet) is always used.
# Usage: ./dotnet.sh build   ./dotnet.sh test   etc.
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
exec "$DOTNET_ROOT/dotnet" "$@"
