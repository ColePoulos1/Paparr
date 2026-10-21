param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$targetRoot = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($targetRoot) | Out-Null

function Add-Entry($Archive, [string]$Name, [string]$Content) {
    $entry = $Archive.CreateEntry($Name, [IO.Compression.CompressionLevel]::NoCompression)
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($Content) } finally { $writer.Dispose() }
}
$books = @(
    @{ File = 'The Quiet Atlas.epub'; Title = 'The Quiet Atlas'; Author = 'Mara Finch' },
    @{ File = 'A Field Guide to Small Things.epub'; Title = 'A Field Guide to Small Things'; Author = 'Elias North' },
    @{ File = 'Notes from the Orchard.epub'; Title = 'Notes from the Orchard'; Author = 'June Adler' },
    @{ File = 'Untitled.epub'; Title = ''; Author = '' }
)
foreach ($book in $books) {
    $path = Join-Path $targetRoot $book.File
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $title = [Security.SecurityElement]::Escape($book.Title)
        $author = [Security.SecurityElement]::Escape($book.Author)
        Add-Entry $archive 'mimetype' 'application/epub+zip'
        Add-Entry $archive 'META-INF/container.xml' '<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>'
        Add-Entry $archive 'content.opf' "<?xml version='1.0'?><package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>paparr-demo-$title</dc:identifier><dc:title>$title</dc:title><dc:creator>$author</dc:creator><dc:language>en</dc:language></metadata><manifest><item id='chapter' href='chapter.xhtml' media-type='application/xhtml+xml'/><item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest><spine><itemref idref='chapter'/></spine></package>"
        Add-Entry $archive 'chapter.xhtml' '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Demo</title></head><body><h1>Paparr demo fixture</h1><p>This synthetic ebook exists to exercise importing and metadata review.</p></body></html>'
        Add-Entry $archive 'nav.xhtml' '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="chapter.xhtml">Demo</a></li></ol></nav></body></html>'
    } finally { $archive.Dispose(); $stream.Dispose() }
}
$broken = [IO.File]::Open((Join-Path $targetRoot 'Incomplete download.pdf'), [IO.FileMode]::CreateNew)
try { $bytes = [Text.Encoding]::UTF8.GetBytes('Intentionally incomplete PDF fixture.'); $broken.Write($bytes) }
finally { $broken.Dispose() }
Write-Output "Created five synthetic demo files in $targetRoot."
