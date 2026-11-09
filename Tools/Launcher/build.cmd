@echo off
rem G3World launcher build (fix-152): build.cmd [out.exe] [source.c]
rem   default out: Tools\Launcher\bin\G3World.exe, default source: G3Launcher.c (DemoBuild passes <build>\G3World.exe).
rem MSVC (cl, static CRT) found through vswhere; else MinGW-w64 gcc on PATH. Exit code 0 = built.
setlocal EnableDelayedExpansion
set "HERE=%~dp0"
set "OUT=%~f1"
if "%~1"=="" set "OUT=%HERE%bin\G3World.exe"
set "SRC=%~f2"
if "%~2"=="" set "SRC=%HERE%G3Launcher.c"
for %%d in ("%OUT%") do if not exist "%%~dpd" mkdir "%%~dpd"
set "OBJ=%TEMP%\g3launcher_obj_%RANDOM%"
mkdir "%OBJ%" 2>nul

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSDIR="
if exist "%VSWHERE%" (
  for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
)
if defined VSDIR if exist "!VSDIR!\VC\Auxiliary\Build\vcvars64.bat" (
  call "!VSDIR!\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul
  cl /nologo /O1 /MT /W4 /WX /GS /Fo"%OBJ%\\" /Fe"%OUT%" "%SRC%" /link /SUBSYSTEM:WINDOWS /DYNAMICBASE /NXCOMPAT user32.lib gdi32.lib shell32.lib ole32.lib >"%OBJ%\cl.log"
  set "RC=!errorlevel!"
  if not "!RC!"=="0" type "%OBJ%\cl.log"
  rmdir /s /q "%OBJ%" 2>nul
  if "!RC!"=="0" echo launcher built with MSVC: %OUT%
  exit /b !RC!
)
where gcc >nul 2>nul
if not errorlevel 1 (
  gcc -O2 -Wall -Werror -municode -mwindows -static -s -o "%OUT%" "%SRC%" -luser32 -lgdi32 -lshell32 -lole32 -luuid
  set "RC=!errorlevel!"
  rmdir /s /q "%OBJ%" 2>nul
  if "!RC!"=="0" echo launcher built with gcc: %OUT%
  exit /b !RC!
)
rmdir /s /q "%OBJ%" 2>nul
echo no C compiler: install Visual Studio Build Tools ("Desktop development with C++") or MinGW-w64 gcc
exit /b 2
