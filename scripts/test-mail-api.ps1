param([string]$Server = '193.182.145.64', [string]$BaseUrl = 'https://nshub.pro', [switch]$Delivery, [switch]$CheckFigma)
$ErrorActionPreference = 'Stop'
$code = @'
import pathlib,json
rows=[json.loads(p.read_text()) for p in pathlib.Path('/var/lib/kiberone-hub/rosters').glob('*.json')]
print(json.dumps([{'location':r.get('location'),'ids':[s['id'] for s in r.get('students',[])]} for r in rows]))
'@
$rosters = ($code | ssh -o BatchMode=yes "root@$Server" python3 -) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read roster metadata through SSH' }
$passwords = Get-Content (Join-Path $env:LOCALAPPDATA 'KiberoneHubMigration/location-passwords.json') -Raw | ConvertFrom-Json
$match = $rosters | Where-Object { $_.ids.Count -gt 0 } | Select-Object -First 1
if (-not $match) { throw 'No existing roster with students' }
$secret = $passwords | Where-Object Location -eq $match.location | Select-Object -First 1
if (-not $secret) { throw 'No location credential available' }
$body = @{location=$match.location; password=$secret.Password; studentId=$match.ids[0]} | ConvertTo-Json -Compress
$account = Invoke-RestMethod "$BaseUrl/api/mail/accounts" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
$again = Invoke-RestMethod "$BaseUrl/api/mail/accounts" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
if ($account.password -ne $again.password -or $account.address -ne $again.address) { throw 'Mailbox credentials changed' }
$wrong = Invoke-WebRequest "$BaseUrl/api/mail/inbox" -Headers @{'X-Mailbox-Id'=$account.studentId; 'X-Mailbox-Password'='wrong-test-password'} -SkipHttpErrorCheck
if ($wrong.StatusCode -ne 401) { throw 'Wrong mailbox password was accepted' }
$unknownBody = @{location=$match.location; password=$secret.Password; studentId=[Guid]::NewGuid()} | ConvertTo-Json -Compress
$unknown = Invoke-WebRequest "$BaseUrl/api/mail/accounts" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($unknownBody)) -SkipHttpErrorCheck
if ($unknown.StatusCode -ne 403) { throw 'Unregistered student was accepted' }
if ($Delivery) {
    $mail = "From: test@nshub.pro`nTo: $($account.address)`nSubject: KIBERone API delivery test`nContent-Type: text/plain; charset=utf-8`n`nKIBERone test code: 749281`n"
    $mail | ssh -o BatchMode=yes "root@$Server" /usr/sbin/sendmail -t
    if ($LASTEXITCODE -ne 0) { throw 'SMTP submission failed' }
}
$headers = @{'X-Mailbox-Id'=$account.studentId; 'X-Mailbox-Password'=$account.password}
$letter = $null
for ($attempt = 0; $attempt -lt 10; $attempt++) {
    $inbox = Invoke-RestMethod "$BaseUrl/api/mail/inbox" -Headers $headers
    $letter = $inbox | Where-Object subject -eq 'KIBERone API delivery test' | Select-Object -First 1
    if (-not $Delivery -or $letter) { break }
    Start-Sleep -Seconds 2
}
if ($Delivery -and (-not $letter -or $letter.confirmationCode -ne '749281')) { throw 'Delivered test message was not readable' }
if ($CheckFigma) {
    $figma = $inbox | Where-Object { $_.sender -match 'figma' } | Select-Object -First 1
    if (-not $figma -or $figma.body.Trim().Length -lt 50 -or $figma.links.Count -lt 1) { throw 'Figma text or verification link is missing' }
    [pscustomobject]@{FigmaTextCharacters=$figma.body.Length; Links=$figma.links.Count; Empty=$false} | ConvertTo-Json -Compress
}
[pscustomobject]@{
    Https=$true; Address=$account.address; StablePassword=$true
    InboxReadable=$true; DeliveredAndRead=($Delivery -and $null -ne $letter)
    WrongPasswordStatus=$wrong.StatusCode; UnregisteredStudentStatus=$unknown.StatusCode
} | ConvertTo-Json -Compress
