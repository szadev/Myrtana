#!/bin/bash
# World remaster v2 isolation shots (Docs/remaster-world.md 10.14). Run from . in Play, world remaster ON,
# Faring noon Sunny, HUD off. Per step: patch Resources/G3WorldLook.json, G3WorldRemasterLook.Reload(), one B shot of
# the vista (W=0 cam.sh) -> Docs/screens/v2dbg_<step>.png. The JSON is restored from git at the end.
#   bash Tools/Remaster/v2_debug_shots.sh [cam.sh args...]   (default: the vista line)
cd /d/G3World
J=Assets/G3/Unity/Runtime/Remaster/Resources/G3WorldLook.json
CAM=${*:-"600 525 =110 450 420 =55"}
set_json() { python -I - "$J" "$1" <<'PY'
import json, sys
p, kv = sys.argv[1], json.loads(sys.argv[2])
d = json.load(open(p, encoding='utf-8')); d.update(kv)
open(p, 'w', encoding='utf-8').write(json.dumps(d, indent=2) + '\n')
PY
}
shoot() {   # name json
  git checkout -- "$J"; set_json "$2"
  bash Tools/eval_job.sh 'return G3.Unity.G3WorldRemasterLook.Reload();' | head -1
  sleep 3
  W=0 bash Work/scratch/r29/cam.sh "v2dbg_$1" $CAM
}
shoot base          '{}'
for m in 2 3 4 5 6 7 8 9; do shoot "mode$m" "{\"v2DebugMode\": $m}"; done
shoot no_hex        '{"v2Stochastic": false}'
shoot no_pom        '{"v2Parallax": false}'
shoot no_rockmulti  '{"v2RockTriplanar": false}'
shoot no_farfade    '{"v2DetailFar": 1.0}'
shoot no_ssao       '{"v2Ssao": false}'
shoot no_fog        '{"v2Fog": false, "v2Shafts": false}'
shoot no_surfaces   '{"v2Surfaces": false}'
shoot no_terrain    '{"v2Terrain": false}'
shoot demo_nogrid   "$(cat Tools/Remaster/v2_presets/demo_nogrid.json)"
git checkout -- "$J"
bash Tools/eval_job.sh 'return G3.Unity.G3WorldRemasterLook.Reload();' | head -1
ls -la Docs/screens/v2dbg_*.png | awk '{print $5, $NF}'
