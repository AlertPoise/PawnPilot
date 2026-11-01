# M2-D 基础动作与购买验证

当前状态：**PARTIAL（独立动作基础与验证器已实现，生产业务动作尚未验收）**。新业务动作已有政策/原生适配代码，但生产硬门关闭；正式 MCP 写动作仅 `close_menu`。本轮没有真实 MCP 购买或完整顾客流程。

## 动作和统一执行器

复用 ActionGate 的 Accept → Advance → Acknowledge 与 ActionDriver。v3 参数参与完整 canonical fingerprint；before/after 深拷贝，旧版本保留。调用边界前重新检查；边界后异常、超时、未知效果不重放，ACK 不解除 unknown 写阻塞。

| 动作 | 原生入口 / 实现 | 当前生产状态 |
|---|---|---|
| start_business | StoreShutterButton.OnClick；MORNING→OPEN，并等待卷帘锁解除 | 关闭：UI/运行范围未验 |
| receive_next_customer | StoreBell.OnClick；无当前顾客/无离场和待叫号，等待实际到达与对话 | 关闭：UI/运行范围未验 |
| advance_dialogue | DialogUIManager.OnAdvancePressed；skip / next 分开、绑定对话阶段和回调范围 | 关闭：回调范围未闭合 |
| choose_dialogue_option | 可见活动选项引用解析到 DialogChoiceBubble.OnDialogChoiceClick | 关闭：所选顾客选项/回调未验 |
| prepare_purchase_items | StoreTransferButton.OnClick；完整可见非空前柜集合，前后柜及玩家库存联合差分 | 关闭：目标容量/UI范围未验 |
| cancel_confirmation | 已识别 typed confirmation 的 CancelButtonPressed / OnNoClick | 关闭：取消回调未验 |
| dismiss_customer | StoreDismissButton.OnClick；不允许未知 intercept、pending endAction 或 autoCallNext | 关闭：回调范围未验 |
| refuse_trade | 与取消/关闭平板/遣走保持不同语义 | 未支持，不以关闭平板代替 |
| purchase_visible_offer | 独立 preflight、容量模型、PurchaseVerifier 与一次调用 latch | 关闭：Q/C/V 未完成 |

候选不进入 legal_actions；业务生产开关是编译代码内的 false，没有 wire/config/environment 开启口。未增加任意方法、桌面输入、Shell 或直接库存/现金/所有权写入。没有自动 close_menu 恢复策略。

## 购买证明范围

CapacityProof 使用不可变、带来源/目标/世界/版本/完整顺序绑定的输入。纯模型只处理已声明 vanilla profile 下的窄 1×1、unit=1、非堆叠、无 feature/嵌套/未知回调范围，按原生购买倒序模拟。未确认 profile、规则、形状、锁、回调等一律 unverified；没有通过真实搬动再回滚来试容量。模型 sufficient 是条件数学结论，尚非本轮运行时接收保证。

PurchaseVerifier 独立保存原始交易的 immutable evidence，核对正常完整报价、资金与独立授权上限、完整集合、容量绑定，以及前后现金/物品/归属/来源目标/额外变化。客户逻辑引用可以在完整结算后清空，不能以自动离场作为成交证明。后续对话与购买结算分开，避免等待必须由第二个动作推进的事件。

当前没有把 verifier 接入生产经济 dispatch：本轮 Q/C/UI 尚未闭合，未获得可用于实时授权与完整物品证据的可信运行时生产者。模拟通过不等于首笔真实 MCP 成交。

## 测试、独立审查与实机

最终自动化 **276 managed + 127 Python = 403 通过、0 失败**。覆盖纯观察/动作参数、完整与部分搬运、资金/授权/报价/容量拒绝、集合顺序、现金与完整库存差分、异常和去重；v3 broker、journal、MCP、跨进程模拟及旧 v1/v2 回归同步更新。独立审查发现并修复容量证据与交易 before 脱离、替换 preflight evidence、原生异常后仍可能宣称成功、snapshot/message 预算混同及 stale references 等问题：现在容量事实精确绑定 before 全集合，latch 消费一次可信授权并绑定同一 immutable evidence，异常保留 unknown。

本轮获批并完成的实机范围及清理见 [M2-C 报告](m2c-implementation.md)。只有正常 UI 准备和真实 Codex MCP 只读观察，**不包含经济交易、故障注入或业务动作启用**。两组报价和复测均现金 550；正常集合搬运前后引用保持不变，但不是 MCP prepare_purchase_items 的验收。一次尝试用铃铛打开确认框，实际直接遣走并叫下一位，取消测试未通过；没有继续重复该入口。这个差异也说明 receive/dismiss 必须保留分开的前置条件。

真实验收：**Level 1 未执行；Level 2 未执行；Level 3 未执行**。没有发生真实成交、重复成交或未知经济结果；unknown/重复命令结论只来自自动化，不能算经济实机可靠性通过。

最终 Gate：Q PARTIAL（行绑定、普通总价、混合 owned 后柜）；C PARTIAL（真实规则与运行时容量证据未接入）；V PARTIAL（纯联合验证器已测试，真实不可变证据采集及生产经济入口未接通）；UI PARTIAL（全局输入/遮挡和回调范围未证实）。不以代码存在宣称能力可用。拒绝交易仍 unsupported，其他表列新动作均关闭。没有调用 OnBuyAllClick，也没有为取得一次成交而绕过门禁。

本轮两个槽均删除，Mod 停用并按收据清理，游戏及 broker 退出，原有三个存档和游戏程序集哈希不变。提交至 `feature/m2cd-basic-trade`，只发布源码、测试和脱敏文档，不发布 DLL、存档、密钥、截图或运行日志。
