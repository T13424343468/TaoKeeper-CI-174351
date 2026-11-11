@echo off
title TaoKeeper Build
cd /d "%~dp0"

echo ====================================================
echo   TaoKeeper build (tests + harness + C++ core)
echo   *** PACKAGE VERSION: PC-FULL-V1 (2026-11-09) ***
echo   Any red test stops the run - no package output.
echo ====================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set RC=%ERRORLEVEL%

echo.
if "%RC%"=="0" goto OK
echo [FAILED] Exit code %RC%.
echo Copy the WHOLE output above (especially red lines) and send it back.
goto END
:OK
echo [OK] Build all green. Report and hashes are in dist\^<timestamp^>\.
echo Next: run the harness per readme, then send the output back.
:END
echo.
echo Window stays open - press any key to close...
pause >nul
exit /b %RC%
