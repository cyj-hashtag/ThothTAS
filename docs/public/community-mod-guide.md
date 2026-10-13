# 社区 Speedrun Mod：计时器与窗口

此适配固定为作者 [rmadlal 的 v2.8 发布件](https://github.com/rmadlal/thoth-mod/releases/tag/v2.8)。它原本通过替换游戏的核心程序集安装。THOTH TAS 将其保存在项目 `work/community-mod/v2.8`，校验 SHA256 后仅在 TAS 进程内替换程序集；游戏磁盘原件及 Steam 安装保持原样。首次准备：

```powershell
.\scripts\Prepare-CommunityMod.ps1
```

本机已准备该文件。打开更新后的编辑器，勾选“社区计时器 / Community timers”，然后播放或重新回放。启动前在“启动尺寸 / Window size”设定分辨率，默认有边框的 1280×720。窗口可移动，运行中尺寸固定；切到编辑器后游戏继续运行。修改社区选项在下一次重新回放生效，自动开启新进程；同一进程不能切换核心程序集。

默认显示全部四个计时值：左上去加载总时间与总时间，右上累计游戏时间与上一关时间。沿用社区 Mod 的开始、停止及过关边界，计时源改为 **每个完整原生周期 1/60 秒**。转场的零输入周期也进入仍运行的总时间；暂停没有周期推进，变速只改变实际等待时间。所有计时状态纳入逐帧比较。游戏 HUD 使用正常渲染，暂停画面可能显示上一次呈现的计时值；精确结果以帧日志为准。

这是 TAS 帧时钟，不能将其视为普通墙钟 RTA 或排行榜有效时间。左上两个值在转场前可能相同；去加载计时器按作者原有停止边界排除转场。还修正了原显示格式在小于100ms时的百分秒计算。TAS 期间物理键鼠输入被接管，T 显示热键及跳关、强制过关等练习作弊键不启用；本次只适配计时器与窗口。

原 Mod 启动代码会重写旧的10行 `settings.txt`。适配层保留现有的 VSync、抗锯齿、Cheap lava 选项，窗口参数由编辑器提供，避免改写文件或突然进入全屏。原版与社区模式是不同的运行配置；两种配置应各自重新验证完整 TAS，不能用原版通过报告代替社区报告。

```powershell
# 默认拼接当前 Basic-1 / Basic-2，五次冷启动加暂停、逐帧和两种速度
.\scripts\Verify-CommunityMod.ps1
# 自选脚本，并附加五次实际同进程重置测量
.\scripts\Verify-CommunityMod.ps1 -Script .\examples\basic-1.tas -Reset
# 编辑器实际按钮、焦点、编辑另存及配置切换自测；测试只另存到 work
.\dist\editor\ThothTAS.Editor.exe --self-test-community .\examples\basic-1.tas .\examples\basic-2.tas --out .\work\community-editor-check
```

v0.3.2 原版规则已停用编辑器和普通Host的同进程重置：Basic-3日志显示原生物理状态残留会造成撞墙反弹及随机记录分歧。现在“重新回放”恢复固定存档并启动独立进程。此前约0.55秒的测量属于非确定性练习路径，不能用于可靠写TAS。`verify-reset`仍可显式运行诊断。[草稿保护与反弹失步修复报告](draft-replay-fix-20261013.md)。

仅验证当前游戏版本及报告中的路径；不包含社区全部作弊功能、所有关卡、其他 Mod 版本或跨电脑一致性。

## v0.4.0 修改规则

勾选“确定性规则（修改游戏）”后，同一社区配置支持同进程快速重新回放。它固定随机结果，改用60Hz物理、稳定Update顺序及逻辑游戏/音频时钟；Gameplay与原版不同。社区计时器仍按完整周期显示。原规则继续使用独立进程。切换规则或社区配置需要新进程。[规则和实际测量](deterministic-rules-20261013.md)。

```powershell
.\scripts\Verify-Deterministic.ps1 -ScriptPath .\examples\basic-1.tas -Count 5 -CommunityMod -Modes
```
