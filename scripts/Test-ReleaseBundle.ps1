[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-FixtureGit {
    param([string]$Repository, [string[]]$GitArguments)

    $output = & git -C $Repository @GitArguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "Fixture git command failed: git $($GitArguments -join ' ')" }
    return ,$output
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$ExpectedMessage)

    $caught = $false
    try { & $Action }
    catch {
        $caught = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
    }
    if (-not $caught) { throw "Expected failure containing '$ExpectedMessage'." }
}

function Remove-OwnedFixtureDirectory {
    param([string]$Path, [string]$OwnedParent)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $parentFull = [IO.Path]::GetFullPath($OwnedParent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $parentFull + [IO.Path]::DirectorySeparatorChar
    $name = [IO.Path]::GetFileName($fullPath)
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        $name -notmatch '^fixture-[0-9a-f]{32}$') {
        throw "Refusing to recursively remove a path outside the owned test fixture: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
}

function Invoke-BundleScript {
    param([string]$ScriptPath, [string]$Destination)
    & $ScriptPath -Destination $Destination
}

function Get-ArchiveHash {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$fixtureParent = Join-Path ([IO.Path]::GetTempPath()) 'paparr-releasebundle-tests'
[void][IO.Directory]::CreateDirectory($fixtureParent)
$fixtureRoot = Join-Path $fixtureParent ('fixture-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)

try {
    $repository = Join-Path $fixtureRoot 'repo'
    $outputOne = Join-Path $repository '.tools/release-one'
    $outputTwo = Join-Path $repository '.tools/release-two'
    $scriptDirectory = Join-Path $repository 'scripts'
    $sourceDirectory = Join-Path $repository 'src'
    [void][IO.Directory]::CreateDirectory($scriptDirectory)
    [void][IO.Directory]::CreateDirectory($sourceDirectory)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'New-ReleaseBundle.ps1') `
        -Destination (Join-Path $scriptDirectory 'New-ReleaseBundle.ps1')
    $bundleScript = Join-Path $scriptDirectory 'New-ReleaseBundle.ps1'

    [IO.File]::WriteAllText((Join-Path $repository '.gitignore'), @'
.env
.env.*
!.env.example
.tools/
ingest/
library/
.paparr/
node_modules/
'@)
    [IO.File]::WriteAllText((Join-Path $repository '.env.example'), "Example=placeholder`n")
    [IO.File]::WriteAllText((Join-Path $sourceDirectory 'app.txt'), "tracked source`n")
    [IO.File]::WriteAllText((Join-Path $repository '.env'), 'DUMMY_SECRET_MUST_NOT_BE_READ')
    foreach ($ignoredDirectory in @('.tools', 'ingest', 'library', '.paparr', 'node_modules')) {
        $ignoredPath = Join-Path $repository $ignoredDirectory
        [void][IO.Directory]::CreateDirectory($ignoredPath)
        [IO.File]::WriteAllText((Join-Path $ignoredPath 'ignored.txt'), 'local-only')
    }

    [void](Invoke-FixtureGit -Repository $repository -GitArguments @('init', '--initial-branch=main'))
    [void](Invoke-FixtureGit -Repository $repository -GitArguments @('add', '.'))
    [void](Invoke-FixtureGit -Repository $repository -GitArguments @(
        '-c', 'user.name=Paparr Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'fixture source'
    ))

    [void](Invoke-BundleScript -ScriptPath $bundleScript -Destination $outputOne)
    $commit = ([string](Invoke-FixtureGit -Repository $repository -GitArguments @('rev-parse', 'HEAD'))).Trim().ToLowerInvariant()
    $shortCommit = $commit.Substring(0, 12)
    $archiveOne = Join-Path $outputOne "paparr-source-$shortCommit.zip"
    $manifestPath = Join-Path $outputOne 'manifest.json'
    Assert-True (Test-Path -LiteralPath $archiveOne -PathType Leaf) 'The source archive is missing.'
    Assert-True (Test-Path -LiteralPath $manifestPath -PathType Leaf) 'The external manifest is missing.'

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    Assert-True ($manifest.kind -eq 'source') 'The manifest kind is not source.'
    Assert-True ($manifest.commitSha -eq $commit) 'The manifest commit does not match HEAD.'
    Assert-True ($manifest.archiveSha256 -eq (Get-ArchiveHash $archiveOne)) 'The manifest archive hash is incorrect.'
    $parsedTimestamp = [DateTimeOffset]::MinValue
    Assert-True ([DateTimeOffset]::TryParse($manifest.createdAtUtc, [ref]$parsedTimestamp)) 'The manifest timestamp is invalid.'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archiveOne)
    try {
        $names = @($zip.Entries | ForEach-Object FullName | Sort-Object -CaseSensitive)
        $expected = @('Paparr/', 'Paparr/.env.example', 'Paparr/.gitignore', 'Paparr/scripts/',
            'Paparr/scripts/New-ReleaseBundle.ps1', 'Paparr/src/', 'Paparr/src/app.txt')
        Assert-True ([string]::Join('|', $names) -ceq [string]::Join('|', $expected)) `
            "The archive contains missing, ignored, or extra entries: $([string]::Join('|', $names))"
    }
    finally { $zip.Dispose() }

    [void](Invoke-BundleScript -ScriptPath $bundleScript -Destination $outputTwo)
    $archiveTwo = Join-Path $outputTwo "paparr-source-$shortCommit.zip"
    Assert-True ((Get-ArchiveHash $archiveOne) -eq (Get-ArchiveHash $archiveTwo)) 'Repeated git archive output was not reproducible.'

    $existing = Join-Path $fixtureRoot 'existing-destination'
    [void][IO.Directory]::CreateDirectory($existing)
    $marker = Join-Path $existing 'keep.txt'
    [IO.File]::WriteAllText($marker, 'do not overwrite')
    Assert-Throws { Invoke-BundleScript -ScriptPath $bundleScript -Destination $existing } 'already exists'
    Assert-True ((Get-Content -LiteralPath $marker -Raw) -eq 'do not overwrite') 'An existing destination was modified.'

    $trackedSource = Join-Path $sourceDirectory 'app.txt'
    $originalSource = [IO.File]::ReadAllText($trackedSource)
    [IO.File]::WriteAllText($trackedSource, "dirty change`n")
    Assert-Throws { Invoke-BundleScript -ScriptPath $bundleScript -Destination (Join-Path $fixtureRoot 'dirty-output') } 'working tree has changes'
    [IO.File]::WriteAllText($trackedSource, $originalSource)

    $unicodeEnvironmentDirectory = Join-Path $repository 'Dóc'
    [void][IO.Directory]::CreateDirectory($unicodeEnvironmentDirectory)
    [IO.File]::WriteAllText((Join-Path $unicodeEnvironmentDirectory '.env'), 'DUMMY_TRACKED_SECRET')
    [void](Invoke-FixtureGit -Repository $repository -GitArguments @('add', '-f', '--', 'Dóc/.env'))
    [void](Invoke-FixtureGit -Repository $repository -GitArguments @(
        '-c', 'user.name=Paparr Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'accidentally tracked env'
    ))
    Assert-Throws { Invoke-BundleScript -ScriptPath $bundleScript -Destination (Join-Path $fixtureRoot 'tracked-env-output') } 'Protected environment file is tracked'

    [void](Invoke-FixtureGit -Repository $repository -GitArguments @('rm', '--cached', '-f', '--', 'Dóc/.env'))
    [void](Invoke-FixtureGit -Repository $repository -GitArguments @(
        '-c', 'user.name=Paparr Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'remove tracked env'
    ))
    $linkTargetBlob = ([string](Invoke-FixtureGit -Repository $repository -GitArguments @('hash-object', 'src/app.txt'))).Trim()
    [void](Invoke-FixtureGit -Repository $repository -GitArguments @(
        'update-index', '--add', '--cacheinfo', "120000,$linkTargetBlob,src/linked-source"
    ))
    [void](Invoke-FixtureGit -Repository $repository -GitArguments @(
        '-c', 'user.name=Paparr Fixture', '-c', 'user.email=fixture@example.invalid', 'commit', '-m', 'tracked link mode'
    ))
    Assert-Throws { Invoke-BundleScript -ScriptPath $bundleScript -Destination (Join-Path $fixtureRoot 'tracked-link-output') } 'Tracked symbolic links are not allowed'

    Write-Output 'Release bundle fixture checks passed.'
}
finally {
    Remove-OwnedFixtureDirectory -Path $fixtureRoot -OwnedParent $fixtureParent
}
