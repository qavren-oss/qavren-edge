<#
.SYNOPSIS
Asserts every packed .nupkg carries the suite's package metadata: icon.png and README.md at the
package root, PackageIcon/PackageReadmeFile pointing at them, and the sub-project's tags.
Run after `dotnet pack QavrenEdge.slnx -c Release -o artifacts/packages`, from the repo root.
#>
param([string]$PackagesDir = 'artifacts/packages', [int]$ExpectedCount = 17)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# One tag that only the sub-project's Directory.Build.props supplies, keyed by id prefix.
# Order matters: the first matching prefix wins, so Ingestion.* precedes the plain foundation rule.
$folderTag = @(
    @{ Prefix = 'Qavren.Edge.Ingestion'; Tag = 'chunking' },
    @{ Prefix = 'Qavren.Edge.Chat';      Tag = 'genai' },
    @{ Prefix = 'Qavren.Edge.Rag';       Tag = 'genai' },
    @{ Prefix = 'Qavren.Edge.Onnx';      Tag = 'onnxruntime' },
    @{ Prefix = 'Qavren.Edge.Embeddings';Tag = 'onnxruntime' },
    @{ Prefix = 'Qavren.Edge.VectorData';Tag = 'onnxruntime' },
    @{ Prefix = 'Qavren.Edge';           Tag = 'hosting' }
)

$nupkgs = @(Get-ChildItem "$PackagesDir/*.nupkg" | Where-Object Name -notlike '*.symbols.nupkg')
if ($nupkgs.Count -ne $ExpectedCount) { throw "expected $ExpectedCount packages in $PackagesDir, found $($nupkgs.Count)" }

foreach ($nupkg in $nupkgs) {
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
    try {
        $names = $zip.Entries.FullName
        foreach ($required in 'icon.png', 'README.md') {
            if ($names -notcontains $required) { throw "$($nupkg.Name) is missing $required at the package root" }
        }
        $entry = $zip.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
        $reader = New-Object IO.StreamReader($entry.Open())
        try { $xml = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $m = $xml.package.metadata
        if ($m.icon -ne 'icon.png')     { throw "$($m.id): PackageIcon is '$($m.icon)', expected icon.png" }
        if ($m.readme -ne 'README.md')  { throw "$($m.id): PackageReadmeFile is '$($m.readme)', expected README.md" }
        if ($m.license.'#text' -ne 'MIT') { throw "$($m.id): licence expression is '$($m.license.'#text')', expected MIT" }
        $tags = ($m.tags -split ' ')
        $rule = $folderTag | Where-Object { $m.id.StartsWith($_.Prefix) } | Select-Object -First 1
        if ($tags -notcontains $rule.Tag) { throw "$($m.id): tags '$($m.tags)' lack the sub-project tag '$($rule.Tag)'" }
        foreach ($suite in 'sqlite', 'maui') {
            if ($tags -notcontains $suite) { throw "$($m.id): tags '$($m.tags)' lack the suite tag '$suite'" }
        }
    }
    finally { $zip.Dispose() }
}
Write-Host "OK: $($nupkgs.Count) packages carry icon.png, README.md, MIT and their sub-project tags"
