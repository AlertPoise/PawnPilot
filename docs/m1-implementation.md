# PawnPilot M1 实现与验收

2026-10-31，整体 **PASS**。已交付可运行的独立 broker、正式 MCP stdio Adapter 和生命周期修复后的 C# Bridge。真实 Codex CLI 经 MCP 在独立测试存档中执行一次原生 `close_menu`，结果、日志和画面一致。自动化 **82 项通过（43 Python + 39 managed C#）**。测试槽与安装的 Mod 已清理，3 个原有存档文件 SHA-256 全部不变。

本结论仅适用于基础观察和关闭 ESC 菜单；没有验证交易或自主经营。机器可读摘要见 [m1-evidence.json](m1-evidence.json)，原始日志、截图、备份留在 Git 忽略的 `artifacts/m1/`。

## 实现范围与 M0 继承

保留 `Codex → MCP stdio → Python broker ← UnityWebRequest → Unity 主线程`，监听地址仍为 `127.0.0.1:18765`。没有在游戏内监听 HTTP、没有内置模型 API 客户端，Adapter 不启动或重启 broker。

| 模块 | 本轮改动 |
|---|---|
| `src/PawnPilot.Probe/ProbeBehaviour.cs` | 统一新建/载入就绪判断、世界身份变化、两个 Update 帧稳定屏障、保留结果直到确认、broker epoch 握手 |
| `src/PawnPilot.Probe/Protocol.cs` | schema/epoch/revision 契约、纯生命周期组件、版本门禁、命令去重和前后状态验证 |
| `ModEntry.cs` / `manifest.xml` | 显示名 PawnPilot Game Bridge、版本 0.1.0；保留程序集及安装 ID `pawnpilot_m0` |
| `bridge/server.py` | 命令状态机、单写门禁、持久化恢复、严格结果校验、人工状态对齐入口 |
| `bridge/journal.py` | 新增独占进程锁、原子 journal、严格格式和容量校验 |
| `bridge/client.py` | 独立诊断、版本参数、结果查询和人工对齐；限制响应大小，禁用代理/重定向 |
| `pawnpilot_mcp/` | 正式 SDK 注册的四个工具、类型化输入输出、结构化错误与 stdio |
| `scripts/` | 保留构建；新增冻结依赖安装、带收据的 Mod 安装/清理、真实 Codex 验证脚本 |
| `tests/` | 保留 M0 回归并扩充生命周期、持久化/崩溃恢复、SDK 子进程测试 |

开始时审查了 M0 实现并通过原有 32 项测试。4 个非 Astra Luna High 子代理分别负责生命周期、broker、MCP 和独立复查，编码文件所有权分开；主 Agent 集成并复查。独立审查发现的 Capture 身份变化竞态、同帧误计数、二次重启 unknown 恢复问题及人工对齐缺口均已修复并加入验证。仓库双层目录结构未移动。

## 新开局生命周期

M0 依赖 `OnGameLoadedLate`；新游戏走初始化保存路径，不保证触发这个载入 Hook。M1 将 Hook 降为“世界失效/换代”信号，不在同步载入回调里直接宣布 ready。

`loaded` 要求当前为真实 `InventoryScene`，先检查 `PlayerStore.HasInstance` 再访问实例，并检查有效 slot/runID、保存版本及必要服务初始化。`ready` 进一步要求库存窗口/元素、活动的 StoreUI、EscapeUI 和 menuPanel 存在，并在两个不同 Unity `Update` 帧持续成立。访问的 singleton getter 已结合本机 IL 审核，避免观察动作隐式创建实例。

每次 Capture 和执行前重新核对存档身份与信号；负信号立即撤销 ready，Capture 本身不推进稳定帧计数。返回菜单、场景切换、载入 Hook、slot/runID 改变都会使旧世界失效。同一存档重载也获得新 epoch，不复用旧动作版本。`loaded=false` 时 cash=0 是无效占位，不代表玩家余额。

实机新建 Apprentice、普通难度、跳过引导的独立 `save_2.es3` 后，**未重载即 loaded=true、ready=true、现金 550**，与画面一致。随后两次正常载入该测试槽均就绪；返回主菜单 loaded/ready 均为 false。

## 协议与恢复边界

| 字段 | 语义 |
|---|---|
| schema_version | 当前结构版本 1，未知版本拒绝 |
| broker_epoch | 每次 broker 进程启动生成新 UUID |
| session_id | Mod 实例的进程期身份；用于绑定交付及迟到结果 |
| world_epoch | 当前世界生命周期身份；场景/载入/存档身份变化时轮换 |
| revision | 同一世界中可见快照变化递增 |
| version | 必须精确等于 `world_epoch:revision` |
| command_id | MCP 名称；HTTP 命令字段为 id。相同 ID 绑定完整请求内容 |

状态含义：pending 已落盘未交付；running 已先落盘再交付、可能已执行；succeeded 为 C# 后置验证成功；rejected 为未通过门禁或未交付即失效；failed 为已报告错误，不能据此推断无副作用；outcome_unknown 为无法确定执行结果并阻塞后续写动作。HTTP 仍保留部分 M0 status 别名，MCP 对外使用规范 command_status。

同一时刻最多一个未解决写动作。pending 遇到状态变化/到期即拒绝，不继续排队。相同 ID 与完整内容返回已有状态；相同 ID 内容不同拒绝。running 不再次交付。超时不能推断动作失败；MCP 提交超时、协议错误或服务端 5xx 不自动重试，调用方查询原 ID。

journal 在 ACK 前持久化命令、在交付前持久化 running、在确认 Mod 结果前持久化终态；使用独占进程锁、临时文件 flush/fsync 和同目录原子替换。重启后 pending 转 rejected，running 转 unknown；已完成结果仍可查询。原 Mod 保留的结果可以按原 broker epoch 和交付 session 归档并获得显式 ACK，不重放动作。

落盘前失败不交付；running 已落盘但交付是否成功无法确认时保守 unknown。记录损坏、写失败、超过 4096 条或 16 MiB 均 fail-closed，不静默淘汰去重记录。Mod 内缓存上限 1024；正常 broker 保留完整去重记录，因此不依赖 Mod 缓存无限增长。缓存/记录丢失、磁盘损坏或 Windows 断电持久性不在保证内，**不宣称 exactly-once**。

未知结果只能先查询原 ID。若 Mod 退出使结果无法找回，操作者必须核对实际世界和已关闭菜单，保留画面证据；命令已到期、当前快照新鲜就绪、世界/版本已前进且参数精确匹配后，诊断客户端 `reconcile` 才可解除单写阻塞。此操作需要说明和 `confirm_no_replay=true`，保留原 unknown 与独立审计，不伪造成功；迟到有效结果仍可归档。没有“清空未知直接继续”的 MCP 工具。具体命令见 [README](../README.md)。

## MCP、配置与启动

使用官方稳定 SDK **mcp 2.3.0**（v2 入口 `MCPServer`）、Pydantic **2.12.5**，在 Python **3.13.2**、uv **0.13.6** 验证。`pyproject.toml` 固定直接依赖，`uv.lock` 固定传递依赖及哈希。官方包下载在本机过慢，GitHub 下载出现 TLS 失败；最终 uv wheel 通过官方 PyPI 元数据的 SHA-256 校验后从清华镜像获取，锁文件也保留该镜像来源，未将失败下载当作完成。

先运行 `scripts/setup-python.ps1` 和 `scripts/build.ps1`。独立启动 `.venv\Scripts\python.exe bridge/server.py --config artifacts/session.json`，再将 [Codex 配置示例](codex-config.example.toml)合并到项目配置并新开会话。stdio 启动命令为 `.venv\Scripts\python.exe -m pawnpilot_mcp`，cwd 是项目根目录。没有覆盖用户全局配置；本轮真实验证使用 CLI 临时配置。具体安装与清理步骤见 README 和[实机计划](m1-live-test-plan.md)。

| MCP 工具 | 已验证行为 |
|---|---|
| health_check | broker/bridge 连接、loaded/ready、协议兼容性、版本及阻塞原因；can_act 表示当前 close_menu 是否可用 |
| observe | 真实基础快照、合法动作、等待/人工处理原因；不虚构顾客或交易数据 |
| act | 仅 close_menu；必填 ID、broker epoch 与观察版本；经过 broker 和 C# 双门禁 |
| get_action_status | 按原 ID 查询状态及 before/after；404 not_found 与查询不可用分开，均不视作执行失败 |

四工具有明确 JSON Schema；普通业务错误为结构化结果，区分 connection/configuration/protocol/broker/internal 来源。SDK 管理 stdio 协议，日志不污染 stdout。broker 不在线也能初始化并注册工具。`observe.snapshot.can_act` 是 broker 门禁信号，具体动作以 `legal_actions` 为准；菜单已关闭时合法动作为空。

[Agent Skill](../.agents/skills/pawnpilot-player/SKILL.md)已验证格式，约束先健康检查/观察、仅合法动作、一次一写、未知查询原 ID、人工暂停不能自行恢复，不授权作弊或文件操作。

## 测试与真实 Codex 证据

| 验收项 | 结果与覆盖 |
|---|---|
| 生命周期 | PASS：39 项 managed 测试中的纯状态/门禁验证；实机新建直接 ready、返回菜单失效、测试槽两次重载就绪 |
| broker 协议/恢复 | PASS：20 broker + 13 recovery + 2 实际 broker 子进程崩溃测试；重复 ID、并发、旧版本、丢响应、二次重启和人工对齐 |
| MCP SDK stdio | PASS：8 项实际 SDK 客户端/Adapter 子进程测试，发现并调用全部四工具，含 broker 不在线和未知结果 |
| 真实 Codex | PASS：Codex CLI 0.145.0，非 Astra `gpt-5.6-luna`，实际 MCP 调用五次：health×1、observe×2、act×1、status×1 |
| 原生端到端动作 | PASS：ESC 菜单 true→false，revision 3→4，现金 550 不变；UI、MCP 和游戏日志一致 |
| 实机 broker 重启 | PASS：无在途时重启重新连接，原成功结果仍可按 ID 查询，没有再执行 |
| 安全清理 | PASS：测试槽删除、Mod 停用并按收据移除、游戏退出、18765 释放、3 个原有存档哈希相同 |

Python 最终命令 `python -m unittest discover -s tests -p 'test_*.py' -v`：43 passed，35.629 秒；managed 测试：39 passed、0 failed。构建零 warning/error。C# 测试是纯 managed 组件测试，不冒充 Unity 集成测试；Unity 行为由单独实机证据支持。

真实客户端日志：`artifacts/m1/codex/live-close-menu.jsonl`。成功命令 ID：`codex-close-menu-20261031-6c44895d-0001`。原 broker epoch `6c44895d-e1e6-4101-a090-6d468d2f69ba`；新开局 world epoch `103234533c784a8ebd16c398cdaac696`。调用 `act` 后首次查询即 succeeded，reason=observed，before `:3`、after `:4`。游戏 Player.log 仅出现一次该动作的 `PAWNPILOT_RESULT`。

重启 broker epoch 为 `95b562af-3194-4dc2-95d2-535046fae0a0`，已完成结果仍带原 epoch。重载后的 world epoch 依次为 `8ad94bb6218e4186a4c0030987011db5` 和 `53d4c799c06f4e1793bcc561cf5f650d`。旧世界请求返回 HTTP 409 stale_state，旧 broker 请求返回 HTTP 409 broker_epoch_mismatch。

原始证据包括：新开局截图/快照 `03-*`、菜单前后 `04-*`/`05-*`、重启结果 `06-*`、返回菜单 `07-*`、隔离槽界面 `08-*`、重载 `09-*`/`11-*`、旧版本拒绝 `10-*`、删除槽 `12-*`、Mod 停用 `13-*`、退出状态 `14-*`、清理核对 `15-*` 和 final-player.log。文件 SHA-256 均记录在 m1-evidence.json。

中间失败没有计入 PASS：Windows 沙箱阻止 SDK stdio 命名管道（WinError 5），在获批宿主运行后通过；早期 Codex 请求模型 `gpt-6-luna` 被该 CLI 账户拒绝，改用已可用非 Astra `gpt-5.6-luna`；默认审批导致非交互工具取消，改为仅本次已授权 PawnPilot 临时配置 `default_tools_approval_mode="approve"` 后通过。此前错误日志保留。没有使用 Computer Use 点击 Codex 审批按钮或修改全局审批设置。诊断 CLI 的旧 ID 校验曾拒绝带连字符的真实命令 ID，已修复；重启后成功结果查询和最终回归覆盖最终代码。

## 清理、Git 与项目外副作用

用户单独明确批准 M1 安装、Computer Use、独立空槽、重载、MCP 原生动作及清理。原存档没有加载或覆盖。游戏正常写入日志、保存索引、Mod 激活/信任历史及测试保存；Steam 可能同步 Cloud，未声称恢复所有历史/索引字节。未以备份覆盖全局索引。

本次创建 `save_2.es3`，删除前仅备份到本机项目忽略目录，随后在游戏 UI 删除；界面仅剩原有 day 5 / cash 527 保存。Mod UI 确认停用，Player.log 包含 enabled=False、SHUTDOWN、DISABLED。退出后删除安装收据哈希完全匹配的 DLL、manifest、session.json 和空目录。密钥、journal、备份及原始证据保留本机忽略目录，便于复核，不提交 Git。

原有 `save_0.es3`、day3/day4 自动备份共 3 文件前后 SHA-256 相同。游戏 Assembly-CSharp.dll SHA-256 仍为 `6398d44aa1675e1f02b63bc668868f970e0f5d3ae5a50f2aced8d06ca084e3b5`。测试 DLL SHA-256 为 `4fc9ff109a6ed8f7f1a96801821e13c37fbeb3c4504bc7e86b81e5937ed58506`。没有残留游戏进程、项目 broker/MCP Python 进程；端口绑定检查确认释放。

M1 验收时仓库仍无初始提交，共 36 个交付文件处于未跟踪状态；该验收阶段未执行 commit、push 或远端修改。扫描所有 Git 候选文件未发现真实会话密钥或游戏 DLL/存档；测试 fixture 的固定假凭据属于测试数据。此前权限异常的空测试临时目录已移除，git status 不再有该警告。后续经用户授权整理、提交与上传的历史以 Git 记录为准。

## 遗留风险与 M2 条件

| 严重度 | 限制与后续要求 |
|---|---|
| 中 | ready 判断依赖此游戏 build 的初始化结构；目前实机覆盖跳过引导的 Apprentice 普通模式，未覆盖所有角色/教程/异常载入。游戏升级后必须重新审核和实测。 |
| 中 | 在途崩溃、结果丢失和人工对齐以 broker 真子进程/协议夹具故障注入验证；未在 Unity 实机故意制造这些故障。新业务动作接入前补充其原生异步路径和实机恢复测试。 |
| 中 | journal 删除、损坏、磁盘故障和容量耗尽只能停止写入；无自动归档或无限期去重保证。不能靠删记录解除未知。 |
| 低 | C# 仍使用 DownloadHandlerBuffer，64 KiB 上限在下载完成后检查；对恶意本机端点的内存上限保护不足。loopback/token 无法抵御同用户恶意进程。 |
| 低 | 本轮尚未把配置安装进当前桌面 Codex 会话；已用独立真实 CLI 验证，可复制项目配置后新开会话。 |

进入 M2 前，选择一个明确的正常经营动作，审核真实游戏入口、可观察前置/后置条件和失败边界，建立最小真实状态模型。保持单写、版本门禁、原 ID 查询与未知停止，为该动作增加纯逻辑、broker/SDK 和独立测试槽实机验证。须重新授权新的业务动作及存档副作用；M1 关闭菜单的成功不能作为交易安全性证据。

## 核查来源

SDK 版本/接口依据[官方 v2.3.0 发布](https://github.com/modelcontextprotocol/python-sdk/releases/tag/v2.3.0)、[官方 SDK 文档](https://py.sdk.modelcontextprotocol.io/)、[结构化输出](https://py.sdk.modelcontextprotocol.io/servers/structured-output/)和[SDK 测试文档](https://py.sdk.modelcontextprotocol.io/get-started/testing/)。Codex 配置依据[官方 MCP 文档](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)及本机 CLI 实际行为；用户提供的 developers.openai.com/codex/mcp 当时重定向至该地址。游戏生命周期依据本机正式版程序集只读分析和本轮实机证据。
