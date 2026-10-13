[CmdletBinding()]
param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot), [string]$Version='0.4.0')
$ErrorActionPreference='Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {throw 'Invalid version.'}
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
Add-Type -AssemblyName System.IO.Compression
$tracked=@(& git -C $ProjectRoot ls-files)
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) {throw 'Stage the curated public source files in Git first.'}
$files=New-Object Collections.Generic.List[string]
foreach($relative in $tracked){
    if($relative -match '(^|/)(game|work|bin|obj|\.git)(/|$)' -or $relative -eq 'session.cfg') {throw "Private path tracked: $relative"}
    $files.Add($relative)
}
foreach($relative in @('dist/editor','dist/host','dist/module')){
    $folder=Join-Path $ProjectRoot $relative
    if(-not(Test-Path -LiteralPath $folder -PathType Container)){throw "Missing release directory: $relative"}
    foreach($file in Get-ChildItem -LiteralPath $folder -Recurse -File){
        $name=$file.FullName.Substring($ProjectRoot.Length+1).Replace('\','/')
        if($file.Name -match '^(UnityEngine|Assembly-CSharp|Assembly-UnityScript|THOTH)\.(dll|exe)$'){throw "Game binary cannot be released: $name"}
        $files.Add($name)
    }
}
$files.Add('dist/Launch-Editor.cmd')
$release=Join-Path $ProjectRoot 'dist/release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$archive=Join-Path $release "ThothTAS-windows-x64-v$Version.zip"
if(Test-Path -LiteralPath $archive){throw 'Public archive already exists. Choose a new version or preserve it elsewhere.'}
$sha=[Security.Cryptography.SHA256]::Create()
function Hash-Stream([IO.Stream]$Stream){return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-','').ToLowerInvariant()}
$expected=@{}
$records=New-Object Collections.Generic.List[object]
$fileStream=[IO.File]::Open($archive,[IO.FileMode]::CreateNew)
$zip=New-Object IO.Compression.ZipArchive($fileStream,[IO.Compression.ZipArchiveMode]::Create,$true)
try{
    foreach($relative in ($files | Sort-Object -Unique)){
        $path=Join-Path $ProjectRoot $relative
        $entryName="ThothTAS/$relative"
        $source=[IO.File]::OpenRead($path)
        try{
            $before=Hash-Stream $source; $source.Position=0
            $entry=$zip.CreateEntry($entryName,[IO.Compression.CompressionLevel]::Optimal)
            $destination=$entry.Open()
            try{$source.CopyTo($destination)}finally{$destination.Dispose()}
            $expected[$entryName]=$before
            $records.Add([ordered]@{path=$relative;sha256=$before;bytes=$source.Length})
        }finally{$source.Dispose()}
    }
}finally{$zip.Dispose();$fileStream.Dispose()}
$fileStream=[IO.File]::OpenRead($archive)
$zip=New-Object IO.Compression.ZipArchive($fileStream,[IO.Compression.ZipArchiveMode]::Read,$true)
try{
    if($zip.Entries.Count -ne $expected.Count){throw 'Archive entry count mismatch.'}
    foreach($entry in $zip.Entries){
        $stream=$entry.Open()
        try{if((Hash-Stream $stream) -ne $expected[$entry.FullName]){throw "Archive hash mismatch: $($entry.FullName)"}}finally{$stream.Dispose()}
    }
}finally{$zip.Dispose();$fileStream.Dispose()}
$source=[IO.File]::OpenRead($archive)
try{$archiveHash=Hash-Stream $source}finally{$source.Dispose();$sha.Dispose()}
$encoding=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $release "SHA256SUMS-v$Version.txt"),($archiveHash+'  '+[IO.Path]::GetFileName($archive)+"`n"),$encoding)
[IO.File]::WriteAllText((Join-Path $release "public-package-v$Version.json"),([ordered]@{version=$Version;archive=[IO.Path]::GetFileName($archive);sha256=$archiveHash;entriesVerified=$true;files=$records}|ConvertTo-Json -Depth 6),$encoding)
Write-Output "Public archive verified: $archive"
