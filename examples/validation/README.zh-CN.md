# 实际回放验证脚本

这些文件是独立进程验证的输入快照。文件存在或编译/解析通过不代表对应玩法已执行；当前状态以 `docs/verification/coverage.json` 中引用的真实验证报告为准。下面的“用途”是脚本设计目的，不是通过声明。

| 脚本 | 起点、帧数 | 用途与证据要求 |
|---|---|---|
| `inputs.tas` | Basic-1，18 帧 | 移动/射击角度、方向简写、小数向量、负坐标与移动中固定点瞄准；检查实际 Input 与玩家坐标字段。 |
| `basic-death.tas` | Basic-1，600 帧 | 不移动并朝右射击；必须观察 `Player.isDead` 从 False 到 True，不能仅用画面或文件名证明死亡。 |
| `basic-transition.tas` | Basic-1，600 帧 | 调整射击方向并向上移动，观察 Room 完成后自然进入 Basic-2；同场景死亡重载不计自然转场。 |
| `balls-collision.tas` | Balls-1，420 帧 | 移动、射击和碰撞；以真实 OnCollision/OnTrigger 阶段佐证，是否造成死亡另看字段。 |
| `teleport.tas` | Teleporters-1，240 帧 | 当前传送代表脚本。先打 `(26,9)`，再向上移动并打 `(16,-4)`；以回调前后 XY 变化和 player1Flag=True 为明确跳跃证据。 |
| `boss-big.tas` | Final-4，420 帧 | 瞄准及 BigBoss 路径；只有真实 Boss 实例字段变化才计观察覆盖，不将 BossMusic 或静态资源存在视为 Boss 战已执行/击败。 |
| `zapper-clock.tas` | zapper-1，360 帧 | LaserBeat/RhythmLaser 的音频时钟、激光开关、碰撞和死亡路径；比较完整字段及随机记录。 |
| `teleport-trial.tas` | Teleporters-1，720 帧 | 早期探索输入，保留作调查材料；不替代当前 `teleport.tas` 的验收。 |

截至本次补充，`work/acceptance/zapper-clock-final/report.json` 为 Passed=True，含五次连续独立进程及暂停、逐帧、0.5×、2×共九次逐帧/完整随机记录比较。此通过只覆盖该脚本实际走过的路径，不代表全部 Zapper 关卡或全部 158 个随机点均通过。

`work/acceptance/editor-runtime-final-v5/editor-runtime-report.json` 为 Passed=True，连续基线和编辑器受控回放逐帧及完整随机记录一致；两个 600 帧 run 在帧 172 记录房间完成，帧 240 自然进入 Basic-2。它同时检查固定客户区 1280×720、带边框、不能缩放/最大化、焦点切换、不可变快照、暂停和逐帧。文件打开通过注入路径调用正式加载逻辑，未覆盖系统文件选择对话框。此 UI 验证不替代代表脚本的五次独立进程要求。

传送试验 `work/acceptance/teleport-trial-v2/20261012-080348-9f1807506966435881e1649136df8e65/runtime-events.txt` 第 12 行记录帧 168 的真实跳跃：`Enemies[0]/TeleporterSphere (1)[2]`，XY `(0,19.7499542)`→`(31.3282166,10.3175812)`，player1Flag=True。active 字段在返回后读取，原跳跃分支将其置为 False。第 13 行标志仍为 True、XY 未变，不计第二次跳跃。该单次试验已经证明路径发生；五次独立进程和模式比较尚须以之后的 verify 报告确认。

在项目根目录手动运行一个代表脚本的五次独立进程加模式验证：

```powershell
.\dist\host\ThothTAS.Host.exe verify .\examples\validation\teleport.tas --root D:\Workspace4All\ThothTAS --count 5 --modes --out .\work\acceptance\teleport-final
```

此命令会启动实际游戏副本。已有批量验证运行时，应等其结束后再执行，避免多个游戏并行干扰窗口和测量。每个新回放先校验游戏/资源指纹，恢复固定初始存档；运行目录保留不可变 `script.tas`、Host/进程元数据、帧、随机、阶段、控制命令和运行事件日志。

只读更新覆盖索引，无须启动游戏：

```powershell
python .\scripts\Summarize-Coverage.py D:\Workspace4All\ThothTAS
```

速度选项设置 30/60/120 完整周期每秒的节奏目标。完整状态记录、物理及渲染可能使实际运行更慢；模式比较通过只证明相同逻辑记录，不自动证明实时达到 2×。UI v5 的 Host 命令时间粗估 1×基线为 37.67 帧/秒，2×短片段为 32.93 帧/秒，后者仅 8 帧，不是稳定速度基准。详细依据与限制见 `docs/reverse-engineering.md`。
