#!/bin/bash
# Offline compile check of the R1 remaster shaders (Docs/remaster-pipeline.md §9.3) with the Windows SDK's dxc
# (SM 6.0, D3D11 API macros): 4 shaders x 3 keyword sets (forward pass) + URP's shadow/depth passes on our cbuffer.
# Not Unity's compiler, but catches undefined identifiers, signature and type errors without an editor.
# Usage: bash Tools/Remaster/shader_check.sh [project-with-Library]   (default .; exit 0 = OK)
# Package includes are resolved through temporary directory junctions under Work/tmp/shader_check/inc/Packages that
# point into <project>/Library/PackageCache; they are removed with `rmdir` (link only) at the end.
REPO=$(cd "$(dirname "$0")/../.." && pwd -W)
PROJECT=${1:-.}
DXC=$(ls "/c/Program Files (x86)/Windows Kits/10/bin/"*/x64/dxc.exe 2>/dev/null | tail -1)
[ -x "$DXC" ] || { echo "dxc.exe not found (Windows SDK)"; exit 1; }
W=$REPO/Work/tmp/shader_check
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
for kind in ARMOUR SKIN HAIR EYE; do
  for variant in "" "_ADDITIONAL_LIGHTS _MAIN_LIGHT_SHADOWS_CASCADE _SHADOWS_SOFT _SCREEN_SPACE_OCCLUSION _LIGHT_LAYERS" \
                 "_ADDITIONAL_LIGHTS _CLUSTER_LIGHT_LOOP _LIGHT_LAYERS _REFLECTION_PROBE_BLENDING _REFLECTION_PROBE_BOX_PROJECTION"; do
    f=$W/forward_$kind.hlsl
    {
      header
      for k in $variant; do echo "#define $k 1"; done
      [ $kind = HAIR ] && echo "#define _ALPHATEST_ON 1"
      echo "#define G3_REMASTER_$kind 1"
      echo "#include \"$SH/G3RemasterCharacter.hlsl\""
      echo "#define G3_REMASTER_FORWARD 1"
      echo "#include \"$SH/G3RemasterCharacter.hlsl\""
    } > "$f"
    for stage in "vs_6_0 RemasterVert SHADER_STAGE_VERTEX" "ps_6_0 RemasterFrag SHADER_STAGE_FRAGMENT"; do
      set -- $stage
      out=$(compile $1 $2 $3 "$f")
      [ -n "$out" ] && { echo "FAIL $kind [$variant] $1"; echo "$out"; fail=1; }
    done
  done
done
for pass in "ShadowCasterPass ShadowPassVertex ShadowPassFragment" "DepthOnlyPass DepthOnlyVertex DepthOnlyFragment" \
            "DepthNormalsPass DepthNormalsVertex DepthNormalsFragment"; do
  set -- $pass
  f=$W/$1.hlsl
  { header; echo "#define _ALPHATEST_ON 1"; echo "#define G3_REMASTER_HAIR 1"
    echo "#include \"$SH/G3RemasterCharacter.hlsl\""
    echo "#include \"Packages/com.unity.render-pipelines.universal/Shaders/$1.hlsl\""; } > "$f"
  out=$(compile vs_6_0 $2 SHADER_STAGE_VERTEX "$f"; compile ps_6_0 $3 SHADER_STAGE_FRAGMENT "$f")
  [ -n "$out" ] && { echo "FAIL $1"; echo "$out"; fail=1; }
done
# fix-191: URP's ObjectMotionVectors pass on our cbuffers (v1 character + v2 skin/hair/eye), opaque and alpha-tested
sed 's/#include_with_pragmas/#include/' "$W/inc/Packages/com.unity.render-pipelines.universal/ShaderLibrary/ObjectMotionVectors.hlsl" > "$W/ObjectMotionVectors_dxc.hlsl"
for inc in "G3RemasterCharacter.hlsl G3_REMASTER_SKIN" "G3RemasterCharacter.hlsl G3_REMASTER_HAIR _ALPHATEST_ON"            "G3RemasterV2.hlsl G3_V2_SKIN" "G3RemasterV2.hlsl G3_V2_EYE" "G3RemasterV2.hlsl G3_V2_HAIR _ALPHATEST_ON"; do
  set -- $inc
  f=$W/mv_$2.hlsl
  { header; echo "#define $2 1"; [ -n "$3" ] && echo "#define $3 1"
    echo "#include \"$SH/$1\""
    echo "#include \"$W/ObjectMotionVectors_dxc.hlsl\""; } > "$f"
  out=$(compile vs_6_0 vert SHADER_STAGE_VERTEX "$f"; compile ps_6_0 frag SHADER_STAGE_FRAGMENT "$f")
  [ -n "$out" ] && { echo "FAIL MotionVectors $2 $3"; echo "$out"; fail=1; }
done
[ $fail = 0 ] && echo "shader check: all variants compile"
exit $fail
