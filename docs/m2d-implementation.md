# M2-D 基础动作与购买验证

当前状态：**PASS（本轮限定的基础普通买入闭环）**。2026-11-01，真实 Codex CLI 经 PawnPilot MCP 完成开门、接待、商品准备、读取普通报价和一次购买；现金550→106，13件商品入库并变为自有，顾客正常离场，恢复到可接待下一位的状态。手动推进对话与选项选择仍缺成功实机样本，不将自动播放算作这两个动作的验收。能力按当前 legal_actions 独立开放，不代表完整自主经营或所有顾客均受支持。

## 动作和统一执行器

复用 ActionGate 的 Accept → Advance → Acknowledge 与 ActionDriver。游戏对象由 Unity 主线程访问，保持单写入、命令去重、世界/状态版本检查、before/after深拷贝和旧版本兼容。原生调用前失败可 rejected；调用后异常、超时或效果不完整归为 outcome_unknown，不自动重放，ACK不解除unknown写阻塞。

| 动作 | 原生入口 / 条件 | 实机结果 |
|---|---|---|
| close_menu | 原有关闭菜单路径 | 保留M2-B已验收能力，本轮没有用它代替经营操作 |
| start_business | StoreShutterButton.OnClick；MORNING→OPEN，等待卷帘锁解除 | MCP succeeded |
| receive_next_customer | StoreBell.OnClick；OPEN、无客户、无离场/自动叫号等待 | MCP succeeded，顾客实际到达 |
| advance_dialogue | DialogUIManager.OnAdvancePressed；区分skip/next，绑定当前对话和typing/manual-wait | 已接入；三次尝试均stale_state、未进入原生动作，成功样本尚缺 |
| choose_dialogue_option | 当前可见、有文本、可交互普通选项 → DialogChoiceBubble.OnDialogChoiceClick | 已接入；未遇到需要选择的实机样本 |
| prepare_purchase_items | StoreTransferButton.OnClick；绑定完整front集合 | 多次MCP succeeded；最后一次front14→0、back0→14、player保持14、cash保持550 |
| purchase_visible_offer | NegociationUIManager.OnBuyAllClick；即时复核普通报价、集合、现金及受支持容量 | MCP succeeded / purchase_settled，普通价444购买13件 |
| dismiss_customer | StoreDismissButton.OnClick；已到达、trading、无对话/锁/已知modal，无特殊intercept/dismiss callback | MCP succeeded；正常可遣走客户不套用购买SELL白名单 |

cancel_confirmation、独立refuse_trade仍unsupported；正常结束交互使用原生dismiss或等待成交后的正常离场。批发、议价、卖出、特殊顾客、日结与多日经营不在范围内。没有Shell、桌面输入、任意方法调用或直接现金/库存/所有权写入。

普通购买、准备和对话支持SELL scavGeneral / shadyPharma；本轮实际成交为scavGeneral，shadyPharma尚未单独实机成交。开门、叫号、遣走检查各自原生条件，不依赖购买白名单、报价完整或通用UI证明。

## 购买证明范围

VisibleQuoteBinding在正常Mod reflection-external权限下，只读取本版两种已核实UI行闭包的public item字段，不调用闭包或写字段。将当前可见行对应到真实purchase subset；普通footer与批发footer分开。行级item_ref/amount可为null，整单集合和正常总价仍必须精确绑定。完整back可含owned报纸，purchase subset只包含原生按钮实际选中的非owned项；prepare使用独立front指纹。

容量采用玩家库存实际allowed/occupied mask，既有物品可为非矩形，无类型白名单。待购项按原生倒序、逐件row-major first-fit模拟；目前限units=1、已知形状、未旋转/翻转和受支持接收条件。不支持堆叠、passthrough、未知自定义回调或非空复杂子窗口。允许已核实的默认GameItem onSlotAdd，现有features列表不再作为统一阻断。contentWindow与examineWindow均无实际childElement时视为空窗口，不读取隐藏内容。

GamePurchaseActions执行前重新解析世界、顾客、offer、集合和进程内GameItem身份，复核cash/max_spend、实际正常报价、native locks和当前容量。单次latch与consumed-command集合阻止本执行器再次进入同一购买调用，不宣称跨任意外部破坏的exactly-once。

PurchaseVerifier保留动作期间真实对象关联，不要求ownership改变后的公开item_ref字符串不变。成功必须同时满足准确扣款、全部目标商品从back转至player并owned、数量一致、非购买back/player成员留在原容器且数量/归属不变；额外未知成员或部分效果不能成功。正常统计、对话、顾客离场不作为失败条件。经济结算后由Agent观察并等待后续状态。

## 真实 MCP 验收

真实Codex CLI经官方MCP stdio实际调用health_check、observe、act、get_action_status、wait_for_update。Computer Use只负责启动、独立槽准备、查看证据和收尾；开门、叫号、搬运、遣走、购买均由MCP提交。诊断HTTP读取仅辅助核对。

最终记录：本机忽略目录 artifacts/m2cd/codex/finish-flow-4.jsonl 与 finish-c-1.jsonl。

1. FLOW-4的start/receive/prepare均succeeded；读取普通顾客已显示的对话，等待其自然结束。搬运14个对象，其中1个为自有报纸，实际购买集合13件；普通总价444、批发总价338。
2. 首次出现合法purchase时暴露MCP ObserveReply顶层动作枚举漏项，observe返回错误，此时尚未购买。补齐枚举并通过回归后，保持同一游戏/broker，重新观察进入C-1。
3. 购买命令 `finish-c-1-C-7f8f3f7e-7a0d-4e12-9f2d-4b0c8a5d1e63` 返回 **succeeded / purchase_settled**。world为036aad8718eb4749aa47f3a4aee4e68f；结算revision12→13，cash550→106，back14→1、player14→27、front0→0。
4. 原13个待购引用item-36…item-48的对象全部入库并owned，公开引用更新为item-51…item-63。原玩家14个对象保留，back只留owned报纸item-17。内部对象身份验证证明转移，不只依赖数量或现金。
5. 成交后普通对话自动播放，MCP wait/observe至revision18：customer absent、cash106、legal_actions含receive_next_customer。没有第二次购买，也未接待下一位。

第一个验收点的开门/接待和可见对话观察通过，但不能声称advance/choose已成功实机执行；本次普通流程不需要手动推进/选项。第二验收点（搬运与报价）及第三验收点（购买、核验、可继续经营）通过，按任务第18节标记本轮闭环PASS，保留上述动作级验证缺口。

较早样本普通/批发价包括119/93、298/224、340/259、110/84、226/181。三次dialogue stale拒绝、一次商品仍在生成时的prepare stale拒绝均未进入原生调用；一次CLI参数包装错误在MCP输入校验阶段拒绝。未重试可能已产生效果的购买。empty-window/default-callback/features限制经本版原生逻辑核对后修复，没有把验证布尔值全部设为true。

## 测试与清理

**317 managed + 135 Python = 452通过，0失败**。覆盖逐动作条件、混合归属集合、报价绑定、占格/接收范围、preflight、结算身份核对、部分变化、旧引用/版本、去重、未知不重放、等待通信、broker/journal/MCP及历史协议。基础故障结论来自自动化，未在真实交易上注入故障。独立审查未发现阻断缺陷。Release构建通过，仅NU1900离线漏洞源警告。

Windows超限HTTP测试曾因发送请求体与服务器提前关闭竞争失败；改为发送超限Content-Length、无请求体，验证读取前413拒绝，完整重跑135项通过，未放宽生产限额。

本轮授权下仅建立save_8/save_9两个独立槽，未加载原进度。已通过游戏UI删除两槽、停用Mod、正常退出，按收据核验并移除DLL/manifest/session三文件和空目录；broker停止，端口不再监听。save_0及day5/day6备份SHA256、游戏Assembly-CSharp.dll SHA256均与本轮前一致。存档根目录差异为新增游戏日志、Player日志轮转和saves_index；正常Mod启停记录属于已授权范围，未盲目恢复全局设置。

本机证据：artifacts/m2cd/finish/live中的purchase-summary.json、cleanup-summary.json、before/after文件哈希、收据、Player日志和成交/清理截图；自动化日志位于同级finish目录。原始证据、存档、游戏DLL、密钥和journal不提交。代码及文档提交到feature/m2cd-basic-trade，具体提交号以Git记录为准。
