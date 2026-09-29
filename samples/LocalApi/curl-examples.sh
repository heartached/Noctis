#!/bin/sh
# Noctis Local API with curl (Linux / macOS / Git Bash). Reference: docs/LOCAL-API.md
# Turn the API on first: Settings > Account & Devices > Local API.
#
# Discovery: local-api.json in the Noctis data folder holds the port and token.
#   Windows: %APPDATA%\Noctis   Linux / macOS: ~/.config/Noctis (or $XDG_CONFIG_HOME/Noctis)
# (NOCTIS_DATA_DIR overrides it, as for the app.) Needs jq for the discovery step;
# otherwise set PORT and TOKEN by hand from Settings.
set -eu

DATA="${NOCTIS_DATA_DIR:-${APPDATA:-${XDG_CONFIG_HOME:-$HOME/.config}}/Noctis}"
PORT="${PORT:-$(jq -r .port "$DATA/local-api.json")}"
TOKEN="${TOKEN:-$(jq -r .token "$DATA/local-api.json")}"
API="http://127.0.0.1:$PORT/api/v1"
AUTH="Authorization: Bearer $TOKEN"

curl -s -H "$AUTH" "$API/status"; echo
curl -s -H "$AUTH" "$API/now-playing"; echo

# Controls are POST-only.
curl -s -X POST -H "$AUTH" "$API/playback/toggle"; echo
curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" -d '{"volume": 40}' "$API/playback/volume"; echo
curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" -d '{"positionMs": 30000}' "$API/playback/seek"; echo
curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" -d '{"mode": "all"}' "$API/playback/repeat"; echo

# Search, then queue the first hit to play next.
ID=$(curl -s -H "$AUTH" "$API/library/search?q=midnight&limit=1" | jq -r '.tracks[0].id')
curl -s -X POST -H "$AUTH" -H "Content-Type: application/json" \
     -d "{\"trackIds\": [\"$ID\"], \"mode\": \"next\"}" "$API/queue/add"; echo

# Cover of the current track to a file.
curl -s -H "$AUTH" -o cover.img "$API/artwork/current"

# Live events (Ctrl+C to stop). -N turns off buffering.
curl -N -s -H "$AUTH" "$API/events?lyrics=1"
