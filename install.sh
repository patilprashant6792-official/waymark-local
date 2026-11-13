#!/bin/bash
# Builds and installs Waymark for the current macOS user, starts it and opens it. Run it from a terminal: bash install.sh
exec /bin/bash "$(cd "$(dirname "$0")" && pwd)/scripts/waymark.sh" install "$@"
