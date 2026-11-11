@echo off
rem ============================================================
rem  TaoKeeper package hash generator (SHA-256) - Windows version
rem  Package: PC-FULL-V1 / MSI 1.0.0.0 (2026-11-12)
rem  Pure ASCII + CRLF (byte-level verified, see LineK handover).
rem  Place this script inside the out folder and run it (double
rem  click or cmd). Output: hashes.txt next to this script.
rem  Format: "<lowercase sha256>  <relative path>" with forward
rem  slashes. Spec: hashes-spec.md. Uses Windows built-in
rem  PowerShell (present on every .NET 8 machine). Any failure
rem  aborts and leaves no partial hashes.txt behind.
rem ============================================================
setlocal
cd /d "%~dp0"
set "OUT=hashes.txt"
if exist "%OUT%" del /q "%OUT%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $root=(Get-Location).Path; $files=@(Get-ChildItem -Recurse -File | Where-Object { $_.Name -ne 'hashes.txt' } | Sort-Object FullName); $lines=@(); foreach ($f in $files) { $rel=$f.FullName.Substring($root.Length+1).Replace('\','/'); if ($rel -match '[^\x00-\x7F]') { throw ('non-ASCII file name: ' + $rel) }; $h=(Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash.ToLower(); $lines += ($h + '  ' + $rel) }; Set-Content -Path 'hashes.txt' -Value $lines -Encoding ASCII"
if errorlevel 1 (
  echo [ERROR] hash generation failed or non-ASCII file name found - no hashes.txt written
  if exist "%OUT%" del /q "%OUT%"
  endlocal
  exit /b 1
)
set /a CHECK=0
for /f %%N in ('type "%OUT%" ^| find /c /v ""') do set /a CHECK=%%N
echo [OK] hashes.txt written: %CD%\%OUT%
echo [OK] files hashed: %CHECK%
endlocal
exit /b 0
