#!/usr/bin/env bash
# Starts a throwaway Jellyfin 12 server with the plugin, static comskip and one demo recording,
# completes the setup wizard, and analyzes the recording. Used only to take README screenshots.
set -euo pipefail

DLL="$1"                       # path to the built Jellyfin.Plugin.Comskip.dll
ROOT="${2:-$PWD/demo}"
URL=http://localhost:8096
PLUGIN_ID=bc780563-77ca-4c84-bb79-8993c65bd271
AUTH_BASE='MediaBrowser Client="screenshots", Device="ci", DeviceId="screenshots-ci", Version="1.0"'

mkdir -p "$ROOT/config/plugins/Comskip_2.0.0.0" "$ROOT/config/comskip" "$ROOT/media/Demo Show/Season 01"
cp "$DLL" "$ROOT/config/plugins/Comskip_2.0.0.0/"
curl -fsSL -o "$ROOT/config/comskip/comskip" \
  https://github.com/${GITHUB_REPOSITORY:-zenmaster14/jellyfin-plugin-comskip}/releases/download/comskip-static/comskip-linux-x64
chmod 755 "$ROOT/config/comskip/comskip"

# A 3-minute "recording": show / black-and-silent ad break / show.
ffmpeg -hide_banner -v error -y \
  -f lavfi -i "testsrc2=s=1280x720:r=30000/1001:d=75" -f lavfi -i "sine=f=440:d=75" \
  -f lavfi -i "color=c=black:s=1280x720:r=30000/1001:d=35" -f lavfi -i "sine=f=440:d=35,volume=0" \
  -f lavfi -i "testsrc2=s=1280x720:r=30000/1001:d=70" -f lavfi -i "sine=f=550:d=70" \
  -filter_complex "[0:v][1:a][2:v][3:a][4:v][5:a]concat=n=3:v=1:a=1[v][a]" -map "[v]" -map "[a]" \
  -c:v mpeg2video -b:v 4M -g 15 -c:a ac3 -f mpegts "$ROOT/media/Demo Show/Season 01/Demo Show S01E01.ts"
touch -d '1 hour ago' "$ROOT/media/Demo Show/Season 01/Demo Show S01E01.ts"

docker run -d --name jellyfin -p 8096:8096 -v "$ROOT/config:/config" -v "$ROOT/media:/media:ro" jellyfin/jellyfin:latest
# Public info answers while first-start database migrations are still running; wait until the
# setup wizard endpoint itself responds.
for i in $(seq 1 150); do curl -fs "$URL/Startup/Configuration" >/dev/null 2>&1 && break; sleep 2; done
curl -fs "$URL/System/Info/Public"; echo

# Setup wizard
curl -fs "$URL/Startup/Configuration" >/dev/null
curl -fs -X POST "$URL/Startup/Configuration" -H 'Content-Type: application/json' \
  -d '{"ServerName":"Demo","UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
curl -fs "$URL/Startup/User" >/dev/null
curl -fs -X POST "$URL/Startup/User" -H 'Content-Type: application/json' -d '{"Name":"admin","Password":"demo-password"}'
curl -fs -X POST "$URL/Startup/Complete"

LOGIN=$(curl -fs -X POST "$URL/Users/AuthenticateByName" -H 'Content-Type: application/json' \
  -H "Authorization: $AUTH_BASE" -d '{"Username":"admin","Pw":"demo-password"}')
TOKEN=$(echo "$LOGIN" | python3 -c 'import json,sys;print(json.load(sys.stdin)["AccessToken"])')
USER_ID=$(echo "$LOGIN" | python3 -c 'import json,sys;print(json.load(sys.stdin)["User"]["Id"])')
H="Authorization: $AUTH_BASE, Token=\"$TOKEN\""

curl -fs -X POST "$URL/Library/VirtualFolders?name=Recorded%20TV&collectionType=tvshows&paths=/media&refreshLibrary=true" \
  -H "$H" -H 'Content-Type: application/json' -d '{"LibraryOptions":{}}'
LIB_ID=$(curl -fs "$URL/Library/VirtualFolders" -H "$H" | python3 -c 'import json,sys;print([f["ItemId"] for f in json.load(sys.stdin) if f["Name"]=="Recorded TV"][0])')

# Plugin settings: analyze the demo library straight away.
curl -fs "$URL/Plugins/$PLUGIN_ID/Configuration" -H "$H" | python3 -c "
import json,sys
c=json.load(sys.stdin); c['SelectedLibraryIds']=['$LIB_ID'.replace('-','')]; c['SettleSeconds']=0
json.dump(c, open('/tmp/plugin-config.json','w'))"
curl -fs -X POST "$URL/Plugins/$PLUGIN_ID/Configuration" -H "$H" -H 'Content-Type: application/json' -d @/tmp/plugin-config.json

# Wait for the library scan to find the episode, then run the Comskip task until it is analyzed.
for i in $(seq 1 60); do
  n=$(curl -fs "$URL/Items?Recursive=true&IncludeItemTypes=Episode&userId=$USER_ID" -H "$H" | python3 -c 'import json,sys;print(json.load(sys.stdin)["TotalRecordCount"])')
  [ "$n" -ge 1 ] && break; sleep 3
done
TASK_ID=$(curl -fs "$URL/ScheduledTasks" -H "$H" | python3 -c 'import json,sys;print([t["Id"] for t in json.load(sys.stdin) if t.get("Key")=="ComskipDetectCommercials"][0])')
curl -fs -X POST "$URL/ScheduledTasks/Running/$TASK_ID" -H "$H"
for i in $(seq 1 90); do
  s=$(curl -fs "$URL/Comskip/Status" -H "$H")
  echo "$s" | python3 -c 'import json,sys;s=json.load(sys.stdin);print("analyzed",s["AnalyzedItems"],"comskip",s["ComskipPath"])'
  echo "$s" | python3 -c 'import json,sys;sys.exit(0 if json.load(sys.stdin)["AnalyzedItems"]>=1 else 1)' && break
  sleep 4
done

echo "URL=$URL" >> "${GITHUB_ENV:-/dev/null}"
echo "JF_USER_ID=$USER_ID" >> "${GITHUB_ENV:-/dev/null}"
