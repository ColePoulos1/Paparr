param(
    [int]$Port = 5188,
    [switch]$Browser,
    [switch]$Calibre,
    [switch]$KeepRunning
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if ($Browser) {
    & node --check (Join-Path $PSScriptRoot 'Test-Browser.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Packaged browser script failed its syntax check.' }
}
$project = 'paparr-smoke-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$testRoot = Join-Path $repoRoot ".tools/$project"
$ingest = Join-Path $testRoot 'ingest'
$demoIngest = Join-Path $ingest 'fiction'
$library = Join-Path $testRoot 'library'
$envPath = Join-Path $testRoot '.env'
$password = [Guid]::NewGuid().ToString('N')
$ownerPassword = [Guid]::NewGuid().ToString('N')
$ownerHash = ''
$authenticationEnabled = 'false'
$baseUrl = "http://127.0.0.1:$Port"
$profileOverride = Join-Path $testRoot 'profiles.yml'
$compose = @('compose', '--file', (Join-Path $repoRoot 'docker-compose.yml'), '--file', $profileOverride, '--env-file', $envPath, '--project-name', $project)
$checks = [Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Path $ingest, $library -Force | Out-Null
@'
services:
  paparr-api:
    environment:
      FolderProfiles__0__Name: Fiction
      FolderProfiles__0__IngestSubfolder: fiction
      FolderProfiles__0__Collection: Fiction
      FolderProfiles__0__FilenameOrder: TitleAuthor
'@ | Set-Content -LiteralPath $profileOverride -Encoding utf8NoBOM
if (!$IsWindows) {
    & chmod 777 $library
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the disposable library mount.' }
}

function Invoke-Compose([string[]]$Arguments) {
    & docker @compose @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Compose command failed: $($Arguments -join ' ')" }
}
function Set-TestEnvironment([string]$LibraryPath, [string]$Database = 'paparr') {
    @(
        "POSTGRES_PASSWORD=$password"
        "POSTGRES_DB=$Database"
        "AUTHENTICATION_ENABLED=$authenticationEnabled"
        "PAPARR_PASSWORD_HASH=$ownerHash"
        "HOST_INGEST_PATH=$($ingest.Replace('\', '/'))"
        "HOST_LIBRARY_PATH=$($LibraryPath.Replace('\', '/'))"
        "PAPARR_PORT=$Port"
        'BIND_ADDRESS=127.0.0.1'
        'DELETE_SOURCE_AFTER_IMPORT=false'
        'METADATA_PROVIDERS_ENABLED=false'
    ) | Set-Content -LiteralPath $envPath -Encoding utf8NoBOM
}
function Wait-For([scriptblock]$Condition, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Description."
}
function Assert-Equal($Expected, $Actual, [string]$Description) {
    if ($Expected -ne $Actual) { throw "$Description failed. Expected '$Expected'; got '$Actual'." }
}
function Assert-Scan([int]$Verified, [int]$Changed) {
    $scan = Invoke-RestMethod "$baseUrl/api/library/audits" -Method Post
    Wait-For { (Invoke-RestMethod "$baseUrl/api/library/audits/$($scan.id)").status -eq 'Completed' } 'integrity scan'
    $result = Invoke-RestMethod "$baseUrl/api/library/audits/$($scan.id)"
    Assert-Equal $Verified $result.verifiedBooks 'Verified count'
    Assert-Equal $Changed $result.changedBooks 'Changed count'
}
Set-TestEnvironment $library
try {
    & (Join-Path $PSScriptRoot 'New-DemoBooks.ps1') -Destination $demoIngest
    Invoke-Compose -Arguments @('up', '--build', '-d', '--wait', '--wait-timeout', '180')
    Wait-For {
        $counts = Invoke-RestMethod "$baseUrl/api/imports/summary"
        $counts.AwaitingApproval -eq 4 -and $counts.Failed -eq 1
    } 'demo discovery and extraction'
    $checks.Add('Clean installation and demo extraction')

    $jobs = Invoke-RestMethod "$baseUrl/api/imports?status=AwaitingApproval"
    $job = $jobs.items | Where-Object { $_.candidates.title -contains 'The Quiet Atlas' } | Select-Object -First 1
    Assert-Equal 'Fiction' $job.profile.name 'Captured folder profile'
    Assert-Equal 'fiction' $job.profile.sourceFolder 'Captured watch subfolder'
    if (!$job) { throw 'The named demo book was not discovered.' }
    $duplicateDirectory = Join-Path $ingest 'copies'
    New-Item -ItemType Directory -Path $duplicateDirectory | Out-Null
    $duplicate = Join-Path $duplicateDirectory 'The Quiet Atlas copy.epub'
    Copy-Item -LiteralPath (Join-Path $demoIngest $job.fileName) -Destination $duplicate
    Wait-For { (Invoke-RestMethod "$baseUrl/api/imports/$($job.id)/duplicates").total -eq 1 } 'duplicate observation'
    $observation = (Invoke-RestMethod "$baseUrl/api/imports/$($job.id)/duplicates").items[0]
    Assert-Equal 'copies/The Quiet Atlas copy.epub' $observation.relativePath 'Duplicate relative path'
    Assert-Equal 5 (Invoke-RestMethod "$baseUrl/api/imports").total 'Duplicate does not create another import'
    $checks.Add('Duplicate content is recorded once and retained without a second import')
    $candidate = $job.candidates | Where-Object title -eq 'The Quiet Atlas' | Select-Object -First 1
    $imported = Invoke-RestMethod "$baseUrl/api/imports/$($job.id)/accept/$($candidate.id)" -Method Post
    Assert-Equal 'Completed' $imported.status 'Approval'
    $files = @(Get-ChildItem -LiteralPath $library -File -Recurse)
    Assert-Equal 1 $files.Count 'Published file count'
    $source = Join-Path $demoIngest $job.fileName
    $expectedHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    Assert-Equal $expectedHash (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash 'Published hash'
    Assert-Equal $expectedHash (Get-FileHash -LiteralPath $duplicate -Algorithm SHA256).Hash 'Duplicate retained after approval'
    $checks.Add('Verified publication with original retained')
    Assert-Equal 'Fiction' $imported.profile.collection 'Captured library collection'
    $publishedRelative = [IO.Path]::GetRelativePath($library, $files[0].FullName).Replace('\', '/')
    if (!$publishedRelative.StartsWith('Fiction/', [StringComparison]::Ordinal)) { throw 'Profile collection was not used for publication.' }
    $checks.Add('Captured folder profile routes reviewed imports into its library collection')
    if (!$IsWindows) {
        $relative = [IO.Path]::GetRelativePath($library, $files[0].FullName).Replace('\', '/')
        Invoke-Compose -Arguments @('exec', '-T', 'paparr-api', 'chmod', 'o+w', "/library/$relative")
    }

    $summary = Invoke-RestMethod "$baseUrl/api/imports/summary" | ConvertTo-Json -Compress
    Invoke-Compose -Arguments @('up', '-d', '--force-recreate', '--wait', '--wait-timeout', '120')
    Assert-Equal $summary (Invoke-RestMethod "$baseUrl/api/imports/summary" | ConvertTo-Json -Compress) 'History after recreation'
    Assert-Equal $expectedHash (Get-FileHash -LiteralPath $files[0].FullName -Algorithm SHA256).Hash 'Hash after recreation'
    $checks.Add('Container recreation preserves records and files')

    Assert-Scan 1 0
    [IO.File]::WriteAllText($files[0].FullName, 'Intentionally changed test copy.')
    Assert-Scan 0 1
    Assert-Equal 'Intentionally changed test copy.' ([IO.File]::ReadAllText($files[0].FullName)) 'Audit leaves changed bytes untouched'
    Copy-Item -LiteralPath $source -Destination $files[0].FullName
    Assert-Scan 1 0
    $checks.Add('Integrity scan detects changes and never modifies files')

    Invoke-Compose -Arguments @('stop', 'paparr-ui', 'paparr-api')
    $backup = Join-Path $testRoot 'backup'
    New-Item -ItemType Directory -Path $backup | Out-Null
    Copy-Item -LiteralPath $library -Destination (Join-Path $backup 'library') -Recurse
    Invoke-Compose -Arguments @('exec', '-T', 'postgres', 'pg_dump', '--username=paparr', '--dbname=paparr', '--format=custom', '--file=/tmp/paparr-smoke.dump')
    Invoke-Compose -Arguments @('cp', 'postgres:/tmp/paparr-smoke.dump', (Join-Path $backup 'database.dump'))
    Invoke-Compose -Arguments @('exec', '-T', 'postgres', 'createdb', '--username=paparr', 'paparr_restore')
    Invoke-Compose -Arguments @('exec', '-T', 'postgres', 'pg_restore', '--username=paparr', '--dbname=paparr_restore', '--exit-on-error', '/tmp/paparr-smoke.dump')
    $restoredLibrary = Join-Path $testRoot 'restored-library'
    Copy-Item -LiteralPath (Join-Path $backup 'library') -Destination $restoredLibrary -Recurse
    Set-TestEnvironment $restoredLibrary 'paparr_restore'
    if (!$IsWindows) {
        # Copy-Item gives the runner ownership, including nested collection and author folders.
        Invoke-Compose -Arguments @('run', '--rm', '--no-deps', '--user', 'root', '--entrypoint', 'chown',
            'paparr-api', '-R', '1654:1654', '/library')
    }
    Invoke-Compose -Arguments @('up', '-d', '--force-recreate', '--wait', '--wait-timeout', '120')
    Assert-Equal $summary (Invoke-RestMethod "$baseUrl/api/imports/summary" | ConvertTo-Json -Compress) 'History after restore'
    $restoredFiles = @(Get-ChildItem -LiteralPath $restoredLibrary -File -Recurse)
    Assert-Equal 1 $restoredFiles.Count 'Restored file count'
    Assert-Equal $expectedHash (Get-FileHash -LiteralPath $restoredFiles[0].FullName -Algorithm SHA256).Hash 'Restored file hash'
    Assert-Scan 1 0
    $checks.Add('Database dump and separate library copy restore into fresh locations')

    $hashOutput = $ownerPassword | & docker @compose run --rm --no-deps -T paparr-api --hash-password
    if ($LASTEXITCODE -ne 0) { throw 'Password hash generation failed.' }
    $ownerHash = [string]($hashOutput | Where-Object { $_ -match '^AQ[A-Za-z0-9+/]+=*$' } | Select-Object -Last 1)
    if (!$ownerHash) { throw 'The password command did not produce a hash.' }
    $authenticationEnabled = 'true'
    Set-TestEnvironment $restoredLibrary 'paparr_restore'
    Invoke-Compose -Arguments @('up', '-d', '--force-recreate', '--wait', '--wait-timeout', '120')
    Assert-Equal 401 (Invoke-WebRequest "$baseUrl/api/imports" -SkipHttpErrorCheck).StatusCode 'Anonymous API access'
    $authSession = Invoke-RestMethod "$baseUrl/api/auth/session" -SessionVariable ownerSession
    $login = Invoke-RestMethod "$baseUrl/api/auth/login" -WebSession $ownerSession -Method Post -ContentType 'application/json' `
        -Headers @{ 'X-CSRF-TOKEN' = $authSession.csrfToken } -Body (@{ password = $ownerPassword } | ConvertTo-Json)
    Assert-Equal $true $login.authenticated 'Owner sign-in'
    Assert-Equal $summary (Invoke-RestMethod "$baseUrl/api/imports/summary" -WebSession $ownerSession | ConvertTo-Json -Compress) 'Authenticated history'
    Assert-Equal 401 (Invoke-WebRequest "$baseUrl/api/library/$($imported.acceptedBook.id)/file" -SkipHttpErrorCheck).StatusCode 'Anonymous download'
    $downloadPath = Join-Path $testRoot 'downloaded-atlas.epub'
    Invoke-WebRequest "$baseUrl/api/library/$($imported.acceptedBook.id)/file" -WebSession $ownerSession -OutFile $downloadPath
    Assert-Equal $expectedHash (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash 'Authenticated download hash'
    $checks.Add('Owner-only download verifies and returns the published file')
    $exportPath = Join-Path $testRoot 'atlas-calibre.zip'
    Assert-Equal 401 (Invoke-WebRequest "$baseUrl/api/library/$($imported.acceptedBook.id)/export" -SkipHttpErrorCheck).StatusCode 'Anonymous Calibre export'
    Invoke-WebRequest "$baseUrl/api/library/$($imported.acceptedBook.id)/export" -WebSession $ownerSession -OutFile $exportPath
    $exportArchive = [IO.Compression.ZipFile]::OpenRead($exportPath)
    try {
        Assert-Equal 3 $exportArchive.Entries.Count 'Calibre export entry count'
        $entryStream = $exportArchive.GetEntry('book.epub').Open()
        try { $exportHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)) }
        finally { $entryStream.Dispose() }
        Assert-Equal $expectedHash $exportHash 'Calibre export retains verified book bytes'
    } finally { $exportArchive.Dispose() }
    $checks.Add('Owner-only Calibre export contains the verified book and metadata sidecar')
    if ($Calibre) {
        & (Join-Path $PSScriptRoot 'Test-CalibreExport.ps1') -ArchivePath $exportPath
        $checks.Add('Installed Calibre imports the exported copy into an isolated library')
    }
    Invoke-Compose -Arguments @('up', '-d', '--force-recreate', '--wait', '--wait-timeout', '120')
    Assert-Equal $summary (Invoke-RestMethod "$baseUrl/api/imports/summary" -WebSession $ownerSession | ConvertTo-Json -Compress) 'Session after recreation'
    $signedOut = Invoke-RestMethod "$baseUrl/api/auth/logout" -WebSession $ownerSession -Method Post `
        -Headers @{ 'X-CSRF-TOKEN' = $login.csrfToken }
    Assert-Equal $false $signedOut.authenticated 'Owner sign-out'
    Assert-Equal 401 (Invoke-WebRequest "$baseUrl/api/imports" -WebSession $ownerSession -SkipHttpErrorCheck).StatusCode 'Signed-out API access'
    $checks.Add('Owner authentication survives recreation and sign-out revokes access')
    if ($Browser) {
        @{ url = $baseUrl; password = $ownerPassword; outputDirectory = (Join-Path $testRoot 'browser'); expectedHash = $expectedHash } |
            ConvertTo-Json -Compress | & node (Join-Path $PSScriptRoot 'Test-Browser.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Packaged browser checks failed.' }
        $checks.Add('Packaged desktop/mobile UI signs in, reviews missing metadata, imports, scans, and revokes sessions')
        Assert-Equal $expectedHash (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash 'Original retained after browser undo and reapproval'
        $checks.Add('Browser undo retains the original and reapproval republishes matching bytes')
        if ($Calibre) {
            & (Join-Path $PSScriptRoot 'Test-CalibreExport.ps1') -ArchivePath (Join-Path $testRoot 'browser/calibre-notebook.zip')
            $checks.Add('Installed Calibre preserves manually approved edition metadata from the browser export')
        }
    }
    $evidence = Join-Path $testRoot 'results.json'
    @{ project = $project; completedAt = [DateTime]::UtcNow.ToString('o'); checks = @($checks); libraryHash = $expectedHash } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidence
    Write-Output "Deployment checks passed. Evidence: $evidence"
    if ($KeepRunning) {
        $authenticationEnabled = 'false'
        Set-TestEnvironment $restoredLibrary 'paparr_restore'
        Invoke-Compose -Arguments @('up', '-d', '--force-recreate', '--wait', '--wait-timeout', '120')
        Write-Output "Disposable demo running at $baseUrl with authentication disabled. Compose project: $project"
    }
}
finally {
    if (!$KeepRunning) {
        # This project name and all mounts were created exclusively for this run.
        Invoke-Compose -Arguments @('down', '--volumes', '--remove-orphans', '--rmi', 'local')
    }
}
