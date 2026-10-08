param([switch]$AllowRealVpn)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (!$AllowRealVpn) { throw 'Explicit -AllowRealVpn is required.' }
$machine = Get-CimInstance Win32_ComputerSystem
if (($machine.Manufacturer + ' ' + $machine.Model) -notmatch 'VirtualBox|innotek|VMware|Virtual Machine') {
    throw 'This test is restricted to virtual machines.'
}

function Invoke-Bridge([string]$Action) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'KIBERone.Student.Vpn', [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(2000)
        $body = [Text.Encoding]::UTF8.GetBytes((@{action=$Action;config_path='C:\ProgramData\KIBERone\Student\vpn\peer.conf'} | ConvertTo-Json -Compress))
        $length = [BitConverter]::GetBytes([int]$body.Length)
        $pipe.Write($length, 0, 4)
        $pipe.Write($body, 0, $body.Length)
        $pipe.Flush()
        # Bound the whole response, including its length prefix, to prevent hangs.
        $prefix = New-Object byte[] 4
        Read-Exact $pipe $prefix
        $size = [BitConverter]::ToInt32($prefix, 0)
        if ($size -le 0 -or $size -gt 1048576) { throw 'Invalid bridge response size.' }
        $reply = New-Object byte[] $size
        Read-Exact $pipe $reply
        $response = [Text.Encoding]::UTF8.GetString($reply) | ConvertFrom-Json
        if (!$response.ok) { throw "Bridge $Action failed: $($response.error)" }
        return $response
    } finally { $pipe.Dispose() }
}
function Read-Exact($Pipe, [byte[]]$Bytes) {
    $offset = 0
    while ($offset -lt $Bytes.Length) {
        $read = $Pipe.ReadAsync($Bytes, $offset, $Bytes.Length - $offset)
        if (!$read.Wait(45000)) { throw 'Bridge response timed out.' }
        if ($read.Result -le 0) { throw 'Bridge closed before its response was complete.' }
        $offset += $read.Result
    }
}
function Public-IP {
    return (Invoke-RestMethod ('https://api.ipify.org?format=json&check=' + [guid]::NewGuid()) -DisableKeepAlive -TimeoutSec 10).ip
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
Write-Output "Elevated=$($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"
$initial = Invoke-Bridge 'Status'
Write-Output "InitialConnected=$($initial.connected)"
if (!$initial.config_exists) { throw 'Managed VPN configuration is absent.' }
$originalIp = Public-IP
Write-Output "InitialPublicIP=$originalIp"
$attempted = $false
try {
    for ($cycle = 1; $cycle -le 3; $cycle++) {
        $attempted = $true
        $off = Invoke-Bridge 'Disconnect'
        if ($off.connected) { throw 'Disconnect left the tunnel connected.' }
        $on = Invoke-Bridge 'Connect'
        if (!$on.connected) { throw 'Connect did not establish a tunnel.' }
        # A running tunnel service can precede route installation and handshake.
        # Fresh HTTP connections avoid reusing a socket established before VPN.
        $ip = $originalIp
        for ($probe = 0; $probe -lt 5; $probe++) {
            Start-Sleep -Seconds 2
            $ip = Public-IP
            if ($initial.connected -or $ip -ne $originalIp) { break }
        }
        if (!$initial.connected -and $ip -eq $originalIp) { throw 'Public IPv4 did not change.' }
        Write-Output "PASS cycle=$cycle connected=$($on.connected) publicIP=$ip"
    }
    Write-Output 'VPN_BRIDGE_PASS'
} finally {
    if ($attempted) {
        $restore = Invoke-Bridge $(if ($initial.connected) { 'Connect' } else { 'Disconnect' })
        if ([bool]$restore.connected -ne [bool]$initial.connected) { throw 'Original VPN state was not restored.' }
        Write-Output "RestoredConnected=$($restore.connected)"
    }
}
