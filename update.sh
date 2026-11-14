#!/bin/bash
# Pulls the latest version of the repository and installs it on macOS. Your data is not touched. Run it from a terminal: bash update.sh
exec /bin/bash "$(cd "$(dirname "$0")" && pwd)/scripts/waymark.sh" update "$@"
