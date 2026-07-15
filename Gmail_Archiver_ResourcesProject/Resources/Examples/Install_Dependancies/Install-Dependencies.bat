@echo off
REM ============================================================
REM  IVolt Gmail IMAP Archiver - Dependency Installer (launcher)
REM  Double-click this file to install/restore NuGet packages.
REM  No build is performed.
REM ============================================================

echo Installing dependencies for the IVolt Gmail IMAP Archiver...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Dependencies.ps1" %*

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Dependency installation reported an error. See the messages above.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Done. Packages restored. Open the project in your IDE and build when ready.
pause
