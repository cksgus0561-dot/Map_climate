@echo off
setlocal
cd /d "%~dp0"
rem The current app (map atlas + climate) is the Release build. artifacts\app is an old 2026-10-18 copy without the climate panel.
if exist "src\TheGarden.Desktop\bin\Release\net10.0-windows\Map.exe" (
    start "" "src\TheGarden.Desktop\bin\Release\net10.0-windows\Map.exe"
    exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Run
