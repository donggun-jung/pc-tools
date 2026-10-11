#!/usr/bin/env bash
# pc-tools page deploy — sync site/ to the VPS ro-mount (idempotent, no container restart needed).
# Usage: scripts/deploy.sh          (from repo root)
# Requires: ssh alias `tgbot`, rsync. Server dir is the nginx ro bind-mount source.
# Adding or releasing a tool does NOT need this: the page reads catalog.json from GitHub at runtime.
# Run it only when site/ itself changes (layout, copy) — and keep site/catalog.json in sync with
# the root catalog.json first (it is the fallback the page uses when GitHub is unreachable).
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f site/index.html ] || { echo "site/index.html not found — run from repo root"; exit 1; }
node tests/site-app.test.js >/dev/null || { echo "site tests failed — run: node tests/site-app.test.js"; exit 1; }
cmp -s catalog.json site/catalog.json || { echo "site/catalog.json differs from catalog.json — copy it first"; exit 1; }
rsync -rlcv --delete --exclude '.DS_Store' site/ tgbot:/opt/pc-tools/site/
echo "--- verify ---"
curl -s -o /dev/null -w "root:    %{http_code}\n" https://tools.jdg.dev/
curl -s -o /dev/null -w "health:  %{http_code}\n" https://tools.jdg.dev/health
curl -s -o /dev/null -w "catalog: %{http_code}\n" https://tools.jdg.dev/catalog.json
curl -s -o /dev/null -w "app.js:  %{http_code}\n" https://tools.jdg.dev/app.js
