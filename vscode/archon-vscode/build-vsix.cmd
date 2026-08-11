@echo off
:: Builds the Archon extension into a .vsix, end to end:
:: npm install -> publish-host -> compile -> vsce package.
::
:: Run from anywhere; the script locates its own directory. Requires Node.js
:: and the .NET SDK on PATH.

setlocal
cd /d "%~dp0"

echo ==^> Installing npm dependencies
call npm install
if errorlevel 1 goto :fail

echo ==^> Publishing the analysis host
call npm run publish-host
if errorlevel 1 goto :fail

echo ==^> Compiling the extension
call npm run compile
if errorlevel 1 goto :fail

echo ==^> Packaging the .vsix
call npx @vscode/vsce package
if errorlevel 1 goto :fail

echo.
echo Build complete. Look for archon-analysis-*.vsix in this folder.
exit /b 0

:fail
echo.
echo Build FAILED.
exit /b 1
