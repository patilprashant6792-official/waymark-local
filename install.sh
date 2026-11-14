#!/bin/bash
# Builds and installs Waymark for the current macOS user, starts it and opens it. Run it from a terminal: bash install.sh
# bash install.sh status (or start, stop, open) runs that command of scripts/waymark.sh instead and installs nothing.
script="$(cd "$(dirname "$0")" && pwd)/scripts/waymark.sh"
case "${1:-}" in
    status | start | stop | open) exec /bin/bash "${script}" "$@" ;;
esac
exec /bin/bash "${script}" install "$@"
