@echo off
rem Myrtana: setup and build from a fresh clone. Arguments go to setup.ps1 (e.g. -GameDir "D:\Games\Gothic 3").
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
exit /b %errorlevel%
