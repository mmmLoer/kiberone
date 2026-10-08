[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string] $Kind = 'patch',
    [ValidateSet('preserve', 'release', 'beta')]
    [string] $Channel = 'beta',
    [string] $VersionFile = (Join-Path (Split-Path -Parent $PSScriptRoot) 'version.json')
)

$ErrorActionPreference = 'Stop'
$path = (Resolve-Path -LiteralPath $VersionFile).Path
$document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
$current = $document.version
if ($current -isnot [string] -or $current -cnotmatch '^(0|[1-9][0-9]*)\.[0-9]\.(0|[1-9][0-9]*)(b)?$') {
    throw 'Expected major.minor.patch with minor 0-9 and optional beta suffix b.'
}
$numeric = [version]($current.TrimEnd('b'))
$major = $numeric.Major
$minor = $numeric.Minor
$patch = $numeric.Build
switch ($Kind) {
    'patch' { $patch++ }
    'minor' {
        $minor++
        $patch = 0
        if ($minor -eq 10) { $major++; $minor = 0 }
    }
    'major' { $major++; $minor = 0; $patch = 0 }
}
$beta = $Channel -eq 'beta' -or ($Channel -eq 'preserve' -and $current.EndsWith('b'))
$next = "$major.$minor.$patch" + $(if ($beta) { 'b' } else { '' })
# Validate the numeric assembly version before changing the file.
[version]($next.TrimEnd('b')) | Out-Null
if ($PSCmdlet.ShouldProcess($path, "Bump $current to $next")) {
    $document.version = $next
    $temporary = Join-Path (Split-Path -Parent $path) ('.version-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $backup = $temporary + '.bak'
    try {
        $json = ($document | ConvertTo-Json -Depth 32) + [Environment]::NewLine
        [System.IO.File]::WriteAllText($temporary, $json, (New-Object System.Text.UTF8Encoding $false))
        # Windows PowerShell coerces a null string argument to an empty path.
        [System.IO.File]::Replace($temporary, $path, $backup)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
    }
    Write-Output $next
}
