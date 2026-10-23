param(
    [switch]$SkipIntegration,
    [switch]$SkipBrowser,
    [switch]$Install,
    [switch]$Coverage
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Invoke-Check([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}
Push-Location $repoRoot
try {
    if ($Coverage -and $SkipIntegration) { throw 'Combined coverage requires the PostgreSQL integration checks.' }
    $coverageDirectory = Join-Path $repoRoot ('.tools/coverage-' + [Guid]::NewGuid().ToString('N'))
    $unitArguments = @('test', 'tests/Paparr.UnitTests', '--configuration', 'Release', '--no-build')
    $integrationArguments = @('test', 'tests/Paparr.IntegrationTests', '--configuration', 'Release', '--no-build')
    if ($Coverage) {
        $unitArguments += @('--collect:XPlat Code Coverage', '--results-directory', (Join-Path $coverageDirectory 'unit'))
        $integrationArguments += @('--collect:XPlat Code Coverage', '--results-directory', (Join-Path $coverageDirectory 'integration'))
    }
    $nodeMajor = (& node -p 'process.versions.node.split(".")[0]') -as [int]
    if ($nodeMajor -ne 24) { throw 'Verification requires Node 24. See .node-version.' }
    Invoke-Check node @('--check', (Join-Path $PSScriptRoot 'Test-Browser.mjs'))
    Invoke-Check pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-Coverage.ps1'))
    Invoke-Check pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-ReleaseBundle.ps1'))
    Invoke-Check dotnet @('build', 'Paparr.slnx', '--configuration', 'Release')
    Invoke-Check dotnet $unitArguments
    if (!$SkipIntegration) {
        Invoke-Check docker @('info', '--format', '{{.ServerVersion}}')
        Invoke-Check dotnet $integrationArguments
    } else { Write-Warning 'PostgreSQL integration checks were explicitly skipped.' }
    if ($Coverage) {
        Invoke-Check pwsh @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Measure-Coverage.ps1'),
            '-ResultsDirectory', $coverageDirectory, '-OutputPath', (Join-Path $coverageDirectory 'summary.md'))
    }
    Push-Location src/Paparr.UI
    try {
        if ($Install) { Invoke-Check npm @('ci') }
        Invoke-Check node @('--run', 'api:check', '--', '--no-build')
        Invoke-Check node @('--run', 'lint')
        Invoke-Check node @('--run', 'test')
        Invoke-Check node @('--run', 'build')
        if (!$SkipBrowser) { Invoke-Check node @('--run', 'test:e2e') }
        else { Write-Warning 'Browser checks were explicitly skipped.' }
    } finally { Pop-Location }
} finally { Pop-Location }
