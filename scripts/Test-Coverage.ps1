$ErrorActionPreference = 'Stop'
$temporaryRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
$testRoot = Join-Path $temporaryRoot ('paparr-coverage-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $first = Join-Path $testRoot 'unit'
    $second = Join-Path $testRoot 'integration'
    [IO.Directory]::CreateDirectory($first) | Out-Null
    [IO.Directory]::CreateDirectory($second) | Out-Null
    @'
<coverage><packages>
<package name="Paparr.API"><classes>
<class filename="Services/Example.cs"><lines><line number="10" hits="2"/><line number="11" hits="0"/><line number="12" hits="0"/></lines></class>
<class filename="Services/Example.cs"><lines><line number="10" hits="3"/></lines></class>
<class filename="Migrations/Initial.cs"><lines><line number="1" hits="0"/></lines></class>
<class filename="Generated.g.cs"><lines><line number="1" hits="0"/></lines></class>
</classes></package>
<package name="Other"><classes><class filename="Other.cs"><lines><line number="1" hits="0"/></lines></class></classes></package>
</packages></coverage>
'@ | Set-Content -LiteralPath (Join-Path $first 'coverage.cobertura.xml') -Encoding utf8NoBOM
    @'
<coverage><packages><package name="Paparr.API"><classes>
<class filename="Services\Example.cs"><lines><line number="10" hits="0"/><line number="11" hits="4"/><line number="12" hits="0"/></lines></class>
</classes></package></packages></coverage>
'@ | Set-Content -LiteralPath (Join-Path $second 'coverage.cobertura.xml') -Encoding utf8NoBOM
    $output = Join-Path $testRoot 'summary.md'
    $result = & (Join-Path $PSScriptRoot 'Measure-Coverage.ps1') -ResultsDirectory $testRoot -OutputPath $output
    if ($result.CoveredLines -ne 2 -or $result.ExecutableLines -ne 3 -or $result.SourceFiles -ne 1 -or $result.CoveragePercent -ne 66.7) {
        throw 'Coverage must union line hits across reports without counting duplicate classes or generated files.'
    }
    $empty = Join-Path $testRoot 'empty'
    [IO.Directory]::CreateDirectory($empty) | Out-Null
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Measure-Coverage.ps1') -ResultsDirectory $empty -OutputPath $output | Out-Null }
    catch { $rejected = $true }
    if (!$rejected) { throw 'Missing reports must fail instead of reporting zero coverage.' }
    Write-Output 'Coverage report checks passed.'
}
finally {
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($testRoot)) -ne $temporaryRoot) {
        throw 'The coverage fixture directory must remain directly inside the temporary directory.'
    }
    [IO.Directory]::Delete($testRoot, $true)
}
