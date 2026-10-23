param(
    [string]$ResultsDirectory = './tests',
    [string]$OutputPath = './.tools/backend-coverage.md'
)
$ErrorActionPreference = 'Stop'
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter coverage.cobertura.xml -File -Recurse)
if (!$reports.Count) { throw 'No Cobertura reports found. Run backend tests with --collect:"XPlat Code Coverage" first.' }
$files = [Collections.Generic.Dictionary[string, Collections.Generic.Dictionary[int, bool]]]::new([StringComparer]::Ordinal)
foreach ($report in $reports) {
    [xml]$coverage = [IO.File]::ReadAllText($report.FullName)
    foreach ($package in $coverage.coverage.packages.package) {
        if ($package.name -ne 'Paparr.API') { continue }
        foreach ($class in $package.classes.class) {
            $filename = ([string]$class.filename).Replace('\', '/')
            if ($filename -match '(^|/)Migrations/' -or $filename -match '\.g\.cs$') { continue }
            if (!$files.ContainsKey($filename)) { $files[$filename] = [Collections.Generic.Dictionary[int, bool]]::new() }
            foreach ($line in $class.lines.line) {
                if ($null -eq $line) { continue }
                $number = [int]$line.number
                $hit = [long]$line.hits -gt 0
                $files[$filename][$number] = $hit -or ($files[$filename].ContainsKey($number) -and $files[$filename][$number])
            }
        }
    }
}
$rows = @($files.Keys | Sort-Object | ForEach-Object {
    $lines = $files[$_]
    if ($lines.Count) {
        [pscustomobject]@{ File = $_; Covered = @($lines.Values | Where-Object { $_ }).Count; Total = $lines.Count }
    }
})
if (!$rows.Count) { throw 'The reports contain no Paparr.API source lines.' }
$total = ($rows | Measure-Object Total -Sum).Sum
$covered = ($rows | Measure-Object Covered -Sum).Sum
$percent = [Math]::Round(100 * $covered / $total, 1)
$markdown = [Collections.Generic.List[string]]::new()
$markdown.Add('# Backend source line coverage')
$markdown.Add('')
$markdown.Add("$covered of $total executable source lines covered ($percent%).")
$markdown.Add('')
$markdown.Add('Combined report hits by source file and line. Generated migrations and .g.cs files are excluded. Combine only reports from the same source revision. This measures executed lines, not correctness or combined branch coverage.')
$markdown.Add('')
$markdown.Add('| Source | Covered | Executable | Coverage |')
$markdown.Add('| --- | ---: | ---: | ---: |')
foreach ($row in $rows) {
    $percentage = [Math]::Round(100 * $row.Covered / $row.Total, 1)
    $markdown.Add(('| `{0}` | {1} | {2} | {3}% |' -f $row.File.Replace('|', '\|'), $row.Covered, $row.Total, $percentage))
}
$target = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
[IO.File]::WriteAllLines($target, $markdown, [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ CoveredLines = $covered; ExecutableLines = $total; CoveragePercent = $percent; SourceFiles = $rows.Count; Report = $target }
