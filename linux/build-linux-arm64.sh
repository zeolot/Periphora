#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
DOTNET=${DOTNET:-dotnet}

# Prefer a project-local SDK: it keeps the build reproducible on machines that
# have no system-wide .NET installed. Both the POSIX and the Windows layout of
# the official install script are covered.
if [ -x "$ROOT/.tools/dotnet/dotnet" ]; then
    DOTNET="$ROOT/.tools/dotnet/dotnet"
elif [ -x "$ROOT/.tools/dotnet/dotnet.exe" ]; then
    DOTNET="$ROOT/.tools/dotnet/dotnet.exe"
fi

"$DOTNET" publish "$ROOT/linux/Periphora.Linux.csproj" \
    -c Release -r linux-arm64 --self-contained true \
    -o "$ROOT/dist/Periphora-linux-arm64"

# package.py only needs the Python standard library, so a bare `python3` (or
# `python` on Windows, where the launcher is not called python3) is enough.
if command -v python3 >/dev/null 2>&1; then
    PYTHON=python3
else
    PYTHON=python
fi

"$PYTHON" "$ROOT/linux/packaging/package.py"
