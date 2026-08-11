@echo off
:: Shows the current extension version and asks which part to bump, then
:: runs bump-version.ps1 with the chosen part.

setlocal
cd /d "%~dp0"

for /f "usebackq tokens=2 delims=:," %%v in (`findstr /r /c:"\"version\":" package.json`) do set RAW_VERSION=%%v
set CURRENT_VERSION=%RAW_VERSION:"=%
set CURRENT_VERSION=%CURRENT_VERSION: =%

if "%CURRENT_VERSION%"=="" (
    echo Could not read the current version from package.json.
    goto :fail
)

echo Current version: %CURRENT_VERSION%
echo.
set /p PART=Bump which part? [major/minor/patch] (default: patch):
if "%PART%"=="" set PART=patch

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bump-version.ps1" -Part %PART%
if errorlevel 1 goto :fail

exit /b 0

:fail
echo.
echo Version bump FAILED.
exit /b 1
