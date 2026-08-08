#!/usr/bin/env bash
# Wrapper so the user-local .NET 8 SDK (~/.dotnet) is always used.
# Usage: ./dotnet.sh build   ./dotnet.sh test   etc.
#
#   ./dotnet.sh test --with-db HbmpPlatform.sln
#
# --with-db points the ~100 env-gated integration and RLS tests at the local Compose Postgres so they RUN
# instead of skipping. Without it they answer Skip.If(...) and report green without touching a database —
# which hides the concurrency, RLS and break-glass proofs precisely when someone is relying on them. The
# flag is stripped before dotnet sees it; everything else passes through untouched. See
# tools/ci/with-test-db.sh.
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

args=()
with_db=0
for arg in "$@"; do
  if [[ "$arg" == "--with-db" ]]; then with_db=1; else args+=("$arg"); fi
done

if [[ "$with_db" == "1" ]]; then
  exec "$(dirname "${BASH_SOURCE[0]}")/tools/ci/with-test-db.sh" "$DOTNET_ROOT/dotnet" "${args[@]}"
fi

exec "$DOTNET_ROOT/dotnet" "${args[@]}"
