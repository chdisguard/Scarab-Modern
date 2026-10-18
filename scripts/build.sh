#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
rid="${1:-linux-x64}"
case "$rid" in win-x64|linux-x64|osx-x64|osx-arm64) ;; *) echo 'Unsupported runtime'; exit 1 ;; esac
dotnet test Scarab.Tests/Scarab.Tests.csproj -c Release
dotnet publish Scarab/Scarab.csproj -c Release -r "$rid" --self-contained true -p:PublishSingleFile=true -o "out/$rid"
