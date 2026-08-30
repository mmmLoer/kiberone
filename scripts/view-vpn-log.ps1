param(
    [int] $Tail = 40
)

$paths = @(
    "$env:ProgramData\KIBERone\Student\vpn\vpn.log",
    "$env:LOCALAPPDATA\KIBERone\Student\vpn\vpn.log"
)

foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    Write-Host "=== $path ===" -ForegroundColor Cyan
    Get-Content -LiteralPath $path -Tail $Tail
    Write-Host ""
}
