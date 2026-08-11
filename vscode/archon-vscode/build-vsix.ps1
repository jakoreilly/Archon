<#
    Builds the Archon extension into a .vsix, end to end:
    npm install -> publish-host -> compile -> vsce package.

    Run from anywhere; the script locates its own directory. Requires Node.js
    and the .NET SDK on PATH.
#>

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Step {
    param(
        [string]$Description,
        [string]$Exe,
        [string[]]$Arguments
    )
    Write-Host "==> $Description" -ForegroundColor Cyan
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (exit $LASTEXITCODE)"
    }
}

Invoke-Step "Installing npm dependencies" "npm" @("install")
Invoke-Step "Publishing the analysis host" "npm" @("run", "publish-host")
Invoke-Step "Compiling the extension" "npm" @("run", "compile")
Invoke-Step "Packaging the .vsix" "npx" @("@vscode/vsce", "package")

$vsix = Get-ChildItem -Filter "*.vsix" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $vsix) {
    throw "vsce reported success but no .vsix file was found."
}

Write-Host ""
Write-Host "Built $($vsix.Name) ($([math]::Round($vsix.Length / 1MB, 2)) MB)" -ForegroundColor Green
Write-Host "Install with: code --install-extension `"$($vsix.FullName)`""
