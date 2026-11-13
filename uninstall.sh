#!/bin/bash
# Removes Waymark from macOS. Your data stays unless you run: bash uninstall.sh --remove-data
exec /bin/bash "$(cd "$(dirname "$0")" && pwd)/scripts/waymark.sh" uninstall "$@"
