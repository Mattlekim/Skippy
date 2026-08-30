#!/bin/bash
# SessionStart hook: make the .NET toolchain available so Claude Code (esp. on
# the web) can build the Desktop and Web heads. Idempotent and non-interactive.
set -euo pipefail

# Only do the heavy install in Claude Code on the web; local machines are
# expected to have the SDK already (see README / global.json).
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_DIR="$HOME/.dotnet"
DOTNET_CHANNEL="8.0"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# 1) Install the .NET 8 SDK. Idempotent, best-effort so a network-restricted
#    environment still starts cleanly.
#
#    Primary path: apt (Ubuntu ships dotnet-sdk-8.0, and archive.ubuntu.com is
#    typically reachable even when Microsoft's own download hosts are blocked by
#    the egress policy). Fallback: Microsoft's dotnet-install script.
export PATH="$DOTNET_DIR:/usr/bin:$PATH"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Installing .NET SDK $DOTNET_CHANNEL ..."
  SUDO=""; [ "$(id -u)" != "0" ] && SUDO="sudo"

  if command -v apt-get >/dev/null 2>&1 && \
     $SUDO apt-get install -y "dotnet-sdk-${DOTNET_CHANNEL}" >/dev/null 2>&1; then
    echo "Installed dotnet-sdk-${DOTNET_CHANNEL} via apt."
  elif curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh 2>/dev/null; then
    bash /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_DIR" || true
    echo "Installed .NET SDK via dotnet-install script."
  else
    echo "WARN: could not install the .NET SDK automatically."
    echo "      Ensure the environment allows either the Ubuntu apt archive"
    echo "      (archive.ubuntu.com) or Microsoft's hosts (dot.net, aka.ms,"
    echo "      builds.dotnet.microsoft.com). NuGet (api.nuget.org) is also"
    echo "      needed to restore packages. Then restart the session."
  fi
fi

export PATH="$DOTNET_DIR:/usr/bin:$PATH"

# Nothing below works without the SDK; bail out gracefully if it's still absent.
if ! command -v dotnet >/dev/null 2>&1; then
  echo "WARN: dotnet not available — skipping workload/restore (see hosts above)."
  exit 0
fi

# 2) Persist env for the rest of the session.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export PATH=\"$DOTNET_DIR:/usr/bin:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
    echo "export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
  } >> "$CLAUDE_ENV_FILE"
fi

# 3) Workload needed to build/publish the browser (Blazor WebAssembly) head.
dotnet workload install wasm-tools --skip-manifest-update || \
  echo "WARN: could not install wasm-tools workload (Web publish may need it)."

# 4) Warm the caches so the first build is fast. Desktop + Web restore cleanly
#    without extra workloads; Android needs 'dotnet workload install android'
#    (~1GB) which is intentionally left out of the hook — run it manually if you
#    build the Android head in a web session.
dotnet tool restore || echo "WARN: 'dotnet tool restore' (MGCB tools) failed."
dotnet restore src/Skippy.Desktop/Skippy.Desktop.csproj || echo "WARN: Desktop restore failed."
dotnet restore src/Skippy.Web/Skippy.Web.csproj || echo "WARN: Web restore failed."

echo ".NET setup complete: $(dotnet --version)"
