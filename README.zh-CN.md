# THOTH TAS

[English](README.md) · 简体中文

Windows 上的 THOTH TAS 编辑器、控制程序和游戏模块。支持文本脚本、模拟移动/射击、固定世界坐标瞄准、暂停逐帧、重复验证、自然转场与多关拼接。

## 使用

1. 下载 [Windows 可运行发布包](https://github.com/cyj-hashtag/ThothTAS/releases/latest)，解压其中的 `ThothTAS` 文件夹。
2. 在该文件夹运行 `scripts/Create-TasCopy.ps1`。它只读本机已有的 THOTH 安装，检查固定版本，创建独立 `game` 副本，并下载固定的 BepInEx x86 包。游戏位于其他目录时使用 `-SourceDirectory` 参数。
3. 启动 `dist/Launch-Editor.cmd`，打开 `.tas`，校验后播放。

发布包自带 .NET 10 运行时。游戏 EXE、Unity 游戏程序集和资源不在公开包内，需要本机已有游戏。改动和存档只写入独立副本。从编辑器或控制程序启动；TAS 会话在内存中停用 Steam 初始化、回调和成就。

## 编辑器

- 高亮输入行旁显示蓝色 **`--- 34`**，表示当前输入段已执行 34 帧。该标记只用于显示，不写入文本、复制内容或保存文件。
- “拼接脚本 / Compose TAS…”打开小窗口，支持添加、移除、上移、下移，生成新草稿后另存为。
- “语言 / Language”可即时切换简体中文和 English，本机记住选择。
- 播放使用不可变快照；播放中编辑和保存只更新草稿，“重新回放”应用新稿。
- “同进程快速重置”保留游戏进程，便于练习；精确重复验证仍使用独立冷进程。

详见 [中文操作说明](docs/editing-guide-zh.md)、[English guide](docs/editing-guide-en.md)。

## 输入与拼接

```text
Version,1
Start,Basic-1
Seed,1

60, M=45, A=270
60, M=45, A=(30,40)
30, N
60, M=0.5:0, A=1:0
```

角度为右 0°、上 90°、左 180°、下 270°。`M=x:y` 是移动摇杆，`A=x:y` 是射击摇杆，`A=(x,y)` 是世界坐标目标，`N` 不移动。每个输入周期为 1/60 秒，保留原物理步长 0.01 秒、死区和射击节奏。语法错误定位到行列。

拼接源文件须 Seed 一致、普通关卡自然相邻。`Room` 章节在本关玩家激活后从第 1 帧执行；过关跳过未用尾部输入，转场期间零输入。整次回放只播种一次，后一关继承前关随机消耗、时间和入场状态。各关独立成功不能保证连段成功，应复核完整连段并按连续入场状态修改后关。

```powershell
.\dist\host\ThothTAS.Host.exe compose --out .\work\combined.tas .\examples\basic-1.tas .\examples\basic-2.tas
.\dist\host\ThothTAS.Host.exe verify .\work\combined.tas --count 2 --modes --out .\work\verification\combined
```

提供的 `examples/composed/basic-1-basic-2.tas` 名义预算 799 帧，实际在第 495 帧完成两关。两次独立回放及暂停、逐帧、速度验证曾逐帧一致；本次界面更新又验证了两次连续回放。

## 源码与验证

构建需要 Windows 和 .NET 10 SDK。源码克隆后，先从发布包取得 `dist` 目录，运行 Create-TasCopy 创建自己的游戏副本和 Unity/BepInEx 编译引用，再运行：

```powershell
.\scripts\Build.ps1
dotnet run --project .\tests\ThothTAS.Core.Tests -c Release
.\scripts\Verify-All.ps1 -OutputDirectory .\work\acceptance\manual
```

纯解析测试无需游戏，不能代替实际回放。公开仓库包含 [实际验证摘要](docs/public/verification-20261013.md)、[逆向摘要](docs/public/reverse-engineering.md)和 [158 处随机调用清单](docs/public/random-calls.json)。完整本机原始日志和原游戏 CIL 保留在本地，可用 `ThothTAS.Inspect` 从自己的游戏重新提取证据。

## 范围

固定 `THOTH-2019-09-10-174628-r239`、Unity 5.3.6p5、32 位 Mono，单人普通固定关卡。未提供即时存档、倒放、寻址跳帧、双人、Hardcore、程序生成模式或跨电脑一致性保证。同进程快速重置不等价于冷启动。TAS 副本同步部分声音加载，并按输入帧归一化 LaserBeat/RhythmLaser 玩法时钟；不声明与普通实时原版逐瞬间相同。尚未执行全部随机调用点和玩法路径。

[第三方组件说明](THIRD-PARTY-NOTICES.md)
