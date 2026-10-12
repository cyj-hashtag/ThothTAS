param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputDirectory = '',
    [switch]$Resume,
    [switch]$CheckAllStarts,
    [switch]$AllModes,
    [ValidateRange(2,100)][int]$RepresentativeCount = 2
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
if (!$OutputDirectory) {
    if ($Resume) { throw 'Resume 必须明确给出同一次统一验证的 OutputDirectory。' }
    $OutputDirectory = Join-Path $ProjectRoot ('work/acceptance/all-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$utf8 = [Text.UTF8Encoding]::new($false)
$hostExe = Join-Path $ProjectRoot 'dist/host/ThothTAS.Host.exe'
$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
$powershellExe = Join-Path $PSHOME 'powershell.exe'
if (!(Test-Path -LiteralPath $powershellExe)) { $powershellExe = (Get-Command pwsh -ErrorAction Stop).Source }

function Get-VerificationIdentity {
    $names = @('docs/game-fingerprint.json', 'docs/levels.txt', 'docs/baseline/THOTH.sav',
        'game/BepInEx/plugins/ThothTAS.Runtime.dll', 'game/BepInEx/patchers/ThothTAS.Preloader.dll',
        'game/BepInEx/config/BepInEx.cfg', 'game/doorstop_config.ini', 'game/winhttp.dll',
        'dist/host/ThothTAS.Host.exe', 'scripts/Verify-LevelStarts.ps1', 'scripts/Verify-Representatives.ps1', 'scripts/Verify-All.ps1')
    foreach ($directory in @('src/ThothTAS.Host', 'src/ThothTAS.Core', 'tests/ThothTAS.Acceptance', 'examples/validation')) {
        $names += Get-ChildItem -LiteralPath (Join-Path $ProjectRoot $directory) -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs', '.csproj', '.tas') } |
            ForEach-Object { $_.FullName.Substring($ProjectRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/') }
    }
    $files = @($names | Sort-Object -Unique | ForEach-Object {
        $path = Join-Path $ProjectRoot $_
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw ('缺少统一验证所需文件：' + $path) }
        [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    })
    $json = ConvertTo-Json -InputObject ([ordered]@{ checkAllStarts = [bool]$CheckAllStarts; allModes = [bool]$AllModes; representativeCount = $RepresentativeCount; files = $files }) -Depth 5 -Compress
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($hash.ComputeHash($utf8.GetBytes($json))).Replace('-', '') }
    finally { $hash.Dispose() }
    return [ordered]@{ version = 1; sha256 = $digest; checkAllStarts = [bool]$CheckAllStarts; allModes = [bool]$AllModes; representativeCount = $RepresentativeCount; files = $files }
}

# Arguments are data passed to a process, never shell command text.
function Quote-ProcessArgument([string]$Value) {
    if ($Value.Contains('"') -or $Value.Contains("`r") -or $Value.Contains("`n")) { throw '参数不能包含引号或换行。' }
    return '"' + $Value.TrimEnd('\') + ('\' * ($Value.Length - $Value.TrimEnd('\').Length) * 2) + '"'
}

$identity = Get-VerificationIdentity
$identityPath = Join-Path $OutputDirectory 'identity.json'
if (Test-Path -LiteralPath $identityPath) {
    if (!$Resume) { throw '输出目录已有统一验证身份。请使用新的目录，或明确指定 Resume。' }
    $prior = Get-Content -Raw -LiteralPath $identityPath | ConvertFrom-Json
    if ($prior.sha256 -cne $identity.sha256) { throw '模块、游戏清单、固定存档、源码或示例脚本已变化；禁止复用旧通过报告，请指定新的输出目录。' }
} elseif ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count) {
    throw '输出目录存在材料但没有 identity.json；无法证明旧报告属于当前模块，请指定新的输出目录。'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
[IO.File]::WriteAllText($identityPath, ($identity | ConvertTo-Json -Depth 6), $utf8)
$stages = @()
$startedUtc = [DateTime]::UtcNow.ToString('o')

function Write-UnifiedReport([bool]$Passed, [string]$ErrorText = '') {
    $result = [ordered]@{
        version = 1; startedUtc = $startedUtc; updatedUtc = [DateTime]::UtcNow.ToString('o'); passed = $Passed
        identitySha256 = $identity.sha256; resume = [bool]$Resume; projectRoot = $ProjectRoot; error = $ErrorText
        scope = '当前电脑、当前模块和游戏清单；代表性重复回放与实际专项，全部关卡起点仅 CheckAllStarts 时执行。编辑器焦点/草稿操作另行自测。'
        stages = $stages
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'summary.json'), ($result | ConvertTo-Json -Depth 8), $utf8)
    $md = @('# THOTH TAS 统一验证', '', ('结果：' + $(if ($Passed) { '通过' } elseif ($ErrorText) { '失败' } else { '执行中' })), '',
        ('身份 SHA256：`' + $identity.sha256 + '`'), '', '|阶段|状态|退出码|报告|', '|---|---|---|---|')
    foreach ($stage in $stages) { $md += ('|' + $stage.name + '|' + $stage.state + '|' + $stage.exitCode + '|' + $stage.report + '|') }
    if ($ErrorText) { $md += @('', ('错误：' + $ErrorText)) }
    $md += @('', '关卡/代表性阶段 Resume 仅复用本次身份一致的通过报告；actual-all 每次重新执行。每轮真实日志由原入口记录。',
        '此入口未执行编辑器焦点、草稿或同进程重置自测。未执行路径和跨电脑一致性不由本报告证明。')
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory 'summary.md'), $md, $utf8)
}

function Invoke-VerificationStage([string]$Name, [string]$Exe, [string[]]$Arguments, [string]$ReportPath, [string]$ReportKind) {
    if ((Get-VerificationIdentity).sha256 -cne $identity.sha256) { throw '长时间验证期间文件身份已变化，停止后续阶段。请使用新输出目录重跑。' }
    $stage = [ordered]@{ name = $Name; state = '执行中'; startedUtc = [DateTime]::UtcNow.ToString('o'); finishedUtc = $null; exitCode = $null; report = $ReportPath; stdout = ''; stderr = ''; executable = $Exe; arguments = $Arguments }
    $script:stages += $stage
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $stage.stdout = Join-Path $OutputDirectory ($Name + '-' + $stamp + '.stdout.txt')
    $stage.stderr = Join-Path $OutputDirectory ($Name + '-' + $stamp + '.stderr.txt')
    Write-UnifiedReport $false
    Write-Output ('开始 ' + $Name + '；进度日志 ' + $stage.stdout)
    $quotedArguments = ($Arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join ' '
    try {
        $process = Start-Process -FilePath $Exe -ArgumentList $quotedArguments -WorkingDirectory $ProjectRoot -WindowStyle Hidden -RedirectStandardOutput $stage.stdout -RedirectStandardError $stage.stderr -PassThru
        # Start-Process -Wait waits for the whole descendant job on Windows.
        # SDK build servers can outlive dotnet run after every actual check has
        # ended. Wait for the owned entry process and inspect its real exit code.
        $process.WaitForExit()
        $process.Refresh()
        $stage.exitCode = $process.ExitCode
        if ($stage.exitCode -ne 0) { throw ($Name + ' 未通过，退出码 ' + $stage.exitCode + '，查看 ' + $stage.stderr) }
        if ($ReportKind) {
            if (!(Test-Path -LiteralPath $ReportPath)) { throw ($Name + ' 缺少报告，不能计为通过。') }
            $r = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
            if ($ReportKind -eq 'summary') {
                if ($r.total -lt 1 -or $r.finished -ne $r.total -or $r.passed -ne $r.total) { throw ($Name + ' 报告仍有失败或未完成项目。') }
            } elseif (!$r.passed -or $r.cases.Count -ne 4) { throw ($Name + ' 实际专项报告未完整通过四项。') }
        }
        $stage.state = '通过'
    } catch { $stage.state = '失败'; throw }
    finally { $stage.finishedUtc = [DateTime]::UtcNow.ToString('o'); Write-UnifiedReport $false }
}

try {
    Invoke-VerificationStage 'check-game' $hostExe @('check-game', '--root', $ProjectRoot) '' ''
    if ($CheckAllStarts) {
        $levelsOutput = Join-Path $OutputDirectory 'levels'
        $levelsArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $ProjectRoot 'scripts/Verify-LevelStarts.ps1'), '-ProjectRoot', $ProjectRoot, '-OutputDirectory', $levelsOutput)
        if ($Resume) { $levelsArgs += '-Resume' }
        Invoke-VerificationStage 'levels' $powershellExe $levelsArgs (Join-Path $levelsOutput 'summary.json') 'summary'
    }
    $representativesOutput = Join-Path $OutputDirectory 'representatives'
    $representativeArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $ProjectRoot 'scripts/Verify-Representatives.ps1'), '-ProjectRoot', $ProjectRoot, '-OutputDirectory', $representativesOutput, '-Count', [string]$RepresentativeCount)
    if ($Resume) { $representativeArgs += '-Resume' }
    if ($AllModes) { $representativeArgs += '-AllModes' }
    Invoke-VerificationStage 'representatives' $powershellExe $representativeArgs (Join-Path $representativesOutput 'summary.json') 'summary'
    $actualOutput = Join-Path $OutputDirectory 'actual-all'
    Invoke-VerificationStage 'actual-all' $dotnetExe @('run', '--project', (Join-Path $ProjectRoot 'tests/ThothTAS.Acceptance/ThothTAS.Acceptance.csproj'), '-c', 'Release', '--', 'actual-all', '--root', $ProjectRoot, '--out', $actualOutput) (Join-Path $actualOutput 'report.json') 'actual'
    Write-UnifiedReport $true
    Write-Output ('全部统一验证通过；总报告 ' + (Join-Path $OutputDirectory 'summary.md'))
    exit 0
} catch {
    Write-UnifiedReport $false $_.Exception.Message
    Write-Error $_.Exception.Message -ErrorAction Continue
    exit 2
}
