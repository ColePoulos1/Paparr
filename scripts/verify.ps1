param(
    [switch]$SkipIntegration,
    [switch]$SkipBrowser,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
function Invoke-Check([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}
Push-Location $repoRoot
try {
    $nodeMajor = (& node -p 'process.versions.node.split(".")[0]') -as [int]
    if ($nodeMajor -ne 24) { throw 'Verification requires Node 24. See .node-version.' }
    Invoke-Check dotnet @('build', 'Paparr.slnx', '--configuration', 'Release')
    Invoke-Check dotnet @('test', 'tests/Paparr.UnitTests', '--configuration', 'Release', '--no-build')
    if (!$SkipIntegration) {
        Invoke-Check docker @('info', '--format', '{{.ServerVersion}}')
        Invoke-Check dotnet @('test', 'tests/Paparr.IntegrationTests', '--configuration', 'Release', '--no-build')
    } else { Write-Warning 'PostgreSQL integration checks were explicitly skipped.' }
    Push-Location src/Paparr.UI
    try {
        if ($Install) { Invoke-Check npm @('ci') }
        Invoke-Check node @('--run', 'lint')
        Invoke-Check node @('--run', 'test')
        Invoke-Check node @('--run', 'build')
        if (!$SkipBrowser) { Invoke-Check node @('--run', 'test:e2e') }
        else { Write-Warning 'Browser checks were explicitly skipped.' }
    } finally { Pop-Location }
} finally { Pop-Location }
