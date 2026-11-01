# M4：单日 MCP 自主经营

当前状态：项目内实现、正式构建与自动化验证已完成，**M4 实机验收进行中，当前为 PARTIAL**。不能把此前单项交易或模拟测试当作单日 PASS。

基线为 `feature/m3c-inspection-handoff` 的 `fa84975502305f4e99bc1454210aceab16d2141f`，开发分支 `feature/m4-single-day`。沿用 wire v3 / business schema2、五个 MCP 工具、ActionDriver、单写、命令去重、journal 和结果未知不重放机制。

## 原生流程与实现范围

定向只读研究基于当前安装游戏程序集，SHA-256 `6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5`。本地研究证据在 `artifacts/m4/research/closing-sleep.md`、`settlement.md` 及对应 IL；不提交游戏 DLL 或完整反编译内容。

| 动作 | 正常原生入口 | 关键前置与结果 |
|---|---|---|
| `close_business` | `StoreShutterButton.OnClick()` | OPEN，当前可见待接待人数0、无当前顾客、正常门禁允许；等待 CLOSED、门动画结束和前台隐藏。 |
| `open_end_of_day` | 当前活动 `StoreBedroll.OnClick()` | CLOSED 且交互安定；打开真实 EndOfDay UI，日期和现金不变。 |
| `confirm_end_of_day` | `EndOfDayUIManager.OnEndDayButtonClick()` | 绑定当前可见费用、服务选中态与授权上限；等待恰好一天推进、MORNING、实际扣费和次晨报告。 |
| `close_day_report` | `AdvCalendarUIManager.CloseUI()` | 仅关闭已验证的次晨报告，日期现金不变，不开门。 |

`CanEndDay` 只阻止不能遣走的重要顾客，并不证明顾客已经耗尽。M4 通过 `StoreUIManager.clientCounter` 指向的 `TextMeshProUGUI` 读取已显示数字（由原生 `StoreClientCounter` 更新）；不读取或导出未来顾客队列。计数不含当前顾客，空文本为未知而非0。关门后卷帘锁保持非零是原生正常状态，睡袋不会等待这个锁归零。

日结通过现有 `observe` 的可选 `business.details.day` 描述日期、显示的剩余人数、关门/睡袋/确认能力、过渡状态及夜报。`settlement` 绑定当前面板、日期、总额和实际可见服务行；服务开关保持打开 UI 后的原生默认状态，没有调整服务的 MCP 动作。费用保持有符号整数，不硬编码电费、不把负额取绝对值。

原生确认先启动黑屏，回调依次执行 EndNight、OnDayEnd、BeginDay 和 SaveGame，随后打开真实 AdvCalendar 夜报。黑屏协程会捕获回调异常，因此不能仅凭动画结束或夜报出现宣告成功。当前受限普通路径联合核验同一世界、日期+1、MORNING、现金差等于确认总额、结算界面退出及稳定次晨报告；意外差额/异常保留 unknown，不重放。保存落盘需另用授权测试槽的证据审计，不额外调用 SaveGame。

正常跨日保留 run/slot 和 world_epoch。日期变化会显式撤销短期交易引用；日结命令仍在原 ActionGate 内完成，未放宽普通买卖的世界校验。

## Agent 连续经营

经营策略由真实 Codex CLI 根据最新可见信息决定。阶段为 PREPARE、OPENING、SERVING、BETWEEN_CUSTOMERS、CLOSING、SETTLEMENT、NEXT_MORNING、DONE；每次转换需要当前观察或已核验结果。交易与议价不强制发生，容量不足时可以正常拒绝。

每次写入前重新 observe，逐字复制当前动作版本与合法参数；一次一个命令，查询原 ID 至终态。连续三次业务状态不变停止盲等。拒绝、超时和未知结果不能自动换 ID 重试。独立经营会话累计接待、写请求、交易、买入支出和议价预算，重启不重置；进入次日后只允许必要夜报收尾，不开始第二天营业。

M3-C 人工检查完整复用，未扩大分类器，生产包不启用测试强制接管。自然出现 handoff 后暂停，向当前用户会话提醒，只有真人明确确认才能恢复同一 handoff，并重新观察以避免重复人工成交。不支持的必要剧情无已验证合法路径时暂停，不作弊跳过。

## 验证记录

自动化基线：此前 M3-C 已报告 506 managed + 178 Python = 684 项。原基线均保留。本轮结果：

- managed 全套 **550 passed、0 failed**，`artifacts/m4/research/managed-tests.log`。新增44项覆盖关门/剩余与当前顾客/过渡、睡袋、费用授权与signed金额、日期/现金/报告结果、旧引用、命令去重、人工接管和夜报禁止开门。
- Python 全套首次 **194 passed、0 failed，82.003s**，`artifacts/m4/research/python-tests.log`。包括既有178项，以及新增8项日结协议与8项会话审计测试。
- 审查后再补1项原命令 `not_found`/`failed` 不解除恢复限制的测试，重跑会话专项 **9 passed、0 failed**，`artifacts/m4/research/session-tests.log`。当时去重为 **550 managed + 195 Python = 745项**，未将重跑的8项重复计数。
- 实机修复后当前覆盖 **554 managed + 197 Python = 751项**：managed全套554通过（`managed-loaded-report.log`）；Python全套197通过、86.028s（`python-final-repair.log`），之后针对加载夜报关闭与有界等待修订分别复验日结8项、会话11项通过（`report-protocol-tests.log`、`session-final.log`）。正费用扣款、负费用返款及错误差额拒绝均覆盖，未将专项复跑重复累计。
- 正式生产包 `scripts/build.ps1`：**0错误**，本机 NuGet 漏洞源不可达产生2条 NU1900 警告；未关闭审计、未引入新依赖。输出 `artifacts/package/pawnpilot_m0`，没有启用 M3-C 测试强制接管。`build-production.log` 保留结果。
- Agent Skill 的 `quick_validate.py` 通过；`git diff --check` 通过。

上述均为项目内/模拟验证，不能证明 Unity 场景上的按钮、显示布局或实际单日经营已经通过。

实际 CLI 模式为 `scripts/verify-codex.py --m4`，要求显式给出 `--session-id`、`--max-customers`、`--max-writes`、`--max-spend`、`--max-trades`、`--max-bargains`、`--max-night-fee`。每次使用唯一 `--label`；同一 session 的总上限不可变，重启从全部原始 JSONL 累计预算，不因 Codex item ID 重新编号而漏计。`artifacts/m4/codex/session-<session-id>.json` 仅索引证据与预算，不是新执行器或恢复 broker。暂停后只有真实用户确认，才能在同会话续跑时传入 `--confirmed-handoff <当前精确引用>`。未决原命令的续跑只查询状态，不提交新写入。

用户已在当前会话批准完整 M4 范围：最多1个测试槽、24位顾客、180次写请求、8笔普通买卖请求、550累计买入支出、6次议价、150夜费，正常默认服务、仅一次跨日；必要修复可在累计限额内退出、重装并继续同槽。CU仅用于启动/准备/观察/清理，人工检查需另等当前会话真人确认。M3-C授权未被沿用。

授权前四个保护文件哈希均与 M3-C 一致，见 `pre-authorization-hashes.json`；授权后备份并逐项hash核对存档根目录26文件，见 `artifacts/m4/live/baseline-files.json`、`backup-before/`。生产Mod按收据安装，普通 network/reflection-external 权限，开发者模式关。唯一新槽为 `save_17.es3`，学徒/普通/无特质/正常Skip Intro，第1天现金550；原第7天槽未加载。

首次真实CLI `slot17-day-01.jsonl` 仅提交 `start_business` 一次。期间broker短暂返回 `delivery_outcome_unknown`，随后同一原ID最终归档 `succeeded`；CLI按未知即停规则停止，没有接待或交易。画面显示剩余2位顾客，MCP计数为null。调查查明场景的 `StoreUIManager.clientCounter` 指向 `clientCounterTMP` 文本对象，本对象没有 `StoreClientCounter` 控制组件；修复为直接读该对象的 `TextMeshProUGUI`，保留渲染可见性与严格数字解析。另修复 `start/receive` 的原生候选生成未应用新增日阶段门禁，避免动画期间输出协议不允许的合法动作而导致HTTP400。没有读取未来队列作为计数兜底。

必要修复包构建0错误、managed复验550通过。游戏通过正常窗口关闭退出后按三文件收据换包，载入同一个第1天 `save_17`；此前仅开门、无经济变动，未创建新槽。后续CLI使用 `slot17-day-02` 和相同 `m4-slot17` 会话总限额，保留首次写请求消耗。原始游戏日志与journal另存 `player-before-repair-01.log`、`journal-before-repair-01.json`。

用户同时报告的 `uv-0.12.19.exe` 异常，经只读进程检查属于另一个 ClassEcho 项目的 `tmp/setup-validation/.../runtime-blocked/cache/bootstrap/`，父进程PowerShell；未关闭或修改该项目进程，不将它作为PawnPilot错误原因。

`slot17-day-02` 正确读到剩余2位顾客，开门、接待成功；自动对话及来货仍变化时准备请求被 broker 入队前 `stale_state` 拒绝。原ID查询 not_found、同epoch新观察和journal无记录共同证明未入队。CLI审计保留消耗和原顾客/动作禁止重放；孤立not_found、超时、未知或换epoch不能作为放行依据。已完成的历史证明固定在当时首条新观察，不因后来正常重启broker失效。Agent Skill/prompt增加对话自动进展等待和准备前商品稳定观察。

`slot17-day-03` 没有重试该准备动作，而是正常遣走第一位顾客。第二位顾客有化学用品需求，但观察交易类型为other，仅有已验证的普通遣走动作，CLI等待其自动台词结束后正常结束交互。总计接待2位、拒绝2位、无买卖/议价/整理/人工检查。随后CLI确认剩余0、顾客缺席，依次通过MCP执行 `close_business` 和 `open_end_of_day`，均获 succeeded。

日结真实条款为电力5（默认开启）、保险6（默认关闭）、清洁3（默认关闭），合计5。CLI按当前引用提交 `confirm_end_of_day`，游戏实际推进第1→2天、现金550→545并自动保存，UI夜报显示电费结清。但Python成功核验把现金差符号写反，拒收原生成功结果为HTTP409 `invalid_result_evidence`，broker保留 `outcome_unknown`。此前日志中的HTTP400是更早的一次快照问题，不是该日结结果的拒绝码。`slot17-day-04-recovery` 仅查询原命令，无写入。

修复现金差为 `before.cash-after.cash == amount`，保留负费用的返款语义，回归额外差额仍拒绝；同步补齐JSON Schema遗漏的M3/M4候选动作enum。未改变unknown门禁、结果执行序列或journal规则。原生完整结果与原Player.log逐字段相等后，经**现有迟到结果exchange接口**归档；没有调用新act、没有改journal记录，也没有人工reconcile。该开发诊断步骤单独留存 `artifacts/m4/live/retained-result-delivery.json`，不能算自主MCP验收。真实CLI `slot17-day-05-recovery` 查询原ID已succeeded，费用5得到确认。第1天因此仍不宣称完整自主PASS。

保存落盘证据：测试槽从165927字节更新为166426字节；留存 `test-slot-day2.es3`，SHA-256 `72D23B91BF6B5129A0392A7D98A823802E23BEBB3C5AD2C43BF015BA928A7318`。游戏载入列表显示唯一测试槽第2天/545，原第7天/394槽未操作。

用户随后明确授权“再开业一天测试”：同一个save_17追加第2→3天，到第3天早晨停止，不增加原累计预算。前一单日session累计10次act、2次接待、5夜费；新单日 `m4-slot17-day2` 固定剩余22顾客、170写请求、8买卖请求、550买入、6议价、145夜费。两份原始session证据共同累计，而非重置全轮预算。

载入后用户要求停止Computer Use，后续观察与经营交给MCP。`slot17-day2-01` 发现原生夜报重新打开，stage=morning，但close_day_report错误依赖当前Mod进程刚完成日结；CLI三次有界等待后停止，零写入。已修复：morning/next_morning、真实夜报可见且无顾客时允许正常关闭，只验证日期现金不变，不视作日结成功。构建0错误，新增4项managed回归；用户另行批准退出/换包/重启/载入这一维护例外。一次CU被用户Esc停止后即停；用户回复继续后才恢复维护。

`slot17-day2-02` 实机通过MCP关闭载入夜报、开门并接待第一位顾客，三个命令均succeeded；现金545不变，显示当天3位、接待后还剩2位。首位NPC的可见对话是特殊商业提议，并留下“名片（绿）”；观察trade_type=other、dialogue_stage=updating、无合法动作，dismiss_customer也因原生前置不可用。没有使用GUI处理剧情。CLI停止，后续 `slot17-day2-03` 只作有界观察确认阻塞，不开发特殊剧情路径。

用户后来明确允许必要时自行启动Computer Use；仍不用于经营。后续人工处理决定、单日验收、清理与最终Git状态待记录。

## 保留边界

仅覆盖已有普通买卖、议价、默认整理与人工检查能力，以及上述正常日末路径。不支持特殊交易、批发、易货、犯罪、战斗、剧情自动化、自动鉴定、自定义格子规划或多日经营。额外夜间经济变化、未完整显示的服务条款及必要事件需报告并停止，不能靠扩大授权或读取隐藏状态强行完成。
