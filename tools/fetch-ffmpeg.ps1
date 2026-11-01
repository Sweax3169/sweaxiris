# Downloads the FFmpeg 7.1.1 shared libraries SweaxIris links against (gyan.dev build, GPLv3)
# and keeps only the four it needs. The archive is pinned by SHA-256.
param([string]$Dest = "$PSScriptRoot\..\vendor\ffmpeg")
$ErrorActionPreference = 'Stop'
$url  = 'https://github.com/GyanD/codexffmpeg/releases/download/7.1.1/ffmpeg-7.1.1-full_build-shared.zip'
$sha  = '9f28727e8b472a04c1d2e520aaa425dca82721b995139b35710091130ea6e699'
$need = 'avcodec-61.dll', 'avformat-61.dll', 'avutil-59.dll', 'swresample-5.dll'
New-Item -ItemType Directory -Force $Dest | Out-Null
if (($need | Where-Object { -not (Test-Path (Join-Path $Dest $_)) }).Count -eq 0) { Write-Host 'FFmpeg libraries already here.'; exit 0 }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('sweaxiris-ffmpeg-' + [guid]::NewGuid())
New-Item -ItemType Directory $tmp | Out-Null
$zip = Join-Path $tmp 'ffmpeg.zip'
Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
$got = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($got -ne $sha) { throw "FFmpeg archive hash mismatch: $got" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
  foreach ($e in $z.Entries) {
    if ($need -contains $e.Name -or $e.FullName -like '*/LICENSE') {
      $name = if ($e.Name -eq 'LICENSE') { 'FFMPEG-LICENSE.txt' } else { $e.Name }
      [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $Dest $name), $true)
    }
  }
} finally { $z.Dispose() }
Remove-Item $tmp -Recurse -Force
Get-ChildItem $Dest | Format-Table Name, Length
