[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string[]]$GitArguments
    )

    $output = @(& git -C $Repository @GitArguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "git $($GitArguments -join ' ') failed with exit code $exitCode."
    }
    return ,$output
}

function Invoke-GitRaw {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string[]]$GitArguments
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.ArgumentList.Add('-C')
    $startInfo.ArgumentList.Add($Repository)
    foreach ($argument in $GitArguments) { $startInfo.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "git $($GitArguments -join ' ') failed with exit code $($process.ExitCode): $stderr"
    }
    return $stdout
}

function Test-PathWithin {
    param([string]$Parent, [string]$Child)

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $childFull = [IO.Path]::GetFullPath($Child)
    $prefix = $parentFull + [IO.Path]::DirectorySeparatorChar
    return $childFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Remove-OwnedTemporaryDirectory {
    param([string]$Path, [string]$Parent)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $name = [IO.Path]::GetFileName($fullPath)
    if (-not (Test-PathWithin -Parent $Parent -Child $fullPath) -or $name -notmatch '^\.paparr-source-bundle-[0-9a-f]{32}$') {
        throw 'Refusing to remove a temporary path outside the owned release directory.'
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

$destinationFull = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationFull) {
    throw "Destination already exists: $destinationFull"
}
$parent = Split-Path -Parent $destinationFull
if ([string]::IsNullOrWhiteSpace($parent) -or -not (Test-Path -LiteralPath $parent -PathType Container)) {
    throw 'The destination parent directory must already exist.'
}

$repositoryOutput = Invoke-Git -Repository $PSScriptRoot -GitArguments @('rev-parse', '--show-toplevel')
$repository = ([string]($repositoryOutput | Select-Object -Last 1)).Trim()
if ([string]::IsNullOrWhiteSpace($repository)) { throw 'The current directory is not inside a Git repository.' }
$repository = [IO.Path]::GetFullPath($repository)
if ($destinationFull.Equals($repository, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose a new destination folder, not the source repository root.'
}
if (Test-PathWithin -Parent $repository -Child $destinationFull) {
    $toolsRoot = Join-Path $repository '.tools'
    if (-not (Test-PathWithin -Parent $toolsRoot -Child $destinationFull)) {
        throw 'Choose a destination outside the source repository or below its ignored .tools directory.'
    }
    $relativeDestination = [IO.Path]::GetRelativePath($repository, $destinationFull).Replace('\', '/')
    $null = & git -C $repository check-ignore --quiet -- $relativeDestination 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'A destination under .tools must be ignored by Git.' }
}

$commitOutput = Invoke-Git -Repository $repository -GitArguments @('rev-parse', '--verify', 'HEAD^{commit}')
$commit = ([string]($commitOutput | Select-Object -Last 1)).Trim().ToLowerInvariant()
if ($commit -notmatch '^[0-9a-f]{40,64}$') { throw 'Git did not return a valid commit SHA.' }
$shortCommit = $commit.Substring(0, 12)

$treeOutput = Invoke-GitRaw -Repository $repository -GitArguments @('ls-tree', '-r', '-z', '--full-tree', $commit)
foreach ($entry in $treeOutput.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
    $separator = $entry.IndexOf("`t", [StringComparison]::Ordinal)
    if ($separator -lt 0) { throw 'Git returned an unreadable tree entry.' }
    $mode = ($entry.Substring(0, $separator) -split ' ', 2)[0]
    $path = $entry.Substring($separator + 1).Replace('\', '/')
    if ($mode -eq '120000') { throw "Tracked symbolic links are not allowed in a source bundle: $path" }
    if ($mode -eq '160000') { throw "Tracked submodules are not allowed in a source bundle: $path" }

    $segments = $path.Split('/')
    foreach ($segment in $segments) {
        if ($segment -match '^\.env(?:\..*)?$' -and $segment -ne '.env.example') {
            throw "Protected environment file is tracked: $path"
        }
        if ($segment -in @('.tools', 'ingest', 'library', '.paparr', 'node_modules')) {
            throw "Protected runtime or local-data path is tracked: $path"
        }
    }
}

$statusOutput = Invoke-Git -Repository $repository -GitArguments @('status', '--porcelain=v1', '--untracked-files=all')
$status = ([string]::Join("`n", $statusOutput)).Trim()
if (-not [string]::IsNullOrEmpty($status)) {
    throw 'The Git working tree has changes or untracked non-ignored files; commit or remove them before packaging.'
}

$temporaryDirectory = Join-Path $parent ('.paparr-source-bundle-' + [Guid]::NewGuid().ToString('N'))
$archiveName = "paparr-source-$shortCommit.zip"
$archivePath = Join-Path $temporaryDirectory $archiveName
$manifestPath = Join-Path $temporaryDirectory 'manifest.json'
[void][IO.Directory]::CreateDirectory($temporaryDirectory)
try {
    [void](Invoke-Git -Repository $repository -GitArguments @(
        'archive', '--format=zip', '--prefix=Paparr/', $commit, '--output', $archivePath
    ))
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw 'git archive did not create the expected source archive.' }
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        kind = 'source'
        commitSha = $commit
        archiveSha256 = $archiveHash
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    }
    $json = $manifest | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText($manifestPath, $json + "`n", [Text.UTF8Encoding]::new($false))

    # Directory.Move fails if a destination appeared after the initial check, so no existing bundle is overwritten.
    [IO.Directory]::Move($temporaryDirectory, $destinationFull)
    Write-Output "Created source bundle: $destinationFull"
}
catch {
    Remove-OwnedTemporaryDirectory -Path $temporaryDirectory -Parent $parent
    throw
}
