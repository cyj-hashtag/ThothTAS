[CmdletBinding()]
param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$ReleaseName = 'ThothTAS',
    [ValidateSet('Draft','Final')][string]$Mode = 'Draft',
    [string]$FinalReport,
    [switch]$IncludeLogs
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$rootPrefix = $ProjectRoot + [IO.Path]::DirectorySeparatorChar
$utf8 = New-Object Text.UTF8Encoding($false)
$shareRead = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete

function Assert-ProjectPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ($absolute -ne $ProjectRoot -and -not $absolute.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)) {
        throw "路径超出项目目录：$absolute"
    }
    $current = $absolute
    while ($current -and $current.StartsWith($ProjectRoot,[StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "打包不跟随重解析点：$current" }
        }
        if ($current -eq $ProjectRoot) { break }
        $current = Split-Path -Parent $current
    }
    return $absolute
}

function Get-RelativePath([string]$Path) {
    $absolute = Assert-ProjectPath $Path
    return $absolute.Substring($rootPrefix.Length).Replace('\','/')
}

function Get-StreamHash([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Stream))).Replace('-','').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Get-SharedHash([string]$Path) {
    $stream = New-Object IO.FileStream($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,$shareRead)
    try { return Get-StreamHash $stream } finally { $stream.Dispose() }
}

function Write-NewText([string]$Path,[string]$Text) {
    $null = Assert-ProjectPath $Path
    $stream = New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $writer = $null
    try {
        $writer = New-Object IO.StreamWriter($stream,$utf8)
        $writer.Write($Text)
        $writer.Flush()
    } finally { if ($writer) { $writer.Dispose() }; $stream.Dispose() }
}

function Copy-StableFile([string]$Source,[string]$Destination) {
    $null = Assert-ProjectPath $Source
    $null = Assert-ProjectPath $Destination
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $before = Get-SharedHash $Source
        $temporary = $Destination + '.copy-' + [Guid]::NewGuid().ToString('N')
        $inputStream = $null
        $outputStream = $null
        try {
            $inputStream = New-Object IO.FileStream($Source,[IO.FileMode]::Open,[IO.FileAccess]::Read,$shareRead)
            $outputStream = New-Object IO.FileStream($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            $inputStream.CopyTo($outputStream)
            $outputStream.Flush()
        } finally {
            if ($outputStream) { $outputStream.Dispose() }
            if ($inputStream) { $inputStream.Dispose() }
        }
        $copyHash = Get-SharedHash $temporary
        $after = Get-SharedHash $Source
        if ($before -eq $copyHash -and $after -eq $copyHash) {
            [IO.File]::Move($temporary,$Destination)
            $original = Get-Item -LiteralPath $Source
            [IO.File]::SetLastWriteTimeUtc($Destination,$original.LastWriteTimeUtc)
            return [pscustomobject]@{ path=(Get-RelativePath $Source); sha256=$copyHash; bytes=(Get-Item -LiteralPath $Destination).Length; staged=$Destination }
        }
        Write-Warning "文件在复制期间变化，第 $attempt 次快照未纳入包：$Source"
    }
    throw "文件持续变化，未生成包含混合内容的交付包：$Source。请在相关编辑、构建或回放完成后重新打包。失败的中间快照保留在 work。"
}

function Get-MainFiles([string]$Directory) {
    $null = Assert-ProjectPath $Directory
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "打包目录含重解析点：$($item.FullName)" }
        if ($item.PSIsContainer) {
            if ($item.Name -in @('bin','obj','.git','work','game','THOTH_Data')) { continue }
            if ($Directory -eq (Join-Path $ProjectRoot 'dist') -and $item.Name -eq 'release') { continue }
            Get-MainFiles $item.FullName
        } else { $item.FullName }
    }
}

function Find-RunEvidence([string]$Directory,[Collections.Generic.HashSet[string]]$Files,[Collections.Generic.List[string]]$RunDirectories) {
    $null = Assert-ProjectPath $Directory
    $items = @(Get-ChildItem -LiteralPath $Directory -Force)
    $hasGameRecord = @($items | Where-Object { -not $_.PSIsContainer -and $_.Name -in @('frames.jsonl','random.tsv','unity.log') }).Count -gt 0
    if ($hasGameRecord) {
        $RunDirectories.Add((Get-RelativePath $Directory))
        foreach ($file in Get-MainFiles $Directory) { $null = $Files.Add($file) }
        return
    }
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        if ($item.PSIsContainer) {
            # Skip tools, copied games and this script's own snapshots before recursive discovery.
            if ($item.Name -in @('bin','obj','.git','tools','decompiled','host-next') -or
                $item.Name.StartsWith('unitypy-', [StringComparison]::OrdinalIgnoreCase) -or
                $item.Name.StartsWith('create-copy-', [StringComparison]::OrdinalIgnoreCase) -or
                $item.Name.StartsWith('core-chapters-net35-', [StringComparison]::OrdinalIgnoreCase) -or
                $item.Name.StartsWith('package-contract-', [StringComparison]::OrdinalIgnoreCase) -or
                $item.Name.StartsWith('package-release-', [StringComparison]::OrdinalIgnoreCase)) { continue }
            Find-RunEvidence $item.FullName $Files $RunDirectories
        } elseif ($item.Name -in @('report.json','report.md')) { $null = $Files.Add($item.FullName) }
    }
}

function Add-PackageMetadata([string]$Stage,[string]$Kind,[Collections.Generic.List[object]]$Records,[object]$Details) {
    $manifest = [ordered]@{
        schemaVersion=1; packageKind=$Kind; releaseMode=$Mode; createdAt=(Get-Date -Format o); originalProjectRoot=$ProjectRoot
        gameIncluded=$false; gameLaunchedByPackaging=$false; packagingCertifiesReplay=$false
        finalReport=$script:finalReportRelative; details=$Details
        files=@($Records | ForEach-Object { [ordered]@{path=$_.path;sha256=$_.sha256;bytes=$_.bytes} })
    }
    $manifestPath = Join-Path $Stage 'PACKAGE.json'
    Write-NewText $manifestPath ($manifest | ConvertTo-Json -Depth 10)
    $lines = New-Object Collections.Generic.List[string]
    $lines.Add("SHA256`tBytes`tPath")
    foreach ($record in $Records) { $lines.Add($record.sha256 + "`t" + $record.bytes + "`t" + $record.path) }
    $listPath = Join-Path $Stage 'PACKAGE-FILES.tsv'
    Write-NewText $listPath ([string]::Join("`n",$lines.ToArray()) + "`n")
    foreach ($file in @($manifestPath,$listPath)) {
        $Records.Add([pscustomobject]@{path=[IO.Path]::GetFileName($file);sha256=(Get-SharedHash $file);bytes=(Get-Item -LiteralPath $file).Length;staged=$file})
    }
}

function Write-CheckedZip([string]$Path,[string]$ArchiveRoot,[Collections.Generic.List[object]]$Records) {
    $expected = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
    $fileStream = New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $archive = $null
    try {
        $archive = New-Object IO.Compression.ZipArchive($fileStream,[IO.Compression.ZipArchiveMode]::Create,$true)
        foreach ($record in $Records) {
            $name = $ArchiveRoot + '/' + $record.path
            $expected.Add($name,$record.sha256)
            $entry = $archive.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            $sourceStream = $null
            try {
                $sourceStream = New-Object IO.FileStream($record.staged,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                $sourceStream.CopyTo($entryStream)
            } finally { if ($sourceStream) { $sourceStream.Dispose() }; $entryStream.Dispose() }
        }
    } finally { if ($archive) { $archive.Dispose() }; $fileStream.Dispose() }

    $fileStream = New-Object IO.FileStream($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $archive = $null
    try {
        $archive = New-Object IO.Compression.ZipArchive($fileStream,[IO.Compression.ZipArchiveMode]::Read,$true)
        if ($archive.Entries.Count -ne $expected.Count) { throw 'ZIP 条目数量不符合交付清单。' }
        foreach ($entry in $archive.Entries) {
            if (-not $expected.ContainsKey($entry.FullName)) { throw "ZIP 包含清单外条目：$($entry.FullName)" }
            $entryStream = $entry.Open()
            try { $hash = Get-StreamHash $entryStream } finally { $entryStream.Dispose() }
            if ($hash -ne $expected[$entry.FullName]) { throw "ZIP 内容校验失败：$($entry.FullName)" }
        }
    } finally { if ($archive) { $archive.Dispose() }; $fileStream.Dispose() }
    $zipHash = Get-SharedHash $Path
    Write-NewText ($Path + '.sha256') ($zipHash + '  ' + [IO.Path]::GetFileName($Path) + "`n")
    return [pscustomobject]@{path=(Get-RelativePath $Path);sha256=$zipHash;bytes=(Get-Item -LiteralPath $Path).Length;files=$Records.Count;entriesVerified=$true}
}

$null = Assert-ProjectPath $ProjectRoot
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "项目目录不存在：$ProjectRoot" }
$required = @('README.md','src','tests','scripts','docs','examples','dist/module/ThothTAS.Runtime.dll','dist/module/ThothTAS.Preloader.dll','dist/host/ThothTAS.Host.exe','dist/editor/ThothTAS.Editor.exe','docs/game-fingerprint.tsv','docs/baseline/THOTH.sav')
foreach ($relative in $required) {
    $path = Assert-ProjectPath (Join-Path $ProjectRoot $relative)
    if (-not (Test-Path -LiteralPath $path)) { throw "交付必需路径不存在：$relative" }
}
$script:finalReportRelative = $null
if ($FinalReport) {
    $reportPath = if ([IO.Path]::IsPathRooted($FinalReport)) { $FinalReport } else { Join-Path $ProjectRoot $FinalReport }
    $reportPath = Assert-ProjectPath $reportPath
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf) -or (Get-Item -LiteralPath $reportPath).Length -eq 0) { throw '指定的最终报告不存在或为空。' }
    $script:finalReportRelative = Get-RelativePath $reportPath
    if (-not $script:finalReportRelative.StartsWith('docs/',[StringComparison]::Ordinal)) { throw '最终报告必须放在 docs 中，确保主包包含该报告。' }
}
if ($Mode -eq 'Final' -and -not $FinalReport) { throw 'Final 模式必须指定 -FinalReport，打包本身不会判断真实回放是否通过。' }

$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$workRoot = Assert-ProjectPath (Join-Path $ProjectRoot ('work/package-release-' + $stamp))
$releaseRoot = Assert-ProjectPath (Join-Path $ProjectRoot 'dist/release')
if (-not (Test-Path -LiteralPath $releaseRoot)) { New-Item -ItemType Directory -Path $releaseRoot | Out-Null }
$mainStage = Join-Path $workRoot 'main'
New-Item -ItemType Directory -Path $mainStage | Out-Null
$mainFiles = New-Object Collections.Generic.List[string]
foreach ($directory in @('src','tests','scripts','docs','examples','dist')) {
    foreach ($file in Get-MainFiles (Join-Path $ProjectRoot $directory)) { $mainFiles.Add($file) }
}
foreach ($name in @('README.md','README.zh-CN.md','AGENTS.md','ThothTAS.sln','ThothTAS.slnx','Directory.Build.props','Directory.Build.targets','NuGet.Config','global.json','.gitignore','.editorconfig')) {
    $path = Join-Path $ProjectRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { $mainFiles.Add($path) }
}
$mainRecords = New-Object 'Collections.Generic.List[object]'
Write-Host "正在制作 $Mode 主包快照；不读取或启动游戏进程。"
foreach ($file in ($mainFiles | Sort-Object)) {
    $relative = Get-RelativePath $file
    $mainRecords.Add((Copy-StableFile $file (Join-Path $mainStage $relative)))
}
Add-PackageMetadata $mainStage 'software-source-docs-examples' $mainRecords ([ordered]@{
    excluded=@('game','THOTH_Data','work','bin','obj','.git','dist/release'); rawLogsInSeparateOptionalArchive=$true
    restoreGameCommand='scripts/Create-TasCopy.ps1'; gameSource='D:/Play/Steam/steamapps/common/THOTH'
    finalReportMeaning='指定报告已纳入包；打包不判定验收通过，请阅读该报告的已验证路径与限制。'
})
$mainZipPath = Join-Path $releaseRoot ($ReleaseName + '-' + $Mode.ToLowerInvariant() + '-' + $stamp + '.zip')
$mainResult = Write-CheckedZip $mainZipPath $ReleaseName $mainRecords
$logsResult = $null
$runDirectories = New-Object Collections.Generic.List[string]
if ($IncludeLogs) {
    $evidenceFiles = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    Find-RunEvidence (Join-Path $ProjectRoot 'work') $evidenceFiles $runDirectories
    $gameLog = Join-Path $ProjectRoot 'game/BepInEx/LogOutput.log'
    if (Test-Path -LiteralPath $gameLog -PathType Leaf) { $null = $evidenceFiles.Add($gameLog) }
    if ($evidenceFiles.Count -eq 0) { throw '未发现实际运行记录，主包已保留，不生成空日志包。' }
    $logsStage = Join-Path $workRoot 'logs'
    New-Item -ItemType Directory -Path $logsStage | Out-Null
    $logRecords = New-Object 'Collections.Generic.List[object]'
    Write-Host "正在制作独立日志包快照，发现 $($runDirectories.Count) 个记录目录。"
    foreach ($file in ($evidenceFiles | Sort-Object)) {
        $relative = Get-RelativePath $file
        $logRecords.Add((Copy-StableFile $file (Join-Path $logsStage $relative)))
    }
    Add-PackageMetadata $logsStage 'validation-raw-records' $logRecords ([ordered]@{
        runDirectories=$runDirectories.ToArray(); discoveryMarkers=@('frames.jsonl','random.tsv','unity.log')
        includesFailedAndIncompleteAttempts=$true; selectedReportFilesIncluded=$true; latestGameLogIncluded=(Test-Path -LiteralPath $gameLog)
        warning='存在记录不代表通过；须阅读对应 report.json/report.md。当前活动回放的文件持续变化时，打包会失败并保留工作快照。'
    })
    $logsZipPath = Join-Path $releaseRoot ($ReleaseName + '-logs-' + $Mode.ToLowerInvariant() + '-' + $stamp + '.zip')
    $logsResult = Write-CheckedZip $logsZipPath ($ReleaseName + '-validation-logs') $logRecords
}
$summary = [ordered]@{
    schemaVersion=1; createdAt=(Get-Date -Format o); mode=$Mode; finalReport=$script:finalReportRelative
    packagingCertifiesReplay=$false; gameLaunchedByPackaging=$false; sourceAndGameUnmodified=$true
    stagingDirectory=(Get-RelativePath $workRoot); main=$mainResult; logs=$logsResult
}
$summaryPath = Join-Path $releaseRoot ($ReleaseName + '-' + $Mode.ToLowerInvariant() + '-' + $stamp + '-package.json')
Write-NewText $summaryPath ($summary | ConvertTo-Json -Depth 8)
Write-Host "主包已创建并逐条解压校验：$mainZipPath"
if ($logsResult) { Write-Host "独立日志包：$(Join-Path $ProjectRoot $logsResult.path)" }
Write-Output $summaryPath
