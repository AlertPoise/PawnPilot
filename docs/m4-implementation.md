# M4：单日 MCP 自主经营

当前状态：项目内实现、正式构建与自动化验证已完成，**尚未进行 M4 实机验收，当前为 PARTIAL**。不能把此前单项交易或模拟测试当作单日 PASS。

基线为 `feature/m3c-inspection-handoff` 的 `fa84975502305f4e99bc1454210aceab16d2141f`，开发分支 `feature/m4-single-day`。沿用 wire v3 / business schema2、五个 MCP 工具、ActionDriver、单写、命令去重、journal 和结果未知不重放机制。

## 原生流程与实现范围

定向只读研究基于当前安装游戏程序集，SHA-256 `6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5`。本地研究证据在 `artifacts/m4/research/closing-sleep.md`、`settlement.md` 及对应 IL；不提交游戏 DLL 或完整反编译内容。

| 动作 | 正常原生入口 | 关键前置与结果 |
|---|---|---|
| `close_business` | `StoreShutterButton.OnClick()` | OPEN，当前可见待接待人数0、无当前顾客、正常门禁允许；等待 CLOSED、门动画结束和前台隐藏。 |
| `open_end_of_day` | 当前活动 `StoreBedroll.OnClick()` | CLOSED 且交互安定；打开真实 EndOfDay UI，日期和现金不变。 |
| `confirm_end_of_day` | `EndOfDayUIManager.OnEndDayButtonClick()` | 绑定当前可见费用、服务选中态与授权上限；等待恰好一天推进、MORNING、实际扣费和次晨报告。 |
| `close_day_report` | `AdvCalendarUIManager.CloseUI()` | 仅关闭已验证的次晨报告，日期现金不变，不开门。 |

`CanEndDay` 只阻止不能遣走的重要顾客，并不证明顾客已经耗尽。M4 额外读取 `StoreClientCounter.clientCounterTMP` 的已显示数字；不读取或导出未来顾客队列。计数不含当前顾客，空文本为未知而非0。关门后卷帘锁保持非零是原生正常状态，睡袋不会等待这个锁归零。

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
- 审查后再补1项原命令 `not_found`/`failed` 不解除恢复限制的测试，重跑会话专项 **9 passed、0 failed**，`artifacts/m4/research/session-tests.log`。当前去重为 **550 managed + 195 Python = 745项**，未将重跑的8项重复计数。
- 正式生产包 `scripts/build.ps1`：**0错误**，本机 NuGet 漏洞源不可达产生2条 NU1900 警告；未关闭审计、未引入新依赖。输出 `artifacts/package/pawnpilot_m0`，没有启用 M3-C 测试强制接管。`build-production.log` 保留结果。
- Agent Skill 的 `quick_validate.py` 通过；`git diff --check` 通过。

上述均为项目内/模拟验证，不能证明 Unity 场景上的按钮、显示布局或实际单日经营已经通过。

实际 CLI 模式为 `scripts/verify-codex.py --m4`，要求显式给出 `--session-id`、`--max-customers`、`--max-writes`、`--max-spend`、`--max-trades`、`--max-bargains`、`--max-night-fee`。每次使用唯一 `--label`；同一 session 的总上限不可变，重启从全部原始 JSONL 累计预算，不因 Codex item ID 重新编号而漏计。`artifacts/m4/codex/session-<session-id>.json` 仅索引证据与预算，不是新执行器或恢复 broker。暂停后只有真实用户确认，才能在同会话续跑时传入 `--confirmed-handoff <当前精确引用>`。未决原命令的续跑只查询状态，不提交新写入。

实机：尚未取得本轮 Computer Use、Mod 安装及独立测试槽授权。M3-C 授权不延续；尚无 M4 项目外安装、游戏或存档写入。只读核对游戏未运行、`pawnpilot_m0` 未安装，仅原 save_0 与两份历史备份存在；四个受保护文件哈希均与 M3-C 结束记录一致，证据 `artifacts/m4/research/pre-authorization-hashes.json`。下一步申请完整实机范围。

待实机记录：起始/结束日期，顾客逐项处理及实际成交，买入累计支出、现金与夜费，议价/人工接管/阻挡次数，原始 CLI MCP 证据路径，测试槽自动保存证据、原存档和程序集哈希、清理收据，最终 PASS/PARTIAL/FAIL 及 Git SHA/远端状态。

## 保留边界

仅覆盖已有普通买卖、议价、默认整理与人工检查能力，以及上述正常日末路径。不支持特殊交易、批发、易货、犯罪、战斗、剧情自动化、自动鉴定、自定义格子规划或多日经营。额外夜间经济变化、未完整显示的服务条款及必要事件需报告并停止，不能靠扩大授权或读取隐藏状态强行完成。
