#!/usr/bin/env bash
# Runs once after the container is created. Restores project deps and installs
# the opencode CLI.
set -euo pipefail

echo "==> Restoring .NET tools"
dotnet tool restore

echo "==> Restoring .NET dependencies"
dotnet restore

echo "==> Installing agent CLIs globally (opencode, claude-code, codex)"
npm install -g opencode-ai @anthropic-ai/claude-code @openai/codex

echo "==> Done."
