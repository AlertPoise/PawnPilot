# PawnPilot — M4 单日自主经营

M4 **PARTIAL（实机测试已结束并清理）**，位于 `feature/m4-single-day`。新增正常关门、睡袋日结、费用绑定确认与次晨报告收尾。真实 Codex CLI 已在第2天通过 MCP 完成关门→默认费用5→第3天→关闭夜报；当天特殊剧情由用户处理，因此不计整天完全自主。追加第3天测试遇到不支持的放射性废料交互，用户选择结束，未进入第4天。全轮24次写请求、0买卖、夜费10；独立测试槽及历史已删除，Mod已停用卸载，broker已停止，原存档、两份历史备份及程序集哈希不变。正式构建0错误，自动化 **554 managed + 197 Python = 751项**通过。证据、授权、已知问题与清理记录见 [M4 报告](docs/m4-implementation.md)。

M3-C **PASS（人工接管与恢复、防重复买入路径）**：七个已核实的香烟/水容器 identifier 经生产观察触发 `inspection_handoff`，等待时拦截经营写操作；只有用户明确检查结束后才通过 `resume_inspection_handoff(handoff_ref)` 恢复并重新观察。真实 Codex CLI/MCP 已对授权测试商品完成接管，两次购买/议价门禁探测均被拒绝；用户手动处理后确认，MCP恢复成功，发现商品已自有且顾客离场，未重复购买。**本轮没有 Agent 自动成交**，七类指定商品未取得各自实机触发样本。自动化 **506 managed + 178 Python = 684 项通过**。测试槽与Mod已清理，原存档和程序集哈希不变。详见 [M3-C 报告](docs/m3c-implementation.md)。

M3-B **PASS（普通买入方向议价后正常结束）**：真实Codex CLI经MCP自主选择降价10%，顾客接受，正常报价265→238。既有嵌套容器容量限制阻止购买，Agent合法遣走，现金仍550；**没有实际成交**。卖出方向和议价后实际结算未实测。自动化436 managed +166 Python =602通过。详见 [M3-B 报告](docs/m3b-implementation.md)。

新增普通议价观察及 `open_bargain_panel`、`choose_bargain_option`、`close_bargain_panel`，仍使用现有五个 MCP 工具和 ActionDriver。Codex 根据可见报价、现金、选项与提示决定是否议价；Mod 不预测随机结果或硬编码最大折扣。提议提交、客户接受和实际成交分开核验；普通买卖继续绑定游戏当前显示金额和新报价引用。

M3-A **PASS（已验证的基础范围）**，交易面板自主开启收尾已通过：真实 Codex CLI 经 PawnPilot MCP 调用 `open_trade_panel(customer_ref)`，将普通 SELL 顾客的面板由 closed 打开为 sell，观察到 trading 和正常总价0（空柜台，无可执行成交报价），现金保持550；没有用 GUI 点击顾客。普通 BUY 开面板已有实现和自动化覆盖，本轮未取得实机样本。自动化 **377 managed + 149 Python = 526通过、0失败**。范围、原始证据索引与清理结果见 [M3-A 报告](docs/m3a-implementation.md)。

原始 M3-A 实机已选择并卖出一件“泥炭”合成肉，正常报价29，现金550→579，玩家主库存8→7；正常拒绝、成交后遣走及固定原生整理均通过。`prepare_sale_items`、`sell_visible_offer`、`sort_inventory_default` 保持可用，所有动作沿用五个 MCP 工具和现有执行器。手动对话/选项仍缺成功实机样本。

《深空当铺：可能是偷的》的本机 MCP 工具链。历史 M2-C/D 基础普通买入闭环 **PASS**：真实 Codex CLI 已经 PawnPilot MCP 完成开门、接待、商品准备、读取普通报价、购买和结果核验，顾客正常离场后可继续接待。成交普通价444，现金550→106，13件商品进入玩家库存并变为自有。各动作仍按当前 legal_actions 和原生条件独立开放，不提供任意方法执行。

Codex → MCP stdio Adapter → loopback HTTP broker ← Unity 主线程主动轮询 Mod。

Adapter 与 broker 独立进程；Adapter 不启动 broker，Codex 退出不销毁结果。保留安装 ID pawnpilot_m0 和既有双层目录。默认 Mod 使用 [wire v3](protocol/v3-contract.md)，broker/MCP 显式兼容 v2；新 business payload 使用 schema 2，历史 schema 1/journal 不重标版本。见 [M2-C 观察报告](docs/m2c-implementation.md)、[M2-D 动作报告](docs/m2d-implementation.md)、[冻结的 v2 契约](protocol/v2-contract.md)及 [M2-B 报告](docs/m2b-implementation.md)。

## 环境与构建

Python 3.11+（实测 3.13.2）、uv 0.13.6、.NET 10 SDK、正式版游戏 0.46D / VERSION 100-Rev6 / build 26337366。Mod 目标 netstandard2.1，只引用本机游戏 DLL，不打包这些 DLL。

~~~powershell
# uv 未安装时，可只安装到项目工具目录：
python -m pip install uv==0.13.6 --target .tools/uv
./scripts/setup-python.ps1
# 首次构建复制并编辑；已有 local.props 不要覆盖：
Copy-Item local.props.example local.props
./scripts/build.ps1
~~~

pyproject.toml 固定官方 MCP SDK 2.3.0、Pydantic 2.12.5 和异步等待使用的 httpx2 2.16.0；uv.lock 固定传递依赖及哈希。锁文件沿用清华 PyPI 镜像。setup 使用 frozen 锁文件，缓存及 .venv 均在项目内。SDK v2 正式入口名为 MCPServer，与 PawnPilot wire 版本分别管理。

## 启动与配置

先单独启动 broker；第一次在项目 artifacts 创建随机密钥：

~~~powershell
.\.venv\Scripts\python.exe bridge/server.py --config artifacts/session.json
~~~

仅监听 127.0.0.1:18765。密钥和同目录 broker-journal.json 必须保留、不得提交。拒绝错误鉴权、浏览器 Origin、错误 Host、超限/非法 JSON。客户端不使用系统代理或 HTTP 重定向。

游戏安装、GUI、测试存档操作须先授权，按[实机计划](docs/m1-live-test-plan.md)执行：

~~~powershell
# 默认预览；批准后追加 -Apply。游戏须先正常退出。
./scripts/install-mod.ps1 -GameRoot 'E:\SteamLibrary\steamapps\common\Probably Stolen'
~~~

只安装 DLL、manifest、session.json，并保留清理收据。游戏内正常启用，批准 network 与 reflection-external 后重启，不使用开发者信任模式。报价反射适配器只读取两种已确认行闭包的商品引用；面板观察另只读原生已构建的 mode 字段，不调用闭包或写字段。

将[配置示例](docs/codex-config.example.toml)的表合并进项目 .codex/config.toml，填写绝对路径后新开 Codex 会话。不覆盖全局配置。stdio 命令为项目 .venv\Scripts\python.exe -m pawnpilot_mcp，cwd 为仓库；可用 PAWNPILOT_CONFIG 指定密钥文件，端口仍固定。broker 不在线也可注册工具、返回结构化不可连接状态。

## MCP 契约

| 工具 | 行为 |
|---|---|
| health_check | 无参数；连接、兼容性、loaded/ready、阻塞原因；health_check.can_act 表示已连接、就绪、broker允许写入且存在当前合法动作 |
| observe | 无参数；快照、legal_actions、等待/人工处理原因；失联快照是历史数据 |
| act | schema_version 复制最新 observe.action_schema_version（2 或 3）；command_id、action 必须逐字匹配当前快照 legal_actions，arguments 遵循该动作形状，并携带 broker_epoch、expected_world_epoch、expected_revision、expected_version；提交后返回状态 |
| get_action_status | 原 command_id；规范状态、before/after、原 broker epoch、人工对齐证据；not_found 不证明执行失败 |
| wait_for_update | 只读；等待 revision/身份改变、指定命令终态或基础可操作状态；最长 30 秒，可取消 |

参数和返回结构都有 JSON Schema。业务错误结构化返回，区分 broker、protocol、connection、configuration、internal。日志不污染 MCP stdout。[Agent Skill](.agents/skills/pawnpilot-player/SKILL.md)规定一次一个动作、未知先查询、人工暂停不得自行恢复。

版本字段不要混用：MCP响应外层 `schema_version: 2` 是响应格式，`business.details.schema_version` 是业务数据格式。`health_check` 和 `observe` 的顶层 `action_schema_version` 才是动作wire版本，等于已连接快照的 `snapshot.state.schema_version`；没有连接/快照时为null。提交前复制最新 `observe.action_schema_version` 到 `act.schema_version`，并继续使用同次快照的身份、版本和合法动作。旧字段及wire2/3校验保持兼容，不自动升级错误请求。

诊断 CLI 的 close_menu/reconcile 必须显式传入与核对快照一致的 --schema-version 2 或 3；不自动猜测或降级。schema_version 是 wire 结构版本，不是能力授权。v1 wire 请求明确拒绝；历史 v1 journal 先备份再迁移，v1/v2/v3 查询保留原证据。broker_epoch 每次 broker 启动改变；session_id 是 Mod 进程身份；同会话禁止混用 wire 版本。world_epoch 在场景切换、载入 Hook 或存档身份改变时更新；revision 随观察快照变化递增；version 必须等于 world_epoch:revision。

loaded 表示当前游戏场景中存档已初始化；ready 额外要求必要 UI/库存组件存在并稳定两个 Unity Update 帧。动作前再次核对身份和状态。loaded=false 时现金 0 是无效占位。两个 can_act 字段作用域不同：health_check.can_act 表示已连接、就绪、broker允许写入且存在当前合法动作；observe.snapshot.can_act 保留 broker 门禁可用性，具体合法动作以 legal_actions 为准。当broker门禁可用但legal_actions为空时，前者false、后者true；不能据此构造未列出的动作。

初始化 ready 不证明所有业务/UI 同步。v3 新 business.details schema 2 提供当前客户与可见对话/选项、分容器直接成员、报价字段以及短期 opaque refs。`front_collection_fingerprint` 绑定 prepare 的完整前柜；顶层 purchase `item_refs`/`collection_fingerprint` 绑定非 owned 后柜中的实际购买子集，完整 back 仍包含 owned 项。`VisibleQuoteBinding` 使用已启用、受限的 normal reflection-external 只读路径，从已核实的 callback closure public item 字段绑定 whole-purchase subset；普通/批发报价分开读取，行级 `item_ref` 在无法无歧义绑定时可为 null。实机已分别读到普通/批发价119/93、298/224和最终成交报价444/338。绑定范围限已支持的 UI/callback 形态。

`GamePurchaseActions` 已接通 legal-action 生成、即时原生复核、单次 OnBuyAllClick 和 settlement verifier。容量模型采用真实 occupied grid mask，既有物品不受类型白名单限制；待购物品限已支持的简单接收 profile，允许已核实的默认 onSlotAdd、现有 features 和无 childElement 的空窗口，非空复杂子窗口等仍不支持。购买前后用进程内对象身份核验13件转移及ownership变化，不因公开引用重发或正常顾客离场误判。跨帧等待不停止心跳。

普通 SELL 支持 scavGeneral、shadyPharma 和 food_scav_story；买入实机成交证据仍为 M2 的 scavGeneral，原始 M3-A 验证了食品卖家的正常拒绝。普通 BUY 支持 spacerFood / spacerFoodStory 的单件卖出，原始 M3-A 成交样本为后者的第一位食品买家。advance_dialogue / choose_dialogue_option 已接入；M2 三次手动推进在执行前因 stale_state 拒绝，M3-A 自动播放场景没有必要手动推进/选项，不能计作这两个动作的成功验收。批发、特殊交易及独立 cancel_confirmation / refuse_trade 仍不支持；结束交互使用当前合法的 dismiss_customer 或正常离场。普通议价见 M3-B，单日日结见 M4 报告。

`business.details.day` 提供当前日期、界面显示的剩余人数、日末可用动作和可见结算条款。只有剩余人数0且无当前顾客等原生条件通过才提供 `close_business`；`open_end_of_day` 经睡袋打开日结，`confirm_end_of_day(settlement_ref,amount,max_spend)` 绑定当前费用和授权，`close_day_report` 只关闭已核验的次晨夜报。服务开关保持原生默认；费用不完整或超出授权时停止。正常跨日不改变存档身份，但会使旧交易引用失效。

`customer.trade_panel` 是可选观察字段：closed 表示关闭，buy/sell 表示已开面板的 NPC 交易方向，unavailable/null/缺失表示未确认。对话结束、顾客到达且原生交互可用时，按当前合法 `open_trade_panel(customer_ref)` 打开；对话或动画期间使用 `wait_for_update`。动作调用原生 OpenUI，后置检查同一顾客、实际模式、trading、正常总价可读和现金不变。已开时不再提供该动作；不因无关全局 UI 未知标记阻止正常开启。空柜台总价0保持 quote.status=unverified、offer_ref=null，后续仍须合法准备和完整报价才能成交。

## 命令状态与恢复

| 状态 | 含义 |
|---|---|
| pending | 已持久化、未交付；到期或状态改变则拒绝 |
| running | 已先落盘再交付，可能已执行；不会再次交付 |
| succeeded | C# 后置条件验证成功 |
| rejected | 未通过门禁/未交付；重新观察后才考虑新动作 |
| failed | 仅历史 v1 结果；新动作调用后异常归入 outcome_unknown |
| outcome_unknown | 结果未知，阻塞新的写动作，只能查询原 ID |

同 ID 同完整内容返回已有状态；内容不同明确拒绝。HTTP 超时或提交 5xx 按未知处理，查询原 ID，不能换 ID 重试。

journal 使用独占进程锁、同目录临时文件、flush/fsync 与原子替换。命令先落盘后交付，结果先落盘后确认。重启 pending 转 rejected，running 转 unknown；原 Mod 保留结果可按原 epoch/session 归档，不重放。已完成结果跨重启可查。上限 4096 条/16 MiB，满载、损坏、写失败停止写入，不淘汰 ID。此方案不宣称 exactly-once，不能抵御人为删除记录或断电磁盘损坏。

若 Mod 退出使结果丢失，不能删除 journal 清零。人工恢复仅允许 close_menu，以及 wire3 的 open_bargain_panel / close_bargain_panel；议价选择、成交等业务动作不允许此恢复。操作者核对当前世界、正常关闭菜单、退出旧 Mod 并建立新会话，重新 observe 并记录画面证据。原命令最迟可能执行期限已过、快照新鲜就绪且版本前进后，才可执行：

~~~powershell
python bridge/client.py reconcile --schema-version 3 --id '<原ID>' --broker-epoch '<当前broker>' --world-epoch '<当前world>' --revision '<当前revision>' --expected-version '<当前world>:<当前revision>' --operator-note '<人工核对世界、菜单及证据位置>' --confirm-no-replay
~~~

将示例占位符全部替换为刚观察到的真实值，revision 必须是整数，expected-version 必须与同次观察一致。该入口不暴露给 MCP；保留 outcome_unknown，另存人工对齐证据后解除门禁，不声称原动作成功。迟到有效结果仍可归档；旧 ID 不会重放。无法核对则继续阻塞。

## 验证与清理

~~~powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet run --project tests/PawnPilot.CoreTests/PawnPilot.CoreTests.csproj -c Release
.\.venv\Scripts\python.exe -m unittest discover -s tests -p 'test_*.py' -v
# 真正 Codex CLI，默认只读，需要已登录账户：
python scripts/verify-codex.py --label read-check
# v3 经营观察验收（仅在本轮明确授权的独立测试槽中）：
python scripts/verify-codex.py --business --label business-read-check
# 已批准测试槽内的有界经营验收（最多六位顾客、只买一次，金额不超过明确上限）：
python scripts/verify-codex.py --business-stage FLOW --max-spend 550 --label business-flow
# M3-A 已授权测试槽；跨多次运行由调用者累计预算，续跑用 --skip-sort：
python scripts/verify-codex.py --m3a FLOW --max-customers 6 --max-writes 24 --label m3a-flow
# 面板专项：需单独批准测试槽，禁止商品搬运/买卖/整理，成功开面板后停止：
python scripts/verify-codex.py --trade-panel --max-customers 4 --max-writes 16 --label panel-check
# M3-B：另行批准普通议价/交易和支出；每次运行最多一次提议、一次成交：
python scripts/verify-codex.py --m3b --max-spend 550 --max-customers 4 --max-writes 24 --label bargain-check
# M3-C：另行批准测试槽/一次性测试触发；此阶段遇到接管即停止，不能购买或自行恢复：
python scripts/verify-codex.py --m3c HANDOFF --max-customers 4 --max-writes 16 --label inspection-handoff
# 仅在用户本会话明确“检查完成，继续”后，复制刚确认的真实ref并按剩余预算运行：
python scripts/verify-codex.py --m3c RESUME --confirmed-handoff <真实ref> --max-spend 550 --max-customers 0 --max-writes 8 --label inspection-resume
# 也可使用 --business-stage A/B/C 分阶段，C 必须提供 --max-spend。
# 仅用于已批准的独立测试槽、正常打开的 ESC 菜单：
python scripts/verify-codex.py --label live-check --close-menu
~~~

先构建 managed Release 测试程序，Python 端到端测试会通过它运行测试专用跨帧模拟。验证脚本使用临时 CLI 配置，只对本次已授权的 PawnPilot 调用自动批准，不改全局设置。默认模型是此前账户可用的非 Astra gpt-5.6-luna，可用 --model 指定其他可用非 Astra 模型。基础证据位于 artifacts/m2b/codex，--business 证据位于 artifacts/m2cd/codex，--m3a 证据位于 artifacts/m3a/codex，--trade-panel 证据位于 artifacts/m3a-panel/codex，同名标签不覆盖。多次运行需累计槽位预算，不能将参数当作新增授权。Windows 沙箱若拒绝 SDK stdio 管道（WinError 5），在正常终端运行，不能把沙箱失败算 PASS。

历史 M2-C/D 自动化为 **317 managed + 135 Python = 452通过、0失败**。真实 MCP 已执行 start/receive/prepare/dismiss/purchase；最终正常报价444（另示批发338），cash550→106、back14→1、player14→27，顾客离场后 receive_next_customer 可用。两个测试槽save_8/save_9已通过UI删除，Mod停用并按收据移除，游戏和broker已退出；原save_0、两份历史备份及游戏程序集哈希不变。故障/去重/未知不重放验证来自自动化，没有实机经济故障注入。详见 [M2-C](docs/m2c-implementation.md) 与 [M2-D](docs/m2d-implementation.md)。

结束时在游戏 UI 删除本次测试槽、禁用 Mod、正常退出，再按收据清理：

~~~powershell
./scripts/install-mod.ps1 -GameRoot 'E:\SteamLibrary\steamapps\common\Probably Stolen' -Action Remove -Apply
# broker 终端 Ctrl+C；保留配置及 journal 用于后续结果查询。
~~~

清理只删除收据哈希匹配的三个文件和空目录。原存档、其他 Mod、游戏本体不改动。缓存、密钥、journal、日志、artifacts 中的截图和备份、存档、调查副本均由 Git 忽略。

## 版本管理范围

上传源码、测试、构建/安装脚本、依赖锁文件、配置示例、Agent Skill 和 docs 中的脱敏验收摘要。原始实机证据与备份保留在本机 artifacts；游戏程序集、local.props、真实 session.json、broker journal、虚拟环境及工具缓存不上传。本机 .codex 配置也忽略，使用 docs/codex-config.example.toml 分享配置模板。

图片和压缩包不按扩展名一概忽略，便于后续纳入合法项目资源；测试截图、下载包和发行产物应放入 artifacts、.tools 或 dist 等已忽略目录。不要使用 git add -f 上传这些本机资料。
