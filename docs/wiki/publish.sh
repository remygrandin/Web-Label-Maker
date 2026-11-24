#!/usr/bin/env bash
# Publish docs/wiki to the GitHub wiki.
#
# GitHub only creates the wiki's git repository once a first page exists, so the very
# first time: open https://github.com/remygrandin/Web-Label-Maker/wiki, click
# "Create the first page" and save it as it is. Then run this script; it replaces that page.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git clone --quiet https://github.com/remygrandin/Web-Label-Maker.wiki.git "$work"
# everything in this folder except this script is wiki content
find "$work" -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
cp -r "$here"/. "$work"/
rm -f "$work/publish.sh"

cd "$work"
git add -A
if git diff --cached --quiet; then
    echo "The wiki is already up to date."
    exit 0
fi
git commit --quiet -m "Update documentation from docs/wiki"
git push --quiet
echo "Wiki published: https://github.com/remygrandin/Web-Label-Maker/wiki"
