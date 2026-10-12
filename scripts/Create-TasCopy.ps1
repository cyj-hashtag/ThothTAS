#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$SourceDirectory = 'D:\Play\Steam\steamapps\common\THOTH',
    [string]$BepInExArchive = '',
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$PinnedBepInExSha256 = '37651c79e40d6f909572a4f461ac25350bb3ef8fe7fbd29f1aa8791a33b84c82'
$PinnedBaselineSha256 = 'afcb6b465d05796d3ca8179fbeefa11ccf315a0115c4b87ab49bacb579be581e'
$PinnedBepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x86_5.4.23.5.zip'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/')
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\', '/')
$GameDirectory = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'game'))
$WorkDirectory = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'work'))
$Fingerprint = Join-Path $ProjectRoot 'docs/game-fingerprint.tsv'
$Baseline = Join-Path $ProjectRoot 'docs/baseline/THOTH.sav'
$BackupDirectory = Join-Path $WorkDirectory ('copy-backups/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$copiedFiles = 0
$preservedFiles = 0
$backupFiles = 0
$instanceLock = $null
$archive = $null

function Is-Within([string]$Root, [string]$Path) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return [IO.Path]::GetFullPath($Path).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}
function Assert-NoReparseAncestors([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "拒绝符号链接或目录联接，避免副本写入原版：$current" }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if (!$parent -or $parent -eq $current) { break }
        $current = $parent
    }
}
function Safe-Child([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw "不安全的绝对或流路径：$Relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    if (!(Is-Within $Root $path)) { throw "路径越界：$Relative" }
    Assert-NoReparseAncestors $path
    return $path
}
function File-Sha([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw "缺少文件：$Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Stream-Sha([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $Stream.Dispose() }
}
function Backup-ManagedFile([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    if (!(Is-Within $GameDirectory $Path)) { throw '只能备份副本中的受控文件。' }
    $relative = [IO.Path]::GetFullPath($Path).Substring($GameDirectory.TrimEnd('\', '/').Length + 1)
    $target = Safe-Child $BackupDirectory $relative
    # Multiple required keys may touch one config. Keep its first, original backup.
    if (Test-Path -LiteralPath $target -PathType Leaf) { return }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $Path -Destination $target
    $script:backupFiles++
}
function Write-ManagedBytesAtomic([string]$Path, [byte[]]$Bytes) {
    # Replacing an existing directory entry avoids writing through an NTFS hard link.
    if (!(Is-Within $GameDirectory $Path)) { throw '只允许原子替换副本中的受控文件。' }
    Assert-NoReparseAncestors $Path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $temporary = $Path + '.thoth-tas-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    if (!(Is-Within $GameDirectory $temporary)) { throw '临时文件必须在副本内。' }
    $stream = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush() } finally { $stream.Dispose() }
    Assert-NoReparseAncestors $Path
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        # File.Replace works on Windows PowerShell 5.1 as well as PowerShell 7.
        # It replaces this name with the new file; other names for the old file keep their data.
        # Pass an actual .NET null string; PowerShell's ordinary $null binds as an empty path.
        [IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value)
    } else { [IO.File]::Move($temporary, $Path) }
}
function Ini-Value([string]$Path, [string]$Section, [string]$Key) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $inside = $false
    $values = @()
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\s*\[([^\]]+)\]\s*$') { $inside = $Matches[1] -eq $Section; continue }
        if ($inside -and $line -match ('^\s*' + [Regex]::Escape($Key) + '\s*=\s*(.*?)\s*$')) { $values += $Matches[1] }
    }
    if ($values.Count -gt 1) { throw "配置键重复，保留原文件并拒绝继续：$Path [$Section] $Key" }
    if ($values.Count -eq 1) { return $values[0] }
    return $null
}
function Set-RequiredIni([string]$Path, [string]$Section, [string]$Key, [string]$Value) {
    if ((Ini-Value $Path $Section $Key) -eq $Value) { return }
    Backup-ManagedFile $Path
    $lines = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $Path -PathType Leaf) { $lines.AddRange([string[]][IO.File]::ReadAllLines($Path)) }
    $start = -1; $end = $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[([^\]]+)\]\s*$') {
            if ($start -ge 0) { $end = $i; break }
            if ($Matches[1] -eq $Section) { $start = $i }
        }
    }
    if ($start -lt 0) { $lines.Add(''); $lines.Add('[' + $Section + ']'); $lines.Add($Key + ' = ' + $Value) }
    else {
        $replaced = $false
        for ($i = $start + 1; $i -lt $end; $i++) {
            if ($lines[$i] -match ('^\s*' + [Regex]::Escape($Key) + '\s*=')) { $lines[$i] = $Key + ' = ' + $Value; $replaced = $true; break }
        }
        if (!$replaced) { $lines.Insert($end, $Key + ' = ' + $Value) }
    }
    $text = [string]::Join([Environment]::NewLine, $lines.ToArray()) + [Environment]::NewLine
    Write-ManagedBytesAtomic $Path ([Text.UTF8Encoding]::new($false).GetBytes($text))
}
function Verify-RequiredConfiguration {
    $config = Safe-Child $GameDirectory 'BepInEx/config/BepInEx.cfg'
    foreach ($pair in @(@('Assembly', 'UnityEngine.dll'), @('Type', 'MonoBehaviour'), @('Method', '.cctor'))) {
        if ((Ini-Value $config 'Preloader.Entrypoint' $pair[0]) -ne $pair[1]) { throw "预加载配置错误：[$('Preloader.Entrypoint')] $($pair[0]) 必须为 $($pair[1])。" }
    }
    $doorstop = Safe-Child $GameDirectory 'doorstop_config.ini'
    if ((Ini-Value $doorstop 'General' 'enabled') -ne 'true') { throw 'Doorstop必须启用。' }
    $target = Ini-Value $doorstop 'General' 'target_assembly'
    if ($null -eq $target -or $target.Replace('/', '\') -ne 'BepInEx\core\BepInEx.Preloader.dll') { throw 'Doorstop目标程序集必须为BepInEx\core\BepInEx.Preloader.dll。' }
}

try {
    if (!(Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "项目目录不存在：$ProjectRoot" }
    if (!(Test-Path -LiteralPath $SourceDirectory -PathType Container)) { throw "只读源游戏目录不存在：$SourceDirectory" }
    Assert-NoReparseAncestors $ProjectRoot
    Assert-NoReparseAncestors $SourceDirectory
    Assert-NoReparseAncestors $GameDirectory
    if ($SourceDirectory -eq $GameDirectory -or (Is-Within $SourceDirectory $GameDirectory) -or (Is-Within $GameDirectory $SourceDirectory)) { throw '源游戏与项目副本必须为两个互不包含的目录。' }
    if (!(Is-Within $ProjectRoot $GameDirectory)) { throw '副本目录必须位于项目内。' }

    $manifest = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [IO.File]::ReadAllLines($Fingerprint)) {
        if (!$line.Trim() -or $line.TrimStart().StartsWith('#')) { continue }
        $parts = $line.Split([char]9)
        if ($parts.Length -ne 2 -or $parts[1] -notmatch '^[0-9a-fA-F]{64}$') { throw '固定游戏指纹清单格式错误；此脚本不会重新生成指纹。' }
        if ($manifest.ContainsKey($parts[0])) { throw ('固定游戏指纹路径重复：' + $parts[0]) }
        $manifest.Add($parts[0], $parts[1].ToLowerInvariant())
    }
    foreach ($required in @('THOTH.exe', 'THOTH_Data/Managed/Assembly-CSharp.dll', 'THOTH_Data/Managed/Assembly-CSharp-firstpass.dll', 'THOTH_Data/Managed/Assembly-UnityScript-firstpass.dll')) {
        if (!$manifest.ContainsKey($required)) { throw "固定指纹缺少必要文件：$required" }
    }
    if ($manifest.Count -lt 5 -or @($manifest.Keys | Where-Object { $_ -match '^THOTH_Data/level\d+$' }).Count -eq 0) { throw '固定指纹缺少关卡资源。' }
    foreach ($entry in $manifest.GetEnumerator()) {
        $source = Safe-Child $SourceDirectory $entry.Key
        if ((File-Sha $source) -ne $entry.Value) { throw "源游戏版本不匹配，拒绝接受新指纹：$($entry.Key)" }
        $destination = Safe-Child $GameDirectory $entry.Key
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            if ((File-Sha $destination) -ne $entry.Value) { throw "已有副本文件指纹不同；保留原文件并拒绝覆盖：$($entry.Key)" }
        } elseif ($ValidateOnly) { throw "副本缺少固定版本文件：$($entry.Key)" }
    }
    $baselineHash = File-Sha $Baseline
    if ($baselineHash -ne $PinnedBaselineSha256) { throw '固定初始存档本身的SHA256不符，拒绝使用新的基线。当前副本存档变化不受此限制。' }
    $modules = @(
        @{source = (Join-Path $ProjectRoot 'dist/module/ThothTAS.Runtime.dll'); relative = 'BepInEx/plugins/ThothTAS.Runtime.dll'},
        @{source = (Join-Path $ProjectRoot 'dist/module/ThothTAS.Preloader.dll'); relative = 'BepInEx/patchers/ThothTAS.Preloader.dll'}
    )
    foreach ($module in $modules) {
        $module.hash = File-Sha $module.source
        if ($ValidateOnly -and (File-Sha (Safe-Child $GameDirectory $module.relative)) -ne $module.hash) { throw "副本模块与dist/module不一致：$($module.relative)" }
    }

    if (!$BepInExArchive) { $BepInExArchive = Join-Path $WorkDirectory 'BepInEx_win_x86_5.4.23.5.zip' }
    $BepInExArchive = [IO.Path]::GetFullPath($BepInExArchive)
    Assert-NoReparseAncestors $BepInExArchive
    if (!(Test-Path -LiteralPath $BepInExArchive -PathType Leaf)) {
        if ($ValidateOnly) { throw '只读校验需要已下载的BepInEx固定版本zip；不会在ValidateOnly中下载。' }
        if (!(Is-Within $WorkDirectory $BepInExArchive)) { throw '下载文件只能写入项目work目录；外部Archive参数只允许读取已有文件。' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($BepInExArchive)) | Out-Null
        Invoke-WebRequest -Uri $PinnedBepInExUrl -OutFile $BepInExArchive -UseBasicParsing
    }
    if ((File-Sha $BepInExArchive) -ne $PinnedBepInExSha256) { throw 'BepInEx zip SHA256不匹配，拒绝安装；不会接受新摘要或自动删除已有文件。' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($BepInExArchive)
    $packageHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) {
        if (!$entry.Name) { continue }
        $target = Safe-Child $GameDirectory $entry.FullName
        $packageHash = Stream-Sha ($entry.Open())
        $packageHashes.Add($entry.FullName, $packageHash)
        if ($entry.FullName -eq 'doorstop_config.ini') { continue }
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            if ((File-Sha $target) -ne $packageHash) { throw "已有BepInEx文件与固定x86包不一致；保留并拒绝覆盖：$($entry.FullName)" }
        } elseif ($ValidateOnly) { throw "缺少BepInEx文件：$($entry.FullName)" }
    }
    foreach ($folder in @('BepInEx/plugins', 'BepInEx/patchers')) {
        $path = Safe-Child $GameDirectory $folder
        if (Test-Path -LiteralPath $path -PathType Container) {
            $allowedModule = if ($folder -eq 'BepInEx/plugins') { 'ThothTAS.Runtime.dll' } else { 'ThothTAS.Preloader.dll' }
            $allowedModulePath = [IO.Path]::GetFullPath((Join-Path $path $allowedModule))
            $extra = @(Get-ChildItem -LiteralPath $path -Recurse -File -Filter '*.dll' | Where-Object { $_.FullName -ne $allowedModulePath })
            if ($extra.Count -gt 0) { throw ('副本中存在额外插件/补丁，不能确认固定TAS环境；保留这些文件并拒绝安装：' + (($extra | ForEach-Object { $_.FullName }) -join ', ')) }
        }
    }

    if (!$ValidateOnly) {
        [IO.Directory]::CreateDirectory($GameDirectory) | Out-Null
        $instanceLock = [IO.FileStream]::new((Join-Path $GameDirectory '.thoth-tas.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        foreach ($item in Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File -Force) {
            Assert-NoReparseAncestors $item.FullName
            $relative = $item.FullName.Substring($SourceDirectory.Length + 1)
            if ($relative -eq 'THOTH.sav' -or $relative -eq 'steam_autocloud.vdf') { continue }
            $destination = Safe-Child $GameDirectory $relative
            if (Test-Path -LiteralPath $destination -PathType Leaf) { $preservedFiles++; continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $item.FullName -Destination $destination
            $copiedFiles++
        }
        foreach ($entry in $archive.Entries) {
            if (!$entry.Name) { continue }
            $destination = Safe-Child $GameDirectory $entry.FullName
            if (Test-Path -LiteralPath $destination -PathType Leaf) { continue }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            $inputStream = $entry.Open(); $outputStream = [IO.File]::Create($destination)
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
            $copiedFiles++
        }
        foreach ($module in $modules) {
            $destination = Safe-Child $GameDirectory $module.relative
            if ((Test-Path -LiteralPath $destination -PathType Leaf) -and (File-Sha $destination) -eq $module.hash) { continue }
            Backup-ManagedFile $destination
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Write-ManagedBytesAtomic $destination ([IO.File]::ReadAllBytes($module.source))
            $copiedFiles++
        }
        $bepConfig = Safe-Child $GameDirectory 'BepInEx/config/BepInEx.cfg'
        Set-RequiredIni $bepConfig 'Preloader.Entrypoint' 'Assembly' 'UnityEngine.dll'
        Set-RequiredIni $bepConfig 'Preloader.Entrypoint' 'Type' 'MonoBehaviour'
        Set-RequiredIni $bepConfig 'Preloader.Entrypoint' 'Method' '.cctor'
        $doorstopConfig = Safe-Child $GameDirectory 'doorstop_config.ini'
        Set-RequiredIni $doorstopConfig 'General' 'enabled' 'true'
        Set-RequiredIni $doorstopConfig 'General' 'target_assembly' 'BepInEx\core\BepInEx.Preloader.dll'
        $gameSave = Safe-Child $GameDirectory 'THOTH.sav'
        if (!(Test-Path -LiteralPath $gameSave -PathType Leaf) -or (File-Sha $gameSave) -ne $baselineHash) {
            Backup-ManagedFile $gameSave
            Write-ManagedBytesAtomic $gameSave ([IO.File]::ReadAllBytes($Baseline))
        }
    }

    foreach ($entry in $manifest.GetEnumerator()) {
        if ((File-Sha (Safe-Child $GameDirectory $entry.Key)) -ne $entry.Value) { throw "复制后固定指纹不匹配：$($entry.Key)" }
    }
    foreach ($entry in $packageHashes.GetEnumerator()) {
        if ($entry.Key -eq 'doorstop_config.ini') { continue }
        if ((File-Sha (Safe-Child $GameDirectory $entry.Key)) -ne $entry.Value) { throw "BepInEx包内容不匹配：$($entry.Key)" }
    }
    Verify-RequiredConfiguration
    foreach ($module in $modules) {
        if ((File-Sha (Safe-Child $GameDirectory $module.relative)) -ne $module.hash) { throw "副本模块校验失败：$($module.relative)" }
    }
    $currentSave = Safe-Child $GameDirectory 'THOTH.sav'
    $currentSaveExists = Test-Path -LiteralPath $currentSave -PathType Leaf
    $currentSaveHash = if ($currentSaveExists) { File-Sha $currentSave } else { $null }
    [pscustomobject]@{
        passed = $true
        mode = $(if ($ValidateOnly) { 'read-only-validation' } else { 'create-or-repair-missing-files' })
        projectRoot = $ProjectRoot
        sourceReadOnly = $SourceDirectory
        gameCopy = $GameDirectory
        fixedManifestFiles = $manifest.Count
        fixedManifestSha256 = (File-Sha $Fingerprint)
        bepInExVersion = '5.4.23.5 x86'
        bepInExZipSha256 = $PinnedBepInExSha256
        bepInExPackageFiles = $packageHashes.Count
        baselineSha256 = $baselineHash
        baselineExpectedSha256 = $PinnedBaselineSha256
        currentSaveExists = $currentSaveExists
        currentSaveSha256 = $currentSaveHash
        currentSaveMatchesBaseline = ($currentSaveHash -eq $baselineHash)
        managedReplacement = '同目录独立临时文件，然后File.Replace已有目录项或File.Move新目录项；不会沿既存硬链接改写另一个名称的内容。'
        copiedFiles = $copiedFiles
        preservedExistingFiles = $preservedFiles
        managedBackups = $backupFiles
        backupDirectory = $(if ($backupFiles -gt 0) { $BackupDirectory } else { $null })
        startedGameProcesses = 0
        changedSteamState = $false
        limitation = '此校验只证明文件与必要配置符合固定清单；实际回放、游戏内行为和确定性以真实验收日志为准。'
    }
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $instanceLock) { $instanceLock.Dispose() }
}
