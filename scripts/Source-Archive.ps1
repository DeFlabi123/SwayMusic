$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$archivePath = Join-Path $projectRoot 'artifacts\SwayMusic-Windows-source.zip'
$sources = @('src','tests','docs','design','scripts','licenses','.github','.config','LICENSE','README.md','FEATURES.md','PROGRESS.md','RELEASE_NOTES.md','THIRD-PARTY-NOTICES.md','Directory.Build.props','global.json','SwayMusic.slnx','.gitignore')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path (Join-Path $projectRoot 'artifacts') -Force | Out-Null
$archiveFile = [IO.File]::Open($archivePath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new($archiveFile, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($source in $sources) {
            $sourcePath = Join-Path $projectRoot $source
            $item = Get-Item -LiteralPath $sourcePath
            $files = if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $sourcePath -File -Recurse } else { @($item) }
            foreach ($file in $files) {
                $relativePath = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\','/')
                if ($relativePath -match '(^|/)(bin|obj|\.preview-profile|\.vs|\.git)/|(^|/)\.preview-script\.js$') { continue }
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relativePath, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        }
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tools') -File -Filter '*LICENSE*.txt') {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "tools/$($file.Name)", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose() }
}
finally { $archiveFile.Dispose() }
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
