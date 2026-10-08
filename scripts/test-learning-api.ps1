param([string]$Server = '193.182.145.64', [string]$BaseUrl = 'https://nshub.pro')
$ErrorActionPreference = 'Stop'
$credentials = Get-Content (Join-Path $env:LOCALAPPDATA 'KiberoneHubMigration/location-passwords.json') -Raw | ConvertFrom-Json
$credential = $credentials | Select-Object -First 1
if (-not $credential) { throw 'No location credential available' }
function Post-Learning($path, $data) {
    Invoke-RestMethod "$BaseUrl/api/learning/$path" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes(($data | ConvertTo-Json -Depth 12 -Compress)))
}
$quizId = [guid]::NewGuid()
$document = @{id=$quizId.ToString();format='kiberone-quiz';version=1;title='__Learning API integration test';timePerQuestionSeconds=30;xpReward=10;questions=@(@{text='Test question?';options=@('yes','no');correctIndex=0})}
$request = @{location=$credential.Location;password=$credential.Password;document=$document;expectedRevision=0}
try {
    $saved = Post-Learning 'quizzes/publish' $request
    $listed = Post-Learning 'quizzes/list' @{location=$credential.Location;password=$credential.Password}
    if (-not ($listed | Where-Object id -eq $quizId.ToString())) { throw 'Published quiz missing' }
    $conflict = Invoke-WebRequest "$BaseUrl/api/learning/quizzes/publish" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes(($request|ConvertTo-Json -Depth 12))) -SkipHttpErrorCheck
    $wrong = Invoke-WebRequest "$BaseUrl/api/learning/quizzes/list" -Method Post -ContentType 'application/json' -Body '{"location":"test","password":"wrong"}' -SkipHttpErrorCheck
    $foreign = @{location=$credential.Location;password=$credential.Password;studentId=[guid]::NewGuid().ToString();records=@()}
    $denied = Invoke-WebRequest "$BaseUrl/api/learning/records" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes(($foreign|ConvertTo-Json))) -SkipHttpErrorCheck
    if ($conflict.StatusCode -ne 409 -or $wrong.StatusCode -ne 401 -or $denied.StatusCode -ne 401) { throw 'Authorization or revision validation failed' }
    [pscustomobject]@{Https=$true;PublishRevision=$saved.revision;Listed=$true;ConflictStatus=$conflict.StatusCode;WrongPasswordStatus=$wrong.StatusCode;ForeignStudentStatus=$denied.StatusCode} | ConvertTo-Json -Compress
} finally {
    # Only the randomly generated test document is removed, never existing quizzes.
    $safeId = $quizId.ToString('N')
    if ($safeId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid test cleanup ID' }
    ssh -o BatchMode=yes "root@$Server" "rm -f -- /var/lib/kiberone-hub/learning/quiz-$safeId.json"
    if ($LASTEXITCODE -ne 0) { throw 'Could not remove test quiz' }
}
