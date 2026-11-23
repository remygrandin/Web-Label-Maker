#!/usr/bin/env bash
# Cloudflare Workers Builds entry point. The build image has no .NET SDK,
# so install one locally, then publish the static site to ./dist/wwwroot.
set -euo pipefail

curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 10.0 --install-dir ./.dotnet

./.dotnet/dotnet publish src/LabelMaker/LabelMaker.csproj -c Release -o dist
