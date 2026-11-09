<#
  Myrtana: one-command setup and build from a fresh clone.

    powershell -ExecutionPolicy Bypass -File Tools\Setup\setup.ps1 [-GameDir "D:\Games\Gothic 3"] [-Unity "...\Unity.exe"]
        [-PackDir <folder with downloaded pack parts>] [-NoPack] [-NoBuild] [-From <phase>] [-Out <build folder>]

  Phases (in order): check, pack, font, video, import, build.  -From resumes at a phase.
  Needs: Gothic 3 (Community Patch 1.75), Unity 6000.6.5f1 (Unity Hub), .NET 8 SDK (+ x86 .NET 8 runtime),
  Python 3, FFmpeg, and Visual Studio Build Tools (C++) or MinGW-w64 gcc for the launcher.
#>
param(
    [string]$GameDir,
    [string]$Unity,
    [string]$PackDir,
    [switch]$NoPack,
    [switch]$NoBuild,
    [ValidateSet('check', 'pack', 'font', 'video', 'import', 'build')]
    [string]$From = 'check',
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Phases = 'check', 'pack', 'font', 'video', 'import', 'build'
$Start = [array]::IndexOf($Phases, $From)
$LogDir = Join-Path $Root 'Logs'
New-Item -ItemType Directory -Force $LogDir | Out-Null

function Say($msg) { Write-Host "[setup] $msg" -ForegroundColor Cyan }
function Fail($msg) { Write-Host "[setup] ERROR: $msg" -ForegroundColor Red; exit 1 }
function Want($phase) { [array]::IndexOf($Phases, $phase) -ge $Start }

# ---------------------------------------------------------------------------------------------------------------- game
function Test-G3([string]$d) {
    if (-not $d) { return $false }
    (Test-Path (Join-Path $d 'Data\Templates.pak')) -and ((Test-Path (Join-Path $d 'Gothic3.exe')) -or (Test-Path (Join-Path $d 'Gothic3Final.exe')))
}
function Find-G3 {
    if ($GameDir) { if (Test-G3 $GameDir) { return (Resolve-Path $GameDir).Path } else { Fail "no Gothic 3 in $GameDir" } }
    if ($env:G3_DIR -and (Test-G3 $env:G3_DIR)) { return $env:G3_DIR }
    $c = @()
    foreach ($steam in @("${env:ProgramFiles(x86)}\Steam", "$env:ProgramFiles\Steam")) {
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $c += (Join-Path ($m.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Gothic 3')
            }
        }
    }
    foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games\1207658713', 'HKLM:\SOFTWARE\GOG.com\Games\1207658713') {
        $p = (Get-ItemProperty $k -ErrorAction SilentlyContinue).path
        if ($p) { $c += $p }
    }
    foreach ($drv in (Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Free -ne $null })) {
        $r = $drv.Root
        $c += "${r}SteamLibrary\steamapps\common\Gothic 3", "${r}Steam\steamapps\common\Gothic 3", "${r}GOG Games\Gothic 3",
              "${r}Games\Gothic 3", "${r}Program Files (x86)\Steam\steamapps\common\Gothic 3", "${r}Program Files (x86)\GOG Galaxy\Games\Gothic 3"
    }
    foreach ($d in $c) { if (Test-G3 $d) { return $d } }
    Fail 'Gothic 3 not found: pass -GameDir "<folder with Gothic3.exe>"'
}

# --------------------------------------------------------------------------------------------------------------- unity
function Find-Unity {
    if ($Unity) { if (Test-Path $Unity) { return $Unity } else { Fail "no $Unity" } }
    $ver = ((Get-Content (Join-Path $Root 'ProjectSettings\ProjectVersion.txt')) -match '^m_EditorVersion:' -replace 'm_EditorVersion:\s*', '')
    $roots = @("$env:ProgramFiles\Unity\Hub\Editor")
    $sec = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
    if (Test-Path $sec) { $roots += ((Get-Content $sec -Raw) | ConvertFrom-Json) }
    foreach ($r in $roots) {
        $exe = Join-Path $r "$ver\Editor\Unity.exe"
        if (Test-Path $exe) { return $exe }
    }
    Fail "Unity $ver not found: install it with Unity Hub, or pass -Unity <path to Unity.exe>"
}

function Need($cmd, $hint) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { Fail "$cmd not found. Install: $hint" }
}

function Find-Python {
    foreach ($p in 'py', 'python', 'python3') {
        $c = Get-Command $p -ErrorAction SilentlyContinue
        if ($c) { return $c.Source }
    }
    Fail 'Python 3 not found. Install: winget install Python.Python.3.12'
}

function Run-Unity([string]$exe, [string]$method, [string[]]$extra, [string]$log) {
    # no -nographics: some builders render (tree impostors, logo video)
    $ua = (@('-batchmode', '-projectPath', $Root, '-executeMethod', $method, '-logFile', $log) + $extra |
        ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    Say "Unity: $method (log $log)"
    $p = Start-Process -FilePath $exe -ArgumentList $ua -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { Fail "$method failed (exit $($p.ExitCode)); see $log" }
}

# ================================================================================================================ run
$G3 = Find-G3
$env:G3_DIR = $G3
$env:G3_DATA = Join-Path $G3 'Data'
Say "Gothic 3: $G3"

if (Want 'check') {
    $UnityExe = Find-Unity
    Need 'dotnet' 'winget install Microsoft.DotNet.SDK.8  (and the x86 runtime: winget install Microsoft.DotNet.Runtime.8 --architecture x86)'
    Need 'ffmpeg' 'winget install Gyan.FFmpeg'
    $null = Find-Python
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere) -and -not (Get-Command gcc -ErrorAction SilentlyContinue)) {
        Fail 'no C compiler for the launcher. Install: winget install Microsoft.VisualStudio.2022.BuildTools (workload "Desktop development with C++")'
    }
    if (-not (Test-Path (Join-Path $G3 'Data\Speech_English.pak')) -and -not (Test-Path (Join-Path $G3 'Data\Speech_Russian.pak'))) {
        Write-Warning 'no Speech_*.pak in the install: dialogs will be silent'
    }
    Say "Unity: $UnityExe"
}
else { $UnityExe = Find-Unity }

# ---------------------------------------------------------------------------------------------------------- pack
if ((Want 'pack') -and -not $NoPack) {
    $manifest = Get-Content (Join-Path $PSScriptRoot 'pack.json') -Raw | ConvertFrom-Json
    if (-not $PackDir) { $PackDir = Join-Path $Root 'Work\pack' }
    New-Item -ItemType Directory -Force $PackDir | Out-Null
    foreach ($part in $manifest.parts) {
        $file = Join-Path $PackDir $part.name
        $ok = (Test-Path $file) -and ((Get-FileHash $file -Algorithm SHA256).Hash -eq $part.sha256.ToUpper())
        if (-not $ok) {
            if (-not $part.url) { Fail "missing $($part.name): download it into $PackDir (see README)" }
            Say "download $($part.name) ($([math]::Round($part.size / 1GB, 2)) GB)"
            & (Join-Path $env:SystemRoot 'System32\curl.exe') -L --fail --retry 5 -C - -o $file $part.url
            if ($LASTEXITCODE -ne 0) { Fail "download failed: $($part.url)" }
            if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $part.sha256.ToUpper()) { Fail "checksum mismatch: $file (delete it and run again)" }
        }
        Say "unpack $($part.name)"
        # Windows' own bsdtar (reads zip); a Git-for-Windows tar earlier on PATH cannot
        & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $file -C $Root
        if ($LASTEXITCODE -ne 0) { Fail "unpack failed: $file" }
    }
    # the prebuilt remaster logo must count as newer than the fresh clone's scripts (RemasterLogoBuild.UpToDate)
    $logo = Join-Path $Root 'Assets\StreamingAssets\Video\G3R_Remaster.webm'
    if (Test-Path $logo) { (Get-Item $logo).LastWriteTime = Get-Date }
}

# ---------------------------------------------------------------------------------------------------------- font
if (Want 'font') {
    $py = Find-Python
    & $py -m pip install --quiet --user fonttools numpy
    & $py -I (Join-Path $Root 'Tools\Fonts\g3cyrillic.py') (Join-Path $G3 'Gothic3.ttf') (Join-Path $Root 'Assets\G3\Fonts\Gothic3_Cyr.ttf')
    if ($LASTEXITCODE -ne 0) { Fail 'font build failed' }
}

# ---------------------------------------------------------------------------------------------------------- video
if (Want 'video') {
    $vdir = Join-Path $Root 'Assets\StreamingAssets\Video'
    New-Item -ItemType Directory -Force $vdir | Out-Null
    foreach ($v in 'G3_Intro', 'G3_Credits', 'G3_Credits2', 'G3_Logo_01', 'Publisher') {
        $src = Join-Path $G3 "Data\Video\$v.bik"
        $dst = Join-Path $vdir "$v.webm"
        if (-not (Test-Path $src)) { Write-Warning "no $src"; continue }
        if (Test-Path $dst) { continue }
        Say "video $v"
        & ffmpeg -loglevel error -y -i $src -c:v libvpx -b:v 6M -crf 8 -c:a libvorbis -q:a 6 $dst
        if ($LASTEXITCODE -ne 0) { Fail "ffmpeg failed on $src" }
    }
}

# ---------------------------------------------------------------------------------------------------------- import
if (Want 'import') {
    Say 'building tools (SptBake, G3Cli)'
    & dotnet build -c Release (Join-Path $Root 'Tools\SptBake\SptBake.csproj') -nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'SptBake build failed (needs the .NET 8 SDK and the x86 runtime)' }
    Say 'import: several hours on the first run (the whole world is converted); resume with -From import'
    Run-Unity $UnityExe 'Myrtana.Unity.Editor.FreshSetup.RunAll' @() (Join-Path $LogDir 'setup-import.log')
}

# ---------------------------------------------------------------------------------------------------------- build
if ((Want 'build') -and -not $NoBuild) {
    $extra = @()
    if ($Out) { $extra += '-demoOut', $Out }
    Run-Unity $UnityExe 'Myrtana.Unity.Editor.DemoBuild.BuildFromCommandLine' $extra (Join-Path $LogDir 'setup-build.log')
    Say 'done: the game is in Builds\ (start G3World.exe)'
}
