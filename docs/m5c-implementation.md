# M5-C：持续决策、持久上下文与运行监督

## 基线与证据边界

以本地 `feature/m5b-generic-interaction` / `5bd5e8b84fd80f11dbe88c88838be3d85452e739` 为基线，初始工作树干净。开发分支 `feature/m5c-persistent-autonomy`；保留双层仓库目录，没有修改旧分支历史。编码前重新运行基线：managed 671、Python 219，全通过。

本轮用户明确授权 Computer Use、隔离新槽、真实 Codex CLI/MCP 有界经营及本地 commit。旧 `save_0`、`save_18`、M5-B journal 不加载、不覆盖、不重放。保护哈希在 `artifacts/m5c/live/protection-before.json`。M5-B 的旧 unknown 继续是旧 unknown；新运行不改写那次事实。

## 核心模块与数据流

- `bridge/run_store.py`：版本化 Run Context、Decision Inbox、单调预算账本、调度令牌和恢复对账。
- `bridge/server.py`：在原 enqueue 之前持久预留，交付前复查暂停和令牌；仍由原 Broker journal 和 Unity ActionGate 执行。
- `bridge/client.py` / `pawnpilot_mcp/server.py`：仅为受管运行转发私有 HTTP 调度头；不增加游戏动作、不新增 MCP 工具、不改变 wire v4。
- `pawnpilot_supervisor/runtime.py`：只读 Broker 检查、运行所有权、有界调度、结果查询、计划更新和停止。
- `pawnpilot_supervisor/codex_driver.py`：独立 Codex CLI 子进程、机器事件、结构化最终回复、取消和进程树回收。
- `pawnpilot_supervisor/__main__.py`：用户侧 create/start/status/inbox/budget/pause/resume/stop/amend/rebind/confirm-inspection。
- `protocol/schemas/run-context-v1.json` 与 `examples/m5c-authorization.json`：持久文档 Schema 和无凭证授权样例。

经营写入链始终是 `独立 Codex CLI → 五工具 MCP → 原 Broker → Unity Mod`。Supervisor 不自动挑选选项或执行任何游戏命令；每轮一次新 act，新的重要决策回到 Agent。

## 持久化与 Inbox

选择同目录 JSON 原子替换，不引入数据库服务。每次事务持有 OS 文件锁，严格检查版本、字段、类型、长度、集合容量和关联不变量；临时文件 fsync 后 replace，损坏、容量耗尽或写失败明确阻挡，不自动清空。独立 Supervisor 长锁阻止同一任务双启动。Windows 锁文件先取得 byte-range lock 再检查/初始化文件内容；双进程回归覆盖竞争，避免锁外读取受锁字节导致虚假的锁失败。

Inbox 保存稳定身份、语义 fingerprint、时间、命令关联、状态和有限结果证据。重复 revision、typing 前缀或 surface/control 重签不会无限新增待办；世界、顾客、对话、游戏日、选择或交易集合变化会区分新决策。阅读不消费待办。旧 pending 失去适用条件时 superseded；submitted 只能依据原命令证据处理；用户手动遣走不记成 Agent 成功。

状态包括 pending、submitted、resolved、superseded、awaiting_user、blocked_unknown、unsupported。已解决的语义再次出现时是新的 occurrence，不复用旧成功结论。

## 授权、预算与计划

用户侧 create/amend 管理授权版本和上限，CAS 防止旧管理操作覆盖新约束。Agent 只通过最终结构化 plan 字段修改计划，不能更改授权。已确认历史来自真实 journal ID/结果摘要；自然语言计划不是事实。

七项预算：顾客接待请求、写请求、普通交易尝试、买入支出预留、议价选择、日结费用预留、跨日次数。预算在入队前保守累计；成功、拒绝和 unknown 不自动返还已预留额度。重复同 ID 不重复扣账；新的有效令牌下的拒绝尝试仍计写请求。买入与日费按请求授权上界保留，因此可能比实际支出更保守。

预算、命令与目标不会因 CLI/Supervisor/Broker 重启或读档重置。日结进入目标次日后只允许必要晨报收尾，不再开门。用户收紧授权可阻止余下计划，历史消耗仍保留。

授权样例使用本轮上限：12 次接待、100 次写请求、4 次普通买卖尝试、买入550、4次议价选择、日费30、两个日结。模型看不到可用于扩大授权的管理接口。

## Journal 一致性、暂停与恢复

保留原 journal format 2 与 wire v2/v3/v4，避免给历史记录塞入新字段。Run Store 固定绑定规范 journal 路径，预留保存原 command ID 和 request fingerprint：先原子保存预算/预留，再原 journal 入队，再从 journal 投影结果。

当前 journal 没有不可变实例 ID：同路径替换只能依靠已有命令关联发现，空任务的同路径空 journal 替换无法独立识别；不得把复制/替换 journal 当作支持的恢复方式。跨文件崩溃会留下保守边界：匹配的原 journal 修复关联；孤立预留或不匹配记录阻挡，不能猜没有执行而重试。恢复不清 journal，不改旧 unknown，不创建替代 ID。预算账本与预留逐项核对；未登记的 journal 写操作不能成为受管运行成功历史。

每轮短期随机调度令牌只通过子进程环境和私有 HTTP 头传递，文件仅存哈希。一个令牌最多一个新 ID，绑定授权版本、世界、Mod session 和 Broker epoch；暂停、授权变更和恢复撤销旧令牌。暂停与交付共用 RunStore 文件锁；deliver 在锁内复核授权并提交原 journal 的 running 状态，避免检查与交付之间的暂停竞态。暂停先禁止新入队/交付；已经进入原生执行的动作继续按原 ID 查询，取消 CLI 不等于撤销游戏动作。正常 CLI 退出后先确认已入队命令终态，再撤销本轮令牌。

运行恢复先检查持久任务、journal、未决命令、人工等待和用户控制，再 fresh observe。当前协议没有可验证的跨加载存档 ID：save_label 是操作员标签，实际绑定 world_epoch 和 Mod session；任何重建都停止并要求明确 rebind，不自动声称同一存档。rebind 保留预算与旧命令，不能解除 unknown。M3-C 人工检查默认保持阻塞，不由 Supervisor 自动确认。用户对当前商品明确作出处理决定后，用户侧 confirm-inspection 可将原话与当前 world/session/customer/handoff/item refs 绑定，持久保存一次性 operator_confirmation；只有该确认匹配时，受管 Agent 才能提交对应 resume_inspection_handoff。确认消费的 command ID 不可换 ID 重用，读档/rebind 会清除未适用确认；预算仍累计。用户接受商品不是鉴定真品的事实，也不会修改游戏物品属性。

## Codex CLI 接口及进程所有权

本机实查 `codex-cli 0.145.0`。使用官方 `codex exec --json --output-schema`，每轮新建独立会话，记录 `thread.started.thread_id`；不使用 `--last`，不把外层开发会话当经营会话。恢复的是 Run Context，不是旧 transcript 中的执行引用。

独立 cwd 位于 `artifacts/m5c`，read-only sandbox，禁用 shell_tool/multi_agent，忽略用户 config，仅配置 PawnPilot 五 MCP 工具。使用现有账户登录，不要求 API key，不在 Unity 中等待模型。JSONL 工具事件和最终结构化答复分别校验；退出码0、模型说“完成”均不是游戏成功依据。

Windows 使用隐藏、挂起启动 → 加入 kill-on-close Job Object → 恢复自有进程线程 → 发送 prompt，消除进程树归属竞态。超时/取消只停止本轮自有子进程树，不关闭游戏/Broker；OS 在监督器崩溃后回收 Job。日志有容量上限，异常工具事件失败关闭。模型诊断与工具事件分开解析。

官方接口依据：[非交互模式](https://learn.chatgpt.com/docs/non-interactive-mode)、[MCP 配置](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)。本机 `exec --help` 支持的参数才进入 argv；超时由 Python deadline 实现。

## 使用流程

先按已有脚本准备 Mod、新隔离槽和独立 session.json。启动 Broker 时指定 `--run-context`，该路径会固定进入 session 配置，之后省略参数仍不能退回不受管模式。Run Store 尚未创建时 Broker 只提供观察，写入失败关闭。

```powershell
.\.venv\Scripts\python.exe -m bridge.server --config artifacts/m5c/live/session.json --run-context artifacts/m5c/live/run.json
.\.venv\Scripts\python.exe -m pawnpilot_supervisor --run artifacts/m5c/live/run.json --config artifacts/m5c/live/session.json create --run-id m5c-live --save-label NEW_ISOLATED_SLOT --authorization examples/m5c-authorization.json --days 2
.\.venv\Scripts\python.exe -m pawnpilot_supervisor --run artifacts/m5c/live/run.json --config artifacts/m5c/live/session.json start --max-turns 40 --timeout 180
```

create 在当前 fresh ready world 建立暂停任务；start 是用户明确启动，先取得单 Supervisor 所有权再恢复暂停，之后仍检查其他门禁。第二个 start 不能解除现有运行器的暂停。pause/resume/stop 是管理入口，resume 不自动启动调度；start 才再次运行。status/inbox/budget 只读，不消费事项。amend 需要 expected-revision；rebind 需要 fresh world 的逐字确认和 expected-revision。confirm-inspection 需要用户真实决定、当前 handoff-ref 和 instruction；CLI fresh observe 后进行 CAS，可额外指定 expected-revision。该例外不允许模型调用管理 CLI，不改变原预算、目标或经济动作准入。stop 不撤销已经发生的游戏动作。

## 完成条件与功能边界

正常完成必须同时证明：当前目标次日 MORNING、晨报关闭、无日过渡、同世界/Mod session、无未决命令或人工阻挡，以及逐日成功的 close_business → open_end_of_day → confirm_end_of_day → close_day_report 证据链。目标次日剩余 start_business 和可选 sort_inventory_default 时属于后续经营，不再派发。目标日排序仍被预算/日期门禁拒绝；可选整理不会无限阻止已证明的日目标完成。

无界轮询、自动重试、任意原生入口、未知经济选项和自动鉴定均未新增。M5-D 可扩展计划中的声望目标与已验证观察；M5-E 可添加审核后的经济契约，不能靠计划文字开放现有非法动作。电话、废料经济执行、武器、报警等仍在 M5-E/F 之外的明确限制内。

## 自动化与实机记录

本机最终回归：managed **671/671**，Python **284/284**，合计 **955 项通过**。新增 Python 65 项，覆盖持久 Schema/损坏拒绝、Inbox 去重/失效/重复 occurrence、预算与证据关联、Broker 恢复、MCP stdio 调度头、暂停交付竞态、单所有权、CLI 生命周期与连续模拟决策。原 M5-B 特殊顾客跨帧引用与 unknown 测试继续通过。JSON Schema/Pydantic 和 MCP stdio 包含在全量回归中。Skill validator、`git diff --check` 通过。

正式 Release 构建 0 错误、2 个 NU1900 警告（NuGet 漏洞源无法联网），未修改游戏程序集。日志：`artifacts/m5c/final-managed.log`、`final-python.log`、`final-build.log`。初次全量回归发现旧无令牌请求被传入新增参数、破坏原超时测试桩调用；修复兼容调用后全量重新通过，未删改旧断言。

真实 CLI 接入先尝试任务指定的 `gpt-6-luna`，账户返回 `model_not_found`。用户随后明确允许**仅经营验收**使用 `gpt-5.6-luna`，开发子 Agent 仍为 GPT-6 Luna。默认模型保持 `gpt-6-luna`；在本机经营验收需显式传 `start --model gpt-5.6-luna`。两种 Luna 的已知 metadata fallback 警告按所选模型精确识别，其他错误仍失败关闭。

只读 CLI 首次成功会话 `01a19bb3-c24f-7461-a70b-7164ef2191bb` 调用了真实 MCP health_check，当时 Broker 未启动，结果如实为不可达；没有 act。另一次已启动隔离 Broker 的只读验证证据与最终 Unity 验收情况见下节。

### 真实游戏过程

首次 Computer Use 激活 Steam/游戏窗口返回 `failed to activate captured window`；用户要求重试后，重新初始化并获取新窗口绑定，控制恢复正常。通过正常菜单启用 Mod、重启，创建学徒/普通难度/跳过开场的新 `save_20`。另外观察到 `save_19`，未加载它并另行记录保护哈希。原 save_0/save_18 不加载、不覆盖。

首段 Supervisor 一次运行完成 `start_business`、`receive_next_customer` 两个真实 CLI/MCP 决策，均有 succeeded 原始结果，现金550不变。第二个 CLI 在结果已成功后因取消回调异常被停止，Supervisor 保留记录并暂停。调查发现 Windows 锁文件初始化在取得锁之前读取受锁字节；双进程测试复现并修复。日志未记录当次回调异常具体类型，因此将此列为实机发现并修复的竞争风险，不把定位推断当作原始异常堆栈。修复后的取消日志会保留异常类型（不输出异常敏感内容）。

首位顾客触发香烟人工检查，Run Context 保持 awaiting_user。用户随后明确指示“直接当正品一样买入这款香烟”；将原话绑定当前 world/session/customer/handoff/item-24 为一次性 operator_confirmation，未声称用户或 Agent 已鉴定真品。真实 MCP `resume_inspection_handoff` succeeded 后重新观察。后续普通准备与成交均 succeeded：当前合法整单含香烟及其他普通商品共7件，普通价206（批发价159未使用），现金550→344；香烟在主库存 owned=true，仍保留游戏原有赝品名称。普通购买的原生物品重新实例化及引用变化按原 M2 交易契约核验，未冒充跨容器永久引用。

在同一 journal/Run Context 上完成 Broker 重启和 Supervisor/CLI 新会话恢复：此前两个 succeeded 不重放，预算仍为2写/1接待。恢复前 paused=true、can_act=false。后续安全轮次检查点再次暂停，写预算保留3；再恢复后继续准备、购买、接待和遣走。没有重建 Run Context 或重置预算。

真实初期模型把报价未验证误当成所有动作都不能执行，进行了有界等待。补充提示澄清：成交需要完整报价/容量，当前合法准备动作可先形成报价且本身不付款；没有硬编码 NPC 或代替模型提交动作。

实际经营共16个独立 CLI 会话（15次正常退出，1次上述取消），共15条真实游戏命令，**15 succeeded、0 rejected/failed/unknown**。其中一次 Supervisor 启动连续完成12条动作，无需逐动作输入“继续”。总预算消耗：接待4/12、写入15/100、普通交易1/4、买入206/550、议价0/4、日费5/30、日结1/2。真实动作包括开门、接待、用户确认后的接管恢复、商品准备、普通购买、普通及特殊遣走、关门、日结面板、默认费用确认、晨报关闭。

第1天完成关门→日结面板→费用5确认→进入第2天→关闭晨报，现金344→339；证据见 `day-one-chain.json`。该日含一次用户对香烟的明确处理决定，因此**正常单日流程已完成，但不能称为完全无人工决定的一天**。

第2天绿色名片顾客通过带 surface/control refs 的通用原生遣走，命令 `cmd-m5c-live-r16-dismiss-customer-20261102-001` 返回 succeeded；现金339不变、前柜绿名片与报纸及主库存的 item refs/名称/归属完整相等。`special-dismiss-confirmed.json` 与原 journal 包含 before/after、native execution completed 证据。M5-B 最终修复包的绿色名片遣走复验 **PASS**；旧 `m5b-f26ee19a83b940d6` 仍保持原 unknown，不对旧记录作成功推断。主动 advance_dialogue 和主选项 choose_dialogue_option 没有本轮真实执行样本，仍为 **PENDING**；过期引用、未审核经济效果拒绝属于自动化证据。

第2天后续特殊顾客停在 `surface.status=unsupported`、`dismiss_cleanup_unverified`、legal_actions=[]。没有点击原生未审核入口，也没有自动处理特殊经济效果。原两日任务停于第2天，未到目标第3天，completion_evidence 仍为空。重启同一 Broker/Supervisor 后返回 unsupported、turns=0，CLI会话数16和命令数15均未增加。未把轮次上限、空 legal_actions 或模型总结当作完成。

实机 Broker 日志中出现过被严格拒绝的 transient invalid_snapshot，随后恢复为有效快照，未用这些被拒绝的数据入队；本轮无因此产生的未知经济结果。保留原日志，不据此声称所有过渡帧都完全无缺陷。

### 验收分级

| 项目 | 结论与证据边界 |
|---|---|
| 项目内实现、协议与自动化 | PASS，包含原回归、持久化、锁竞争、Schema、预算/恢复与完成条件 |
| 真实 Codex CLI 连续决策 | PASS，一次运行连续12条MCP动作；决定来自独立模型会话 |
| 暂停、原记录恢复 | PASS（实机安全检查点与Broker重启）；预算/历史未清零 |
| 在途崩溃、unknown不重放 | PASS（模拟）；未人为在真实经济执行中制造故障 |
| 正常单日经营与日结 | PASS（含一次用户商品处理决定） |
| 完全无人工单日、两日目标完成 | PARTIAL；首日用户确认、次日未审核交互阻挡 |
| M5-B 绿色名片最终包复验 | PASS；主动推进/选项样本仍PENDING |

### 最终恢复审计与清理

收尾审计发现旧人工待办在 handoff/对话语义变化后残留 awaiting_user。修复使当前事项新建或复用时均淘汰无在途命令的旧 manual/unsupported 项，并在 manual→unsupported 切换时清除过期人工门禁；只标 superseded，不能伪造 resolved，也不触碰 submitted/blocked_unknown。新增回归覆盖重启后既有事项、人工确认后的完整日链、unsupported 消失后的完成判定及在途状态保留。

最后一次同一 Broker/Run Context 恢复实测：15 条命令与15条确认历史保持不变，Inbox 为 resolved=15、superseded=6、unsupported=1，无陈旧 awaiting_user；预算仍为4/15/1/206/0/5/1。Supervisor 再次返回 unsupported、turns=0，经营CLI会话仍16个。证据为 `final-audit.json`、`supervisor-inbox-recovery.log`。这是安全停止，不能计作两日完成。

已通过游戏正常菜单停用 Mod、退出游戏；按安装收据核对哈希后移除3个测试Mod文件及空目录。本轮 Broker、Supervisor和经营CLI进程均已退出。隔离 save_20 保留，未删除任何存档。原 save_0/save_18及其历史备份、游戏程序集、旧M5-B journal共7个保护文件哈希全部一致；另行观察后保护的save_19也一致，合计8个。清理和哈希证据：`artifacts/m5c/live/cleanup-audit.json`、`protection-after.json`。原始日志、Run Context、journal与CLI证据均保留在忽略目录，不提交凭证、游戏文件或原始长日志。

最终结论：项目内 **PASS**；实际完成首日普通经营与日结（含一次用户商品决定），完整自主单日及两日任务 **PARTIAL**。M5-D可接续声望目标与持久计划；M5-E/F仍需补齐审核后的特殊交互/经济效果契约和真实样本，不能靠扩大调度权限跨过本轮 unsupported。
