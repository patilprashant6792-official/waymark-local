#!/bin/bash
# Installs, updates, starts, stops and removes Waymark for the current macOS user. The macOS counterpart of waymark.ps1.
#
#   install    builds Waymark from this repository into ~/Library/Application Support/Waymark/app, starts it when you sign in, and opens it.
#   update     pulls the latest version of the repository and installs it. Your data is not touched.
#   uninstall  stops and removes Waymark. Your data stays unless --remove-data is given.
#   start, stop, open, status   do what they say.
#   run        is what launchd calls at sign-in: it starts the program and keeps a short last-run log.
#
# Your data (database, encrypted key ring, secrets) lives in ~/Library/Application Support/Waymark and is never part of the app folder.
# Waymark runs as you, as a LaunchAgent, not as a system daemon: its Keychain, folders and git credentials belong to your account.
#
# Written for the bash 3.2 that macOS ships: no associative arrays, no ${var,,}, no mapfile, and no empty "${array[@]}" under set -u.

set -euo pipefail

ADMIN_PORT="${Waymark__AdminPort:-47100}"
MCP_PORT="${Waymark__McpPort:-47101}"
ADMIN_URL="http://localhost:${ADMIN_PORT}"

LABEL="com.waymark.host"
DATA_DIR="${HOME}/Library/Application Support/Waymark"
APP_DIR="${DATA_DIR}/app"
STAGING_DIR="${DATA_DIR}/app.new"
LOG_DIR="${DATA_DIR}/logs"
EXE="${APP_DIR}/Waymark.Host"
INSTALLED_SCRIPT="${DATA_DIR}/waymark.sh"
REPO_FILE="${DATA_DIR}/repo.path"
VERSION_FILE="${DATA_DIR}/version.txt"
PLIST="${HOME}/Library/LaunchAgents/${LABEL}.plist"
DOMAIN="gui/$(id -u)"
SERVICE="${DOMAIN}/${LABEL}"
APP_BUNDLE="${HOME}/Applications/Waymark.app"
KEYCHAIN_SERVICE="waymark-data-protection"
KEYCHAIN_ACCOUNT="waymark"

# The .NET SDK, Homebrew and a per-user SDK are often not on the PATH of a fresh terminal or of launchd.
export PATH="${PATH}:${HOME}/.dotnet:/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin"

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
good() { printf '\033[32m    %s\033[0m\n' "$1"; }
note() { printf '\033[33m    %s\033[0m\n' "$1"; }
fail() {
    printf '\n\033[31mWaymark: %s\033[0m\n' "$1" >&2
    exit 1
}

assert_macos() {
    if [ "$(uname -s)" != "Darwin" ]; then
        fail "This script is for macOS. On Windows use install.cmd."
    fi
}

# Any answer from the admin address, even an error page, means the program is up.
test_up() {
    curl -s -o /dev/null --max-time 2 "${ADMIN_URL}"
}

wait_up() {
    local deadline=$(( $(date +%s) + $1 ))
    while [ "$(date +%s)" -lt "${deadline}" ]; do
        if test_up; then
            return 0
        fi
        sleep 0.5
    done
    return 1
}

# The full path of a program, following every link (macOS readlink has no -f before 12.3).
resolve_path() {
    local path="$1" target
    while [ -L "${path}" ]; do
        target="$(readlink "${path}")"
        case "${target}" in
            /*) path="${target}" ;;
            *) path="$(dirname "${path}")/${target}" ;;
        esac
    done
    printf '%s/%s' "$(cd -P "$(dirname "${path}")" && pwd -P)" "$(basename "${path}")"
}

xml_escape() {
    printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g'
}

is_loaded() {
    launchctl print "${SERVICE}" >/dev/null 2>&1
}

# Running copies of the installed program, also one started by hand outside launchd.
running_pids() {
    local pid
    for pid in $(pgrep -x Waymark.Host 2>/dev/null || true); do
        if [ "$(ps -p "${pid}" -o comm= 2>/dev/null || true)" = "${EXE}" ]; then
            printf '%s\n' "${pid}"
        fi
    done
}

assert_ports_free() {
    local port pid owner name
    for port in "${ADMIN_PORT}" "${MCP_PORT}"; do
        pid="$(lsof -nP -iTCP:"${port}" -sTCP:LISTEN -t 2>/dev/null | head -n 1 || true)"
        if [ -z "${pid}" ]; then
            continue
        fi
        owner="$(ps -p "${pid}" -o comm= 2>/dev/null || true)"
        if [ "${owner}" = "${EXE}" ]; then
            continue
        fi
        name="$(basename "${owner:-another program}")"
        fail "Port ${port} is already used by ${name} (pid ${pid}). Close it, or give Waymark other ports with the environment variables Waymark__AdminPort and Waymark__McpPort, and run this again."
    done
}

has_sdk() {
    command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'
}

# Microsoft's own script installs the SDK for this user into ~/.dotnet: no administrator rights, and exactly the 10.0 channel
# (Homebrew's dotnet-sdk cask follows the newest release, which global.json does not accept).
install_sdk() {
    if has_sdk; then
        good ".NET 10 SDK found."
        return
    fi

    note "The .NET 10 SDK is missing. Installing it into ~/.dotnet with Microsoft's dotnet-install.sh (this can take a few minutes)..."
    local installer
    installer="$(mktemp -t dotnet-install)"
    if ! curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${installer}"; then
        rm -f "${installer}"
        fail "dotnet-install.sh could not be downloaded. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again."
    fi
    if ! bash "${installer}" --channel 10.0 --install-dir "${HOME}/.dotnet" --no-path; then
        rm -f "${installer}"
        fail "The .NET 10 SDK could not be installed. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again."
    fi
    rm -f "${installer}"

    export DOTNET_ROOT="${HOME}/.dotnet"
    export PATH="${HOME}/.dotnet:${PATH}"
    has_sdk || fail "The .NET 10 SDK was installed but cannot be used. Open a new terminal and run this again."
    good ".NET 10 SDK installed in ~/.dotnet."
}

find_repo() {
    local candidate
    for candidate in "$(cd "$(dirname "$0")/.." && pwd)" "$(cat "${REPO_FILE}" 2>/dev/null || true)"; do
        if [ -n "${candidate}" ] && [ -f "${candidate}/src/Waymark.Host/Waymark.Host.csproj" ]; then
            printf '%s' "${candidate}"
            return
        fi
    done
    fail "The Waymark repository was not found. Run install.sh from the cloned repository."
}

# The PATH of the user's own interactive login shell, so a program launchd starts finds what a terminal finds (Homebrew, nvm, pyenv).
# It runs in the foreground on purpose: an interactive shell sent to the background is stopped as soon as it touches the terminal.
# Its input is empty, so a profile that asks a question reads end-of-file instead of waiting. If it fails, the current PATH is used.
login_path() {
    local output found
    output="$(mktemp -t waymark-path)"
    "${SHELL:-/bin/zsh}" -ilc 'printf "\n__WAYMARK_PATH__%s\n" "$PATH"' </dev/null >"${output}" 2>/dev/null || true
    found="$(sed -n 's/^__WAYMARK_PATH__//p' "${output}" | tail -n 1)"
    rm -f "${output}"
    printf '%s' "${found:-${PATH}}"
}

# Waymark starts ngrok as a child program. These are the ngrok programs Waymark itself started ("http <MCP port> ... --log=stdout");
# an ngrok you started by hand does not match and is left alone.
tunnel_pids() {
    pgrep -f "ngrok http ${MCP_PORT} .*--log=stdout" 2>/dev/null || true
}

stop_tunnel() {
    local pid
    for pid in $(tunnel_pids); do
        kill "${pid}" 2>/dev/null || true
    done
}

stop_waymark() {
    local pid waited
    if is_loaded; then
        launchctl bootout "${SERVICE}" 2>/dev/null || true
    fi

    for pid in $(running_pids); do
        kill "${pid}" 2>/dev/null || true
        waited=0
        while kill -0 "${pid}" 2>/dev/null && [ "${waited}" -lt 30 ]; do
            sleep 0.5
            waited=$(( waited + 1 ))
        done
        kill -9 "${pid}" 2>/dev/null || true
    done

    stop_tunnel
}

# Builds into a side folder first, so a failed build leaves the working install exactly as it was.
publish_waymark() {
    local repo="$1"
    rm -rf "${STAGING_DIR}"
    mkdir -p "${STAGING_DIR}"

    step "Building Waymark (the first time takes a minute or two)"
    # Warnings are not errors here: a newer SDK may report warnings the project was not written against.
    if ! (cd "${repo}" && dotnet publish "src/Waymark.Host/Waymark.Host.csproj" -c Release -o "${STAGING_DIR}" --nologo -v minimal -p:TreatWarningsAsErrors=false); then
        rm -rf "${STAGING_DIR}"
        fail "The build failed. Nothing was changed: the installed version, if there is one, still works."
    fi
}

# The LaunchAgent: started at sign-in as you, started again after a crash (not after a clean stop), at most once a minute.
# AbandonProcessGroup lets a detached update script outlive the Waymark it restarts. DOTNET_ROOT tells the program where its runtime is.
write_plist() {
    local service_path dotnet_root extra name value
    service_path="$(login_path):/usr/bin:/bin:/usr/sbin:/sbin:${HOME}/.dotnet:/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin"
    dotnet_root="$(dirname "$(resolve_path "$(command -v dotnet)")")"

    extra=""
    for name in Waymark__AdminPort Waymark__McpPort Waymark__DataDirectory LANG LC_ALL LC_CTYPE; do
        value="$(printenv "${name}" 2>/dev/null || true)"
        if [ -n "${value}" ]; then
            extra="${extra}        <key>${name}</key><string>$(xml_escape "${value}")</string>
"
        fi
    done

    mkdir -p "$(dirname "${PLIST}")"
    cat >"${PLIST}" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>${LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>/bin/bash</string>
        <string>$(xml_escape "${INSTALLED_SCRIPT}")</string>
        <string>run</string>
    </array>
    <key>WorkingDirectory</key><string>$(xml_escape "${APP_DIR}")</string>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
    <key>ThrottleInterval</key><integer>60</integer>
    <key>ProcessType</key><string>Interactive</string>
    <key>AbandonProcessGroup</key><true/>
    <key>EnvironmentVariables</key>
    <dict>
        <key>PATH</key><string>$(xml_escape "${service_path}")</string>
        <key>DOTNET_ROOT</key><string>$(xml_escape "${dotnet_root}")</string>
${extra}    </dict>
</dict>
</plist>
EOF
    plutil -lint "${PLIST}" >/dev/null || fail "The LaunchAgent file ${PLIST} is not valid; nothing was started."
    good "Waymark will start when you sign in (LaunchAgent ${LABEL})."
}

# A small app in ~/Applications that opens the admin page, so Waymark can be found in Spotlight and Launchpad. Best effort.
write_app_bundle() {
    local source
    source="do shell script \"/bin/bash \" & quoted form of \"${INSTALLED_SCRIPT}\" & \" open > /dev/null 2>&1 &\""
    mkdir -p "$(dirname "${APP_BUNDLE}")"
    rm -rf "${APP_BUNDLE}"
    if osacompile -o "${APP_BUNDLE}" -e "${source}" >/dev/null 2>&1; then
        good "App added: ~/Applications/Waymark.app (opens the admin page)."
    else
        note "The Waymark app in ~/Applications could not be created; use the open command instead."
    fi
}

start_waymark() {
    if test_up; then
        return
    fi
    assert_ports_free
    if [ ! -f "${PLIST}" ]; then
        fail "The LaunchAgent is missing. Run install.sh from the repository."
    fi
    if is_loaded; then
        launchctl kickstart "${SERVICE}" >/dev/null 2>&1 || true
    else
        launchctl bootstrap "${DOMAIN}" "${PLIST}" || fail "launchd refused to load ${PLIST}."
    fi
}

show_log() {
    local name file
    for name in host.err.log host.log; do
        file="${LOG_DIR}/${name}"
        if [ -s "${file}" ]; then
            printf '\033[33m--- %s (last lines) ---\033[0m\n' "${name}"
            tail -n 20 "${file}"
        fi
    done
}

invoke_install() {
    local pull="$1" repo version had_tunnel=0
    assert_macos
    repo="$(find_repo)"

    step "Checking the .NET 10 SDK"
    install_sdk

    if [ "${pull}" = "1" ]; then
        step "Getting the latest version"
        command -v git >/dev/null 2>&1 || fail "git is needed to update."
        git -C "${repo}" pull --ff-only || fail "git pull did not finish cleanly (local changes?). Fix that and run update again."
    fi

    mkdir -p "${DATA_DIR}"
    chmod 700 "${DATA_DIR}"
    publish_waymark "${repo}"

    step "Installing"
    if [ -n "$(tunnel_pids)" ]; then
        had_tunnel=1
    fi
    stop_waymark
    rm -rf "${APP_DIR}"
    mv "${STAGING_DIR}" "${APP_DIR}"
    cp "${repo}/scripts/waymark.sh" "${INSTALLED_SCRIPT}"
    chmod 755 "${INSTALLED_SCRIPT}"
    printf '%s\n' "${repo}" >"${REPO_FILE}"

    version="unknown"
    if command -v git >/dev/null 2>&1; then
        version="$(git -C "${repo}" rev-parse --short HEAD 2>/dev/null || printf 'unknown')"
    fi
    printf '%s\n' "${version}" >"${VERSION_FILE}"

    write_plist
    write_app_bundle

    step "Starting Waymark"
    start_waymark
    if ! wait_up 90; then
        show_log
        fail "Waymark did not answer at ${ADMIN_URL} within 90 seconds. The last lines of its log are above (folder: ${LOG_DIR})."
    fi

    good "Waymark is running at ${ADMIN_URL} (version ${version})."
    if [ "${had_tunnel}" = "1" ]; then
        good "The ngrok tunnel was stopped for the update and Waymark starts it again by itself; the Setup page shows when it is online."
    fi
    note "macOS asks before a program reads Documents, Desktop, Downloads or iCloud Drive. Keep your projects elsewhere (~/source, say),"
    note "or give Waymark.Host Full Disk Access in System Settings > Privacy & Security."
    open "${ADMIN_URL}"
}

invoke_uninstall() {
    local remove_data="$1" answer
    assert_macos
    step "Stopping Waymark"
    stop_waymark

    rm -f "${PLIST}"
    rm -rf "${APP_BUNDLE}" "${APP_DIR}" "${STAGING_DIR}"
    rm -f "${REPO_FILE}" "${VERSION_FILE}"
    good "Waymark was removed."

    if [ "${remove_data}" = "1" ]; then
        printf 'This deletes the database, the key ring and every stored secret in %s. Type DELETE to continue: ' "${DATA_DIR}"
        read -r answer || answer=""
        if [ "${answer}" = "DELETE" ]; then
            rm -rf "${DATA_DIR}"
            # Without the data the master key opens nothing, so it goes too.
            security delete-generic-password -s "${KEYCHAIN_SERVICE}" -a "${KEYCHAIN_ACCOUNT}" >/dev/null 2>&1 || true
            good "Your data and the Waymark master key in the login Keychain were deleted."
            return
        fi
        note "Nothing was typed that matches, so your data was kept."
    fi

    rm -f "${INSTALLED_SCRIPT}"
    good "Your data is kept in ${DATA_DIR} (database, key ring, secrets), and its master key in your login Keychain. Run uninstall.sh --remove-data to delete them as well."
}

invoke_open() {
    [ -x "${EXE}" ] || fail "Waymark is not installed. Run install.sh from the repository."
    start_waymark
    if ! wait_up 60; then
        show_log
        fail "Waymark did not answer at ${ADMIN_URL} within 60 seconds."
    fi
    open "${ADMIN_URL}"
}

# What launchd runs at sign-in. Only warnings, errors and the start-up lines reach the log, which keeps it small; the Live page is not affected.
# exec hands the process over, so launchd watches Waymark itself and its stop signal reaches it directly.
invoke_run() {
    [ -x "${EXE}" ] || fail "Waymark is not installed. Run install.sh from the repository."
    mkdir -p "${LOG_DIR}"
    local out="${LOG_DIR}/host.log" err="${LOG_DIR}/host.err.log"
    if [ -f "${out}" ]; then mv -f "${out}" "${LOG_DIR}/host.previous.log"; fi
    if [ -f "${err}" ]; then mv -f "${err}" "${LOG_DIR}/host.err.previous.log"; fi

    cd "${APP_DIR}"
    # A name with dots cannot be exported by bash, so env sets the Microsoft.Hosting.Lifetime level for the program alone.
    exec env "Logging__LogLevel__Default=Warning" "Logging__LogLevel__Microsoft.Hosting.Lifetime=Information" "${EXE}" >>"${out}" 2>>"${err}"
}

show_status() {
    local autostart="none" running="no"
    if [ -x "${EXE}" ]; then printf 'Installed:   yes\n'; else printf 'Installed:   no\n'; fi
    if [ -f "${VERSION_FILE}" ]; then printf 'Version:     %s\n' "$(cat "${VERSION_FILE}")"; fi
    if [ -f "${PLIST}" ]; then
        if is_loaded; then autostart="LaunchAgent ${LABEL} (loaded)"; else autostart="LaunchAgent ${LABEL} (not loaded)"; fi
    fi
    printf 'Autostart:   %s\n' "${autostart}"
    if test_up; then running="yes, at ${ADMIN_URL}"; fi
    printf 'Running:     %s\n' "${running}"
    printf 'Data folder: %s\n' "${DATA_DIR}"
}

COMMAND="${1:-status}"
if [ "$#" -gt 0 ]; then
    shift
fi

REMOVE_DATA=0
for argument in "$@"; do
    case "${argument}" in
        --remove-data | -RemoveData) REMOVE_DATA=1 ;;
        *) fail "Unknown option: ${argument}" ;;
    esac
done

case "${COMMAND}" in
    install) invoke_install 0 ;;
    update) invoke_install 1 ;;
    uninstall) invoke_uninstall "${REMOVE_DATA}" ;;
    start)
        [ -x "${EXE}" ] || fail "Waymark is not installed. Run install.sh from the repository."
        start_waymark
        if ! wait_up 60; then
            show_log
            fail "Waymark did not answer within 60 seconds."
        fi
        good "Waymark is running at ${ADMIN_URL}"
        ;;
    stop)
        stop_waymark
        good "Waymark was stopped."
        ;;
    open) invoke_open ;;
    status) show_status ;;
    run) invoke_run ;;
    *) fail "Unknown command '${COMMAND}'. Use install, update, uninstall, start, stop, open, status or run." ;;
esac
