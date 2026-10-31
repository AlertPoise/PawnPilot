# PawnPilot M0 游戏桥接可行性验证

检查日期：2026-10-31（用户时区 America/New_York）。仓库保持外层工作区、内层 Git 根目录的双层布局；未提交、未 push、未发布。

## 结论与验收

**M0 整体：PASS，限本报告验证的最小桥接路径。** 正式版可正常加载 `network` 权限 Mod，外部 Python 与游戏双向通信，读取真实现金/阶段，并通过游戏原生 Resume 入口关闭菜单。前四项全部具有实机证据。稳定性为 **PARTIAL**：已验证关键保护与正常退出，未发现阻断这条路径的问题；新开局就绪、跨进程结果恢复等限制仍需 M1 解决。

| 核心能力 | 状态 | 实际验收依据 |
|---|---|---|
| Mod 加载 | PASS | v0.0.4 正常激活/更新扫描；日志 `running=True trust=Updated caps=network`、INIT/ENABLED/BRIDGE_READY；开发模式关闭 |
| 状态读取 | PASS | 正常载入独立测试槽，`loaded=true, phase=MORNING, cash=550`；界面现金显示 550，场景 InventoryScene |
| 本机通信 | PASS | 游戏 UnityWebRequest ↔ 独立 Python broker；真实 observe 命令与结果 ID 对应，HTTP 200，非夹具 |
| 合法动作 | PASS | `EscapeUIManager.OnResume()`；ID `88120c4f35fd65c4d55bce36fb7f41a9`；menu_open true→false，version :5→:6，现金 550→550，前后截图一致 |
| 稳定性 | PARTIAL | 32 个自动化检查通过；实机未载入拒绝、过期版本拒绝、断线重连、场景退出、游戏关闭清理均通过；新游戏必须正常重载，未测热卸载/长时间运行/所有异常组合 |

完整脱敏证据摘要在 [m0-evidence.json](m0-evidence.json)。原始日志、HTTP JSON、10 张截图在本地 `artifacts/live/`，由 Git 忽略，不将游戏原始文件或个人保存数据提交到仓库。

## 实际版本和来源

| 项目 | 核对结果 |
|---|---|
| Steam | 正式版 App 4348910，build 26337366，depot 4348911 manifest 1192219238240115953；由本机 appmanifest 核对 |
| 游戏 | 界面/PlayerStore.VERSION 为 `VERSION 100-Rev6`；Application.version 和 loader audit 为 `0.46D`；不能混为同一种版本号 |
| 引擎 | Unity 2021.3.45f2 (88f88f591b2e)，Mono 后端；Assembly-CSharp 引用 netstandard 2.1.0.0，CLR 元数据 v4.0.30319 |
| 构建工具 | Windows 10.0.22631；.NET SDK 10.0.401，Python 3.13.2；Mod TFM netstandard2.1，测试 TFM net10.0 |
| 官方 Mod 仓库 | commit `d0538eacf763675d8e74c982d80c2d915f721a25`，2026-10-30 安全更新；官方样例仍写 0.46，不据此推断本机版本 |
| STS2-Agent | commit `89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1`，2026-10-28，v0.17.0；net9.0 + GodotSharp，与本游戏运行环境不同 |

游戏程序集 SHA256：`6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5`。

最终 Probe DLL SHA256：`5F092344D3AFBEDFC6B843292835916179A8D001D54A2A8C809B7DE296FF135D`；游戏信任指纹（manifest+DLL 等）为 `89f618a3645f7530671e709da22d3f9272929e5988230fa4520709cc4d123aa7`，两者不是同一计算对象。

实时访问并交叉核查：[Steam 商店](https://store.steampowered.com/app/4348910/Probably_Stolen/)、[开发者公告](https://steamcommunity.com/app/4348910/allnews/)、[官方 APIChanges](https://github.com/dragonpan2/probably-stolen-modding/blob/d0538eacf763675d8e74c982d80c2d915f721a25/APIChanges.md)、[官方指南](https://github.com/dragonpan2/probably-stolen-modding/blob/d0538eacf763675d8e74c982d80c2d915f721a25/ModdingGuide.md)。部分公告 URL 网页抓取失败，未将搜索摘要替代本机版本证据。

## Mod API、权限和线程

正式入口为 `IMod.Init(ModManifest)/OnEnable()/OnDisable()`。根目录 manifest.xml 必须列出每个 DLL，并声明 Capabilities；本包只有 Probe DLL、manifest、独立配置，无脚本和游戏 DLL。游戏退出调用 OnDisable，探针还在 OnDestroy/OnApplicationQuit 幂等清理请求与事件。

官方支持 ModHook 和 Harmony；优先 Hook。本轮不引用 Harmony，不打补丁。扫描器版本为 2：System.Net、Socket、HttpListener 与 UnityWebRequest 属于 network 能力；任意 Process 启动、对信任/加载器的反射或补丁属于硬拒绝。普通文件 IO 需 files，官方受限 Mod 文件读取可免除此项。代码只用 ReadModFileText 读取自己的 session.json。

两种 HTTP 方向在文档/本机 IL 中均需 network。**只实测了 Mod 主动连接 Python**，未实现或证明游戏内 HttpListener 服务可运行。选择外部监听减少游戏内线程、listener 关闭及依赖负担；Unity 协程读取状态、接收命令、复查前置并执行，后台线程不触碰 Unity 对象。Python 固定绑定 127.0.0.1:18765，随机 Bearer token，拒绝浏览器 Origin/错误 Host，禁止重定向和任意方法执行。

状态成员：先检查 `PlayerStore.HasInstance`，避免 Instance getter 创建新对象；现金使用与 `StoreCreditDisplay.Update` 相同的 `GetCash()`；阶段只取公开 storeState；菜单只取 menuPanel 的活动状态。不导出客户秘密、完整 Unity 对象或保存内容。

动作前检查载入、场景、菜单开启、组件启用、状态版本和截止时间；调用原生 `OnResume`；执行后检查同一场景仍已载入且菜单关闭。IL 链为 OnResume→CloseEscapeMenu→SetActive(false)，另播放原生音效。探针没有直接写字段/SetActive。原候选 WildNetwork 被排除：Open 会改 daySinceLastBrowseUpgrade、补全升级数据，Close 可能推进教程/遣走顾客。

运行环境参考：[Unity .NET 支持](https://docs.unity3d.com/2021.3/Documentation/Manual/dotnetProfileSupport.html)、[UnityWebRequest](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest.html)、[.NET Standard](https://learn.microsoft.com/en-us/dotnet/standard/net-standard)、[Json.NET 设置](https://www.newtonsoft.com/json/help/html/SerializationSettings.htm)。Json.NET 仅引用游戏副本，TypeNameHandling.None、MetadataPropertyHandling.Ignore、MaxDepth=16。

## STS2-Agent 源码借鉴与边界

| 已读源码（上述固定 commit） | 借鉴与不照搬之处 |
|---|---|
| [server.py L98/L229/L562/L694](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/mcp_server/src/sts2_mcp/server.py#L98) | FastMCP 工具注册及 stdio；最新项目还提供原生 HTTP /mcp，Python 已是可选入口，M0 不提前实现完整 MCP |
| [GameStateService L94–251](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/STS2AIAgent/Game/GameStateService.cs#L94)、[decision.py L24](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/mcp_server/src/sts2_mcp/decision.py#L24) | 同次快照生成状态与合法动作；其 state_version=16 是 schema 常量，不是变更计数。PawnPilot 使用会话UUID+revision |
| [GameActionService L81](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/STS2AIAgent/Game/GameActionService.cs#L81)、[ActionExecutionGate L49](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/STS2AIAgent/Game/ActionExecutionGate.cs#L49) | 单写入口、busy拒绝、finally释放。PawnPilot broker单命令+Unity单协程；没有照搬战斗/联机逻辑 |
| [GameThread L165](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/STS2AIAgent/Game/GameThread.cs#L165) | 区分排队/开始/放弃，不能让等待超时意味着动作取消；Unity使用自己的主线程协程，不照搬 Godot context |
| [client.py L419](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/mcp_server/src/sts2_mcp/client.py#L419)、[Skill L52](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/skills/sts2-mcp-player/SKILL.md#L52) | 动作不自动重试，unknown保持不确定；一次一个动作，依据当前状态。STS2 Python act 没有统一 expected_revision，不能声称其所有入口已有旧状态保护 |
| [HttpServer L69](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/STS2AIAgent/Server/HttpServer.cs#L69) | loopback和关闭清理的设计；其 HttpListener 不证明 Unity 兼容性 |

STS2 根许可证为 [AGPL v3](https://github.com/CharTyr/STS2-Agent/blob/89d5fd3d56c25d7d87a09dfbaa283485a6c3e1b1/LICENSE)；本轮只参考设计、独立实现，没有复制其工程代码。官方示例仓库未找到 LICENSE，同样未复制完整示例或其 DLL。

## 执行命令与证据层级

环境与本机定位实际使用 `git status --branch --short`、`git remote -v`、`Get-Command dotnet,python,py,git,rg`、`dotnet --info`、`python --version`；只检查 Steam 注册表、libraryfolders.vdf、三个已配置 steamapps 的 appmanifest，以及命中的游戏/保存目录，无无边界扫描。

本机 IL 通过游戏已有 Cecil 只读提取，没有执行游戏程序集：

```powershell
$managed = 'E:\SteamLibrary\steamapps\common\Probably Stolen\Probably Stolen_Data\Managed'
[void][System.Reflection.Assembly]::LoadFrom((Join-Path $managed 'Unity.Burst.Cecil.dll'))
$a = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Assembly-CSharp.dll'))
# 实际遍历指定 Type 的 Fields/Properties/Methods/Body.Instructions，输出 .research/local-api/*-il.txt
Get-FileHash -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll') -Algorithm SHA256
```

官方资料用 Git clone 固定 commit；STS2 clone 网络受阻，改用 GitHub connector 固定提交取实际文件。全部调查缓存在 .research，未作为工程依赖。

实际构建/测试命令（重复构建使用同一命令，最终 0 警告/0 错误）：

```powershell
Copy-Item local.props.example local.props
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
dotnet build src/PawnPilot.Probe/PawnPilot.Probe.csproj -c Release --nologo
dotnet restore src/PawnPilot.Probe/PawnPilot.Probe.csproj --disable-parallel --nologo
./scripts/build.ps1
dotnet run --project tests/PawnPilot.CoreTests/PawnPilot.CoreTests.csproj -c Release
python -m unittest discover -s tests -p test_bridge.py -v
python -c "from bridge.server import create_config; create_config('artifacts/session.json')"
python bridge/server.py --config artifacts/session.json
python bridge/client.py state
python bridge/client.py observe --expected-version '353cc2d4db204e97a457d009365d1fd6:1'
python bridge/client.py result --id 0c93206e23d2f20bfb4ace4bfa52bb88
python bridge/client.py close_menu --expected-version '353cc2d4db204e97a457d009365d1fd6:1'
python bridge/client.py result --id 94827c6138222e84a049e831950dfcdb
python bridge/client.py close_menu --expected-version '353cc2d4db204e97a457d009365d1fd6:5'
python bridge/client.py result --id 88120c4f35fd65c4d55bce36fb7f41a9
# 同样的 :5 在动作完成后再次提交，返回 HTTP 409 stale_state。
git diff --check
git status --short
```

退出后的端口释放检查实际执行 `python -c "import socket; s=socket.socket(); s.bind(('127.0.0.1',18765)); print('PASS port_18765_rebind_after_shutdown'); s.close()"`，成功。此前受限环境的 connect_ex 返回10035，未将它当作关闭成功证据；以主机环境重新绑定成功为依据。

版本号/请求ID仅为本次证据；复现时必须重新读取当前 state.version，不得重用旧值。安装/更新仅在用户明确批准后 Copy-Item 到 Mods/pawnpilot_m0；退出后逐文件删除该目录内预期的三个文件。没有使用官方会自动安装的 create-mod.bat。

## 测试数量、失败历史与实机检查

最终自动化：**32 通过，0 失败**。Python 19 项包含真实 localhost HTTP、独立 Python 子进程往返及正常关闭、鉴权/Origin/Host/大小/类型拒绝、并发写互斥、过期、未知结果、重复与冲突结果、心跳、会话变化、重启后旧结果拒绝、显式可选对象标志。C# 13 项覆盖未载入、旧版本、到期、白名单、菜单前置、后置验证、去重、只读观察、异常/未观察/场景离开不确定、重入拒绝和空请求。C# 测试是纯托管门控，不冒充 Unity 测试。

实机检查清单：**10 项通过，1 项已知限制未通过**：

| 检查 | 结果/证据 |
|---|---|
| 正常权限激活与加载 | 通过；activation.log、final-player.log、01/09 截图 |
| 主菜单 observe 往返 | 通过；main-menu-observe-result.json，success |
| 未载入写动作拒绝 | 通过；unloaded-close-result.json，not_loaded |
| 正常载入状态与 UI 一致 | 通过；reloaded-state.json、05-cash-550.png |
| 原生 Resume 动作前后验证 | 通过；close-result.json、06/07 截图 |
| 旧状态写请求拒绝 | 通过；stale-rejected.json，HTTP 409 |
| 无在途动作断线/重连 | 通过；日志 DISCONNECTED→CONNECTED，after-broker-restart.json；不能推广到带未确认结果重启 |
| 返回主菜单清理状态 | 通过；returned-to-main-menu.json，loaded=false |
| 游戏/服务退出 | 通过；SHUTDOWN/DISABLED，after-game-exit.json connected=false；停止 broker 后同端口重新 bind 成功 |
| 清理及原存档保护 | 通过；新测试槽/Mod目录不存在，3 个原 save_0* 文件最终哈希不变 |
| 新开局直接就绪 | **未通过**；loaded-state.json 中新游戏 false；重载新槽后通过。源代码证实 StartNewGame 不触发 OnGameLoadedLate |

保留的失败记录：

- 构建曾失败 **2 次**：NU1100（缺 NETStandard.Library.Ref 2.1.0），NU1301（受限网络代理）；官方 NuGet 还原成功后构建通过。SDK 首次运行输出过开发证书提示；只读核对 CurrentUser/My 仅见 2026-09-14 的既有 localhost 证书，未删除或信任任何证书，后续明确关闭生成选项。
- Python 中间运行曾 **1 error**：Windows 超限 body 提前拒绝后连接 reset，客户端未读到413；保留测试并改为只发送超限长度头，验证服务端提前拒绝，最终19项通过。超限客户端可能见TCP reset，未宣称所有超限上传都有JSON响应。
- 实机旧版本有 **3 次不成功实验**：v0.0.1 初始化 ArgumentException；v0.0.2 网络超时阶段；v0.0.3 确认 HTTP400 invalid_snapshot。日志分别保留。v0.0.4 经后续真实请求、状态和动作验证修复；超时阶段没有足够证据唯一归因于代理/沙箱，不作确定结论。
- 两次独立只读审查发现 broker 重启旧结果409循环、JsonUtility 可选对象风险；分别修为明确停止并要求恢复、显式 has_result/has_command，随后根据实机空 state 改为游戏自带 Json.NET。没有删除失败后称全程成功。

## 安全、生命周期与剩余风险

1. 正式版 LoadModDll 在 OnEnable 返回后才登记 LoadedMod；ReadModFileText 必须延迟一帧。已实机修复；仅 loader 的 Initialized 日志不足以证明探针就绪，还须 BRIDGE_READY/CONNECTED。
2. 新建游戏不触发载入 Hook；本轮限定“正常载入已有槽”。loaded=false 时经营字段无效。M1 应覆盖新游戏、重复载入与更完整场景就绪条件，不能仅依赖 HasInstance。
3. 已交付超时会标 uncertain 并锁住新写请求；不自动重发。broker 重启丢内存结果时，Mod 收到401/409明确停止，需人工核对并重启。此故障分支有回归测试，未做游戏中丢响应故障注入；不承诺持久化 exactly-once。
4. 32项测试不能证明所有Unity线程、崩溃、热卸载、网络洪泛、模态窗口优先级及长期稳定性。响应大小在下载后检查；强鉴权本机broker是信任边界，尚无下载前流式大小限制。主线程轮询不改 Application.runInBackground，失焦/最小化可能延迟心跳，过期请求会拒绝。
5. 应用/内容版本检查不能代替游戏DLL哈希绑定；当前正式版更新后必须重新编译/扫描/实测。不会自动兼容 Demo/Playtest，也不改安全机制。远程信任列表有缓存/联网失败日志，证据是当次正常检查结果，不保证未来名单不变。
6. 本次动作只是关闭菜单，不证明经营交易、全部合法动作、多日自主游玩可行；这属于M1后续工作。

## 外部副作用与恢复结果

用户明确批准后才使用 Computer Use 与游戏目录写入。备份保存目录普通文件到 artifacts/backup/before-live，未读取/上传保存内容。新建测试使用正常UI的“学徒、普通、跳过开场”选项，分配 save_1；未载入用户原 save_0。测试结束将测试槽另备份到项目，并通过游戏UI删除；保留原有第5天进度。原 save_0 及两个历史文件最终哈希完全一致。

游戏已正常退出；PawnPilot 在游戏内停用；Mods/pawnpilot_m0 已按路径和文件清单校验后移除；broker停止，18765可重新绑定。未触碰其他Mod、未修改游戏二进制或dev.txt。游戏正常操作更新了 SaveFile.es3、saves_index.es3、日志和可能的 Steam Cloud 元数据/统计；不通过覆盖全局文件“恢复”以免损伤真实进度，也不声称全局配置字节完全未变。备份、测试原始证据和调查缓存均仅本机忽略目录保留。

## 工程交付与 M1

| 文件 | 作用 |
|---|---|
| .gitignore / NuGet.Config / local.props.example | 隔离游戏资产、保存/个人数据、密钥、路径和产物；本地程序集引用配置 |
| src/PawnPilot.Probe/PawnPilot.Probe.csproj / manifest.xml | netstandard2.1 Mod、network-only包声明 |
| src/PawnPilot.Probe/ModEntry.cs / ProbeBehaviour.cs / Protocol.cs | 入口版本检查、延迟配置、主线程通信/状态/原生动作、DTO与门控 |
| bridge/server.py / client.py | 标准库 loopback broker与手工客户端，不是完整MCP |
| scripts/build.ps1 | 仅项目内构建打包，不自动安装 |
| tests/test_bridge.py / tests/PawnPilot.CoreTests/{PawnPilot.CoreTests.csproj,Program.cs} | 19项协议/进程测试，13项门控测试 |
| README.md / docs/live-test-plan.md / docs/m0-feasibility.md / docs/m0-evidence.json | 复现指南、授权范围、结论与脱敏证据 |

**M1 优先顺序**：先解决新开局就绪并引入明确 schema_version/ready 字段；再增加 broker epoch 与未确认结果恢复协议；随后做最小 Python MCP stdio 的 observe/decide/act 封装，复用现有单写门控；最后选择一个经营动作，逐个核查原生入口、可见合法条件与结果。每个游戏更新都须重做加载/权限/状态/动作四项实机门禁。当前无环境或权限阻塞，但上述工程限制不能省略。

最终工作区全部为未跟踪新文件，main 无初始提交；无暂存、无提交、无push。详细最终状态由交付时 `git status --short --branch` 核对。
