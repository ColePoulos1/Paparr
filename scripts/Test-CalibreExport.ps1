param([Parameter(Mandatory)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$archiveFile = (Resolve-Path -LiteralPath $ArchivePath).Path
$ebookMeta = (Get-Command ebook-meta -ErrorAction Stop).Source
$calibreDb = (Get-Command calibredb -ErrorAction Stop).Source
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('pcal-' + [Guid]::NewGuid().ToString('N').Substring(0, 16))
$previousConfig = $env:CALIBRE_CONFIG_DIRECTORY
$previousEncoding = $env:PYTHONIOENCODING
$originalHash = (Get-FileHash -LiteralPath $archiveFile -Algorithm SHA256).Hash

function Invoke-Calibre([string]$Executable, [string[]]$Arguments) {
    $output = & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Calibre command failed with exit code $LASTEXITCODE." }
    return ($output -join "`n")
}
function Read-Dc([xml]$Document, [string]$Name) {
    return @($Document.GetElementsByTagName($Name, 'http://purl.org/dc/elements/1.1/') | ForEach-Object { $_.InnerText })
}

try {
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    $env:CALIBRE_CONFIG_DIRECTORY = Join-Path $temporary 'config'
    $env:PYTHONIOENCODING = 'utf-8'
    $zip = [IO.Compression.ZipFile]::OpenRead($archiveFile)
    try {
        $names = @($zip.Entries.FullName)
        if ($names.Count -ne 3 -or 'metadata.opf' -notin $names -or 'README.txt' -notin $names -or
            (@($names | Where-Object { $_ -in @('book.epub', 'book.pdf') }).Count -ne 1)) {
            throw 'Expected a Paparr export with exactly one book, metadata.opf, and README.txt.'
        }
        if (($zip.Entries | Measure-Object -Property Length -Sum).Sum -gt 512MB) { throw 'Export exceeds the test size limit.' }
        foreach ($entry in $zip.Entries) {
            $inputStream = $entry.Open()
            $outputStream = [IO.File]::Open((Join-Path $temporary $entry.FullName), [IO.FileMode]::CreateNew)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }

    $bookName = $names | Where-Object { $_ -in @('book.epub', 'book.pdf') }
    $bookPath = Join-Path $temporary $bookName
    $opfPath = Join-Path $temporary 'metadata.opf'
    [xml]$expected = [IO.File]::ReadAllText($opfPath)
    $version = Invoke-Calibre $ebookMeta @('--version')
    Invoke-Calibre $ebookMeta @($bookPath, '--from-opf', $opfPath) | Out-Null
    $libraryPath = Join-Path $temporary 'library'
    Invoke-Calibre $calibreDb @('add', '--with-library', $libraryPath, $bookPath) | Out-Null
    $books = @(Invoke-Calibre $calibreDb @('list', '--with-library', $libraryPath, '--for-machine', '--fields', 'id,title') | ConvertFrom-Json)
    if ($books.Count -ne 1) { throw 'Calibre did not import exactly one book.' }
    [xml]$actual = Invoke-Calibre $calibreDb @('show_metadata', '--with-library', $libraryPath, '--as-opf', [string]$books[0].id)
    foreach ($field in @('title', 'creator', 'publisher')) {
        $expectedValues = @(Read-Dc $expected $field)
        if ($expectedValues.Count -gt 0 -and ($expectedValues -join ', ') -ne (@(Read-Dc $actual $field) -join ', ')) {
            throw "Calibre did not preserve the approved $field."
        }
    }
    $expectedIsbn = @($expected.GetElementsByTagName('identifier', 'http://purl.org/dc/elements/1.1/') |
        Where-Object { $_.GetAttribute('scheme', 'http://www.idpf.org/2007/opf') -eq 'ISBN' } | ForEach-Object { $_.InnerText })
    if ($expectedIsbn.Count -gt 0 -and $expectedIsbn[0] -notin @(Read-Dc $actual 'identifier')) {
        throw 'Calibre did not preserve the approved ISBN.'
    }
    $expectedYear = @(Read-Dc $expected 'date')
    if ($expectedYear.Count -gt 0 -and !(@(Read-Dc $actual 'date') | Where-Object { $_.StartsWith($expectedYear[0]) })) {
        throw 'Calibre did not preserve the approved publication year.'
    }
    $expectedLanguage = @(Read-Dc $expected 'language')
    if ($expectedLanguage.Count -gt 0) {
        $language = $expectedLanguage[0]
        try { $language = [Globalization.CultureInfo]::GetCultureInfo($language).ThreeLetterISOLanguageName } catch { }
        if ($language -notin @(Read-Dc $actual 'language')) { throw 'Calibre did not preserve the approved language.' }
    }
    if ((Get-FileHash -LiteralPath $archiveFile -Algorithm SHA256).Hash -ne $originalHash) { throw 'The original export changed.' }
    [pscustomobject]@{ Version = $version; ImportedBooks = $books.Count; MetadataPreserved = $true; ArchiveUnchanged = $true }
} finally {
    $env:CALIBRE_CONFIG_DIRECTORY = $previousConfig
    $env:PYTHONIOENCODING = $previousEncoding
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedTemporary.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTemporary) -notmatch '^pcal-[0-9a-f]{16}$') { throw 'Refusing to delete an unexpected test directory.' }
    if (Test-Path -LiteralPath $resolvedTemporary) { Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force }
}
