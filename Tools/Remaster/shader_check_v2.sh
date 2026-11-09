#!/bin/bash
# Offline compile check of the v2 hero shaders (G3RemasterV2.hlsl: Skin, Eye, Hair + fringe pass; EyeOcclusion) with the
# Windows SDK's dxc (SM 6.0, D3D11 API macros), like shader_check.sh for R1. Not Unity's compiler: the editor's own
# compile is still the final word (runner checklist).
# Usage: bash Tools/Remaster/shader_check_v2.sh [project-with-Library]   (default .; exit 0 = OK)
REPO=$(cd "$(dirname "$0")/../.." && pwd -W)
PROJECT=${1:-.}
DXC=$(ls "/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/dxc.exe 2>/dev/null | tail -1)
[ -x "$DXC" ] || { echo "dxc.exe not found (Windows SDK)"; exit 1; }
W=$REPO/Work/tmp/shader_check_v2
SH=$REPO/Assets/G3/Unity/Shaders/Remaster
mkdir -p "$W/inc/Packages"
links=()
for name in com.unity.render-pipelines.universal com.unity.render-pipelines.core com.unity.render-pipelines.universal-config; do
  src=$(ls -d "$PROJECT/Library/PackageCache/$name@"* 2>/dev/null | head -1)
  [ -n "$src" ] || { echo "missing package $name in $PROJECT/Library/PackageCache"; exit 1; }
  [ -e "$W/inc/Packages/$name" ] || cmd //c "mklink /J $(cygpath -w "$W/inc/Packages/$name") $(cygpath -w "$src")" >/dev/null
  links+=("$W/inc/Packages/$name")
done
cleanup() { for l in "${links[@]}"; do cmd //c "rmdir $(cygpath -w "$l")" >/dev/null 2>&1; done; }
trap cleanup EXIT

compile() { # target entry stage file
  "$DXC" -nologo -HV 2018 -T "$1" -E "$2" -D "$3=1" -I "$W/inc" -Fo "$W/out.bin" "$(cygpath -w "$4")" 2>&1 | grep -i "error" | head -8
}
header() { printf '#define SHADER_API_D3D11 1\n#define UNITY_VERSION 60006\n#define SHADER_TARGET 45\n'; }

fail=0
for kind in SKIN EYE HAIR HAIR_FRINGE; do
  for variant in "" "_ADDITIONAL_LIGHTS _MAIN_LIGHT_SHADOWS_CASCADE _SHADOWS_SOFT _SCREEN_SPACE_OCCLUSION _LIGHT_LAYERS" \
                 "_ADDITIONAL_LIGHTS _CLUSTER_LIGHT_LOOP _LIGHT_LAYERS _REFLECTION_PROBE_BLENDING _REFLECTION_PROBE_BOX_PROJECTION"; do
    f=$W/forward_$kind.hlsl
    {
      header
      for k in $variant; do echo "#define $k 1"; done
      case $kind in
        HAIR) echo "#define G3_V2_HAIR 1";;
        HAIR_FRINGE) echo "#define G3_V2_HAIR 1"; echo "#define G3_V2_FRINGE 1";;
        *) echo "#define G3_V2_$kind 1";;
      esac
      echo "#include \"$SH/G3RemasterV2.hlsl\""
      echo "#define G3_V2_FORWARD 1"
      echo "#include \"$SH/G3RemasterV2.hlsl\""
    } > "$f"
    for stage in "vs_6_0 V2Vert SHADER_STAGE_VERTEX" "ps_6_0 V2Frag SHADER_STAGE_FRAGMENT"; do
      set -- $stage
      out=$(compile $1 $2 $3 "$f")
      [ -n "$out" ] && { echo "FAIL $kind [$variant] $1"; echo "$out"; fail=1; }
    done
  done
done
for pass in "ShadowCasterPass ShadowPassVertex ShadowPassFragment" "DepthOnlyPass DepthOnlyVertex DepthOnlyFragment" \
            "DepthNormalsPass DepthNormalsVertex DepthNormalsFragment"; do
  set -- $pass
  for at in "" "#define _ALPHATEST_ON 1"; do
    f=$W/$1.hlsl
    { header; echo "$at"; echo "#define G3_V2_HAIR 1"
      echo "#include \"$SH/G3RemasterV2.hlsl\""
      echo "#include \"Packages/com.unity.render-pipelines.universal/Shaders/$1.hlsl\""; } > "$f"
    out=$(compile vs_6_0 $2 SHADER_STAGE_VERTEX "$f"; compile ps_6_0 $3 SHADER_STAGE_FRAGMENT "$f")
    [ -n "$out" ] && { echo "FAIL $1 [$at]"; echo "$out"; fail=1; }
  done
done
# the occlusion shell: its HLSLPROGRAM block as a file
f=$W/occlusion.hlsl
{ header; sed -n '/HLSLPROGRAM/,/ENDHLSL/p' "$SH/G3RemasterV2EyeOcclusion.shader" | grep -v -E 'HLSLPROGRAM|ENDHLSL|#pragma|include_with_pragmas'; } > "$f"
out=$(compile vs_6_0 OccVert SHADER_STAGE_VERTEX "$f"; compile ps_6_0 OccFrag SHADER_STAGE_FRAGMENT "$f")
[ -n "$out" ] && { echo "FAIL EyeOcclusion"; echo "$out"; fail=1; }
[ $fail = 0 ] && echo "shader check v2: all variants compile"
exit $fail
