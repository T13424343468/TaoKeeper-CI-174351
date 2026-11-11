@echo off
rem   *** PACKAGE VERSION: PC-FULL-V1 / MSI 1.0.0.0 (2026-11-12) ***
rem TaoKeeper MSI build (Windows only, WiX 4+). Prerequisites:
rem   1) dotnet SDK 8+   2) dotnet tool install -g wix   3) wix extension add -g WixToolset.UI.wixext
rem Full Chinese docs: docs folder, "MSI packaging notes" (Chinese filename).
rem Optional arg 1: path to the publish output folder (must contain TaoKeeper.App.exe)
setlocal
cd /d "%~dp0"
set PUBLISH_DIR=%~dp0publish
if not "%~1"=="" set "PUBLISH_DIR=%~1"

rem --- Step 1: locate publish payload (harvested from there, offline, no runtime downloads) ---
if not exist "%PUBLISH_DIR%\TaoKeeper.App.exe" (
  echo [ERROR] publish output not found: "%PUBLISH_DIR%\TaoKeeper.App.exe"
  echo         First run: dotnet publish src\TaoKeeper.App -c Release -r win-x64 --self-contained false -o "%PUBLISH_DIR%"
  echo         or pass the publish folder as first argument: build-msi.bat ^<publish folder^>
  exit /b 1
)

rem --- Step 2: compile and bind MSI (bind needs Windows msi.dll, so this script runs on Windows only) ---
if not exist out mkdir out
wix build -ext WixToolset.UI.wixext -culture zh-CN -arch x64 -pdbtype none ^
  -define "PublishDir=%PUBLISH_DIR%" ^
  -o out\TaoKeeper-PC-MSI-v1.0.0.msi Product.wxs
if errorlevel 1 (
  echo [ERROR] wix build failed. Check messages above.
  exit /b 1
)

rem --- Step 3: out must contain exactly 1 msi ---
set MSI_COUNT=0
for %%f in (out\*.msi) do set /a MSI_COUNT+=1
echo [CHECK] msi files in out: %MSI_COUNT%
if not "%MSI_COUNT%"=="1" (
  echo [ERROR] out folder must contain exactly 1 msi. Clean out\ and rebuild.
  exit /b 1
)

echo [OK] MSI built: out\TaoKeeper-PC-MSI-v1.0.0.msi
echo [OK] Default install: D:\Program Files\TaoKeeper (changeable in UI)
endlocal
exit /b 0
