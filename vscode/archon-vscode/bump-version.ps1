<#
    Increments the extension's build number (the patch segment of the semver
    version in package.json), e.g. 0.2.4 -> 0.2.5, and sets every other place
    the number is written to match: package-lock.json, both READMEs' example
    .vsix filename, and <Version> in the repository's Directory.Build.props, so
    the command line, the host and the extension report one version.

    package.json is the number that is incremented; the others are overwritten
    with the result whatever they held, which is how drift between them is
    repaired as well as prevented.

    Run from anywhere; the script locates its own directory.

    Usage:
      .\bump-version.ps1              # bump patch (build number)
      .\bump-version.ps1 -Part minor  # bump minor, reset patch to 0
      .\bump-version.ps1 -Part major  # bump major, reset minor and patch to 0
#>

param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Part = 'patch'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$packagePath = Join-Path $PSScriptRoot 'package.json'
$content = [System.IO.File]::ReadAllText($packagePath)

$pattern = '"version":\s*"(\d+)\.(\d+)\.(\d+)"'
$match = [regex]::Match($content, $pattern)
if (-not $match.Success) {
    throw "Could not find a `"version`" field in $packagePath"
}

$major = [int]$match.Groups[1].Value
$minor = [int]$match.Groups[2].Value
$patch = [int]$match.Groups[3].Value

switch ($Part) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}

$oldVersion = "$($match.Groups[1].Value).$($match.Groups[2].Value).$($match.Groups[3].Value)"
$newVersion = "$major.$minor.$patch"

$updated = $content.Substring(0, $match.Index) + "`"version`": `"$newVersion`"" + $content.Substring($match.Index + $match.Length)
[System.IO.File]::WriteAllText($packagePath, $updated, (New-Object System.Text.UTF8Encoding($false)))

# package-lock.json carries the extension's own version twice (top-level and under
# packages[""]), each immediately preceded by "name": "archon-analysis". Anchor on that
# so dependency entries elsewhere in the file (which also have "version" fields) are untouched.
$lockPath = Join-Path $PSScriptRoot 'package-lock.json'
if (Test-Path $lockPath) {
    $lockContent = [System.IO.File]::ReadAllText($lockPath)
    $lockPattern = '("name":\s*"archon-analysis",\s*"version":\s*")\d+\.\d+\.\d+(")'
    $lockUpdated = [regex]::Replace($lockContent, $lockPattern, "`${1}$newVersion`$2")
    [System.IO.File]::WriteAllText($lockPath, $lockUpdated, (New-Object System.Text.UTF8Encoding($false)))
}

# Both READMEs reference an example .vsix filename that embeds the version. Whatever version
# they name is replaced, not only the one package.json held, so a README left behind by an
# earlier bump is caught up rather than skipped.
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$vsixPattern = 'archon-analysis-\d+\.\d+\.\d+\.vsix'
foreach ($readmePath in @((Join-Path $PSScriptRoot 'README.md'), (Join-Path $repoRoot 'README.md'))) {
    if (Test-Path $readmePath) {
        $readmeContent = [System.IO.File]::ReadAllText($readmePath, [System.Text.Encoding]::UTF8)
        $readmeUpdated = [regex]::Replace($readmeContent, $vsixPattern, "archon-analysis-$newVersion.vsix")
        [System.IO.File]::WriteAllText($readmePath, $readmeUpdated, $utf8NoBom)
    }
}

# Directory.Build.props is the one version every .NET project builds with. It is set to the new
# number outright: package.json is the counter, this file follows it.
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (Test-Path $propsPath) {
    $propsContent = [System.IO.File]::ReadAllText($propsPath, [System.Text.Encoding]::UTF8)
    $propsPattern = '<Version>\d+\.\d+\.\d+</Version>'
    if (-not [regex]::IsMatch($propsContent, $propsPattern)) {
        throw "Could not find a <Version> element in $propsPath"
    }
    $propsUpdated = [regex]::Replace($propsContent, $propsPattern, "<Version>$newVersion</Version>")
    [System.IO.File]::WriteAllText($propsPath, $propsUpdated, $utf8NoBom)
}

Write-Host "Bumped version: $oldVersion -> $newVersion (package.json, package-lock.json, READMEs, Directory.Build.props)" -ForegroundColor Green
