# M5-A：候选准入修复与柜台机制静态核验

## 1. 结论、范围与基线

**M5-A 项目内实现与静态调查 PASS；未经新的 Unity/MCP 实机验收。** 本轮只修复生产候选发布边界、增加回归测试并形成 M5-B～D 设计。没有实现通用剧情执行、声望运行时、报警、扫描、武器、电话或夜间外出。没有启动/关闭游戏、安装 Mod、读写存档、使用 Computer Use、push、PR 或 merge。

实际仓库保持 `D:\c\aaaprojects\PawnPilot\PawnPilot`。开始时工作树干净，本地 `feature/m4-single-day` 与远端同名分支均为 `ef6ca679dd853b0a8804cf547975ae90a40ed283`；新建 `feature/m5a-foundation-research`，没有改写 M4 历史。远端检查在默认网络沙箱中失败，经允许的只读网络访问复核成功。

目标安装目录为 `E:\SteamLibrary\steamapps\common\Probably Stolen`。Steam app 4348910 / build **26337366**；对应既有 0.46D / VERSION 100-Rev6。研究前后 `Probably Stolen_Data/Managed/Assembly-CSharp.dll` SHA-256 均为：

`6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5`

因此本次 DLL 证据与 M4 同版本。本地化另从该安装的 Addressables string tables 只读提取，English SHA-256 `45050F70E72873429B310273EF220DA7284ADD019700AEB9F377D812E30378F2`，简中 `440EA10221C410345528134AB2B9337C94E2CCF018BAD5EA574BF7035E032854`，shared keys `F9474C775BBF07CF866ADC6A9B435672C9CA42C0A1147B7B128C5D6914A0A6A3`。47 项特权的名称/描述 key 均命中；正文只保留名称和自行概述的机制。

方法定位采用 **类型.方法 + MethodDef RID**，例如 `StoreReputation.OnItemTraded (0x1869)` 对应完整 token `0x06001869`；字段会另标 FieldDef。token 仅对指定程序集有效。保存链另查同目录 `Assembly-CSharp-firstpass.dll`（SHA-256 `D2EAAD799C95F8DEAAC26F2E866B8FC07B833DEBAE01BBE0600A7B3C3734C164`），相关token在正文明确标为firstpass。研究使用 Cecil 读取元数据/IL、隔离 UnityPy 读取资源，没有加载执行游戏方法；不能仅凭 getter 名称假定安全。

证据等级：`STATIC_CONFIRMED`（目标 DLL/资源证明）、`LIVE_CONFIRMED`（同版本已保存实机观察）、`GUIDE_ONLY`（仅外部资料）、`PARTIAL`（局部调用已证明，条件/参数/场景仍未闭合）、`UNKNOWN`（无可靠实现证据）。本文已核对的代码事实默认 `STATIC_CONFIRMED`；推断和例外单独标出。M4 的可见快照可以算历史实机观察，不能使本次新机制自动成为 `LIVE_CONFIRMED`。

## 2. A1：统一候选发布边界

### 已确认问题

调用链是主线程 `ProbeBehaviour.Capture` → customer/UI/collection/quote/day projection → 各原生动作生成器 → inspection gate → revision/world 绑定 → HTTP exchange → Python `valid_snapshot_v3` → Broker 当前快照合法动作绑定 → MCP。命令执行仍会由 `ActionDriver` 重新捕获并核验。

M4 `GameInventoryActions.BuildLegalActions` 只证明原生可整理，没有统一复用 business 日阶段门禁。`day.transition_pending=true` 的卷帘状态不保证存在命名 UI blocker，故能构造“原生可整理，但同一快照协议禁止”的候选。**这是确定的代码缺口，不证明历史每一次 HTTP400 都只有这个原因。** 报告/结算通常还被现有 UI 门禁挡住，不能把每个理论组合都写成已发生的 Unity bug。

审查还发现原生 sale/purchase 等生成器不能独自证明 Python 的全部 DTO 约束；旧 C# `ActionGate.IsSnapshotValid` 偏重形状，Python 对 legal_actions 绑定更严格。不能靠放宽 Broker 或改写历史记录解决。

### 本轮实现

- 新增 `Runtime/CandidateAdmission.cs`。`Validate` 在同一 snapshot 上复用 `BusinessActionPolicy.ValidateCandidate`，补齐 schema 2、接待时客户 phase、对话 typing/wait、准备买入的 front 顺序、准备卖出的 back/quote、整理/出售 UI 状态、整理的客户及店铺阶段、日结金额上限等跨层约束。
- business、inventory、sale、bargain、day 生成器通过 `TryAdd` 发布。已被正常门禁拒绝的原生候选保留 `candidates.status=blocked` 和原因；`candidates` 仍只是说明，不能用于执行。畸形参数抛错，不伪装成正常阻塞。
- purchase 必须先通过原有原生报价/容量检查，再由 `TryAddVerifiedPurchase` 写定最终 capacity/eligibility 后校验；不以“先禁止购买的中间 DTO”误拒正常购买。若最终准入拒绝，`purchase_enabled=false` 并保留原因。
- inventory 原生捕获还要求当前 phase 与 snapshot 一致、当前客户实例为空，防止原生与 DTO 短暂错位。
- 普通 `close_menu` 的发布和 `can_close_menu` 同步应用日过渡/报告/结算门禁；`close_day_report` 仍保留独立日结策略，不受普通 UI 全局禁用误伤。
- `Capture` 最后调用 `EnsureValid`，检查最终形状、每个候选和 purchase eligibility 对应关系。发现内部矛盾就拒绝发布整份快照，**不删除错误 legal entry 后继续声称正常**。Poll 本轮准备失败不会发 exchange，之后重试；不是永久终止轮询。执行中捕获失败沿现有驱动失败边界处理，已调用但未核验的结果仍为 unknown，禁止重放。
- 修正 `GameBusinessActions` 既有 `(int)clientIntent==5` 的错误 LEAVE 注释，使用等价枚举 `DIALOGUE`。本版 enum 没有 LEAVE；既有议价离场 ID/结果白名单未扩大。

未修改 Broker、MCP、journal、wire schema、执行金额/库存/容量验证或普通剧情白名单。wire v2/v3 旧记录保持原 schema、fingerprint 与含义；command ID、单写、旧引用失效、人工检查确认恢复和 `outcome_unknown` 阻塞继续生效。

### 新增验证

新增 39 个 managed 检查（总数 554→593）和 5 个 Python 测试（197→202）。生产 helper 导出 **23 组同一份 JSON**，Python 对它们依次跑 JSON Schema、`valid_snapshot_v3`、Pydantic `GameStateV3` 和真实 Broker exchange。正常动作必须仍存在，避免“全部清空所以全通过”的假修复。

覆盖卷帘 OPEN+transition、未知过渡、settlement、next_morning/report、客户离场/旧 choice、对话与其他 blocker、无命名 blocker 但 UI blocked/updating、正常 MORNING/OPEN 整理、sale/dialogue/purchase 和 day-report close、ESC menu 日阶段一致性、畸形/重复候选、最终坏快照不可修补、调用后捕获错误 unknown 不重放。原始 sort+transition 非法快照仍被严格 Broker 拒绝。原生错位的 DTO 侧有模拟覆盖，Unity 实例切换时序仍待实机。

## 3. A2：按交互机制核验特殊剧情

### 通用对话生命周期

`DialogUIManager.StartDialogue (0x24ac)` 设置当前行并启动渲染。`OnCurrentTextDisplayed (0x24af)` 可排队 endAction；有 choices 时延迟显示按钮，不必先进入 manual-wait。`DisplayChoice (0x24b8)` 把当前选项文本放入 TMP 并设置 enabled。`DialogChoiceBubble.OnDialogChoiceClick (0x1c8a)` 的 type 0 路由主对话，type 1 路由电话；没有 type 2 路由，type 的实际 prefab 赋值不在 DLL 中。

`SelectChoice (0x24bb)` **先执行所选 callback**，再换行、隐藏按钮并安排后续 conversation；`ContinueConversation (0x24b4)` / `FinishConversationNow (0x24a7)` 也可能执行 endAction。因此“推进一行”“点击选项”“关闭面板”都可能有经济或剧情副作用，不能自动套用普通对话的现金不变后置条件。生产入口应复用正常控件的受限路由，而不是公开任意 callback/反射执行。

当前 Reader 只读当前已渲染文本、可见且可交互的主对话选项；没有 typing、manual wait 或已呈现 choices 时输出 `updating`。原生排队显示 choices 的间隔确实可出现这个状态，但 M4 连续等待后仍无选项的原因不能仅用延迟解释。必须区别：可见控件漏读、受支持组件尚在推进、未支持的面板、普通交易策略拒绝。`trade_type=other` 只表示不是原生普通 BUY/SELL，不表示“没有对话”。

### 绿色名片与特殊合作

本地化 `item_card_rev_name` 精确对应 **Business Card (Green) / 名片（绿）**，`MiscItemDirectory.CardRev (0x0519)` 使用 sprite `card_rev`；Joe 的 `item_joe_card_name` 则为“名片（乔·王尔德）”。结合调用链，M4合作事件高置信度对应革命军商人介绍 `StoreClientListWanted.CreateWanted4 (0x1b18)`，不是Joe网络升级教程。内部wanted身份只用于本次研究定位，不应直接暴露给生产Agent替代玩家判断。

该工厂以 `DIALOGUE=5` 初始化八句主对话，**没有 AddChoice、特殊数量框或打开交易面板**。`HandleSkipClient (0x1c44)` 在skipIntro且day=2时安排它；M4第2天、台词列出的武器/弹药/护甲/毒品/物资箱/通行卡，与后续 `CreateWanted4Normal (0x1b19)` 的收购标签相符，前台随后出现精确同名绿卡。M4 DTO未保存原生客户ID，因此这是有多项证据的场景归属推断，不是实机身份直接观测。

首句 `SetStartAction` callback `0x55ae` 把初始 `dismissable=false` 改为 **true**；不能只读工厂初始赋值就断言不能遣走。第八句 endAction `0x557a` 把 `card_rev` 加到前台桌，并把RevMerchant电话状态设为4，**没有自动离场**。`EndConversation (0x24b6)` 清pending、隐藏对话/choices、令currentDialog=null，同样不移除客户。普通玩家可以通过 `StoreDismissButton.OnClick (0x1f1c)` → `TryPlayerDismissCurrentClient (0x1777)` → `DismissCurrentClient (0x1778)` 完成收尾；该工厂没有自设tryInterceptDismiss。可见按钮在现场是否被其他UI遮挡仍待实机。

这条链提供了比“对话还在渲染”更具体的解释：**对话已结束，但特殊DIALOGUE客户仍等玩家遣走**。Reader在currentDialog=null且无交易面板时投影为`updating/unknown`，无法表达“已结束、可正常遣走”；而动作策略要求受支持普通客户且phase=trading，故不会提供dismiss。M4末态正与这一机制相符，但历史DTO不足以排除所有其他UI条件。结论是已证明的状态表达/能力缺口，加上高置信度现场解释；不能声称一次按钮漏读已经实机复现。

卡片自身的可用操作 callback `0x40d4` 调 `PaperUIManager.OpenCard("card_rev", null)`。后续电话路径 `RevMerchantPhoneDialog (0x193e)` 的yes callback `0x52cd` 使用服务并排队两天后的 `wanted4Normal`，no callback告别；后者是BUY客户，这条工厂未设置isOfferWholesale。**留下名片不等于当场出现批发交易UI**，电话和未来客户调度仍在本轮范围外。通用适配方向是dialogue_flow（包括start/end效果）、已结束客户状态、原生dismiss控制和发放物品/服务状态效果核验，而不是新增wanted4 Handler。

另作组件扩展参照、不要混成此事件：Joe的 `CreateNetworkIntroduction (0x1a96)` 的end callback `0x54a9` 打开新版 `WildNetworkUI.Open (0x2936)`，通过 `CustomUIWindow.root` 上的 `WildNetworkFrame` 观察；`StoreUIManager.ResolveEscape (0x281e)` → Close(0x2937) → OnWindowClosed(0x2938) 才接 `WildNetworkContinue (0x1994)` 后续。旧 `WildUIManager` / `CloseUI("network_upgrade")` 是另一套root。这个额外静态链证明native_panel适配器必须观察实际root和关窗后续效果，不能把所有合作面板套一个字符串关闭入口；prefab关闭按钮绑定待实机。

### 离散数量选择与带付款的物品转移

`StoreClientListMinor.CreateRadioactiveWasteCustomer (0x1a56)` 创建 **DIALOGUE=5** 客户，借助普通 `Dialogue.AddChoice` 提供接收 1/3/5 桶，callbacks `0x5451/52/53` 调用 `TakeNuclearWaste (0x1a57)`。这不是数字输入控件，也不是正常 purchase 面板。

该方法先清除柜台所有非自有物品，再逐桶调用 `TryAddDirectSellingItemToTable("nuclear_waste", true, false, false, 0)`；只对返回 true 的桶累计 `accepted`，支付 `100*accepted`，最后无论 accepted 是否为零都让客户离场。string overload `0x17b1` 先问 front `CanTakeAnything`，GameItem overload `0x17b3` 再做 `GraphUtils.TryAcceptAllOrDestroy` 实际放置。因此原展示的五桶非自有废料并不等于已成交库存；选择 5 也可能只接收一部分甚至 0。副作用顺序是清理 → 实际放置 → 收款 → 离场，不能设计为原子“全接收或全回滚”。

没有第四个拒绝 choice。但 `StoreClient` ctor `0x1bd7` 默认 dismissable=true，此工厂未覆盖；正常玩家可经 `StoreDismissButton.OnClick (0x1f1c)` → `IsClientDimissable (0x1768)` → dismiss 路径拒绝，仍受原生锁和客户存在条件限制。现 PawnPilot dismiss 只开放普通客户 trading/无对话等已审范围，故未提供这个特殊拒绝入口。

M4 revision 73 的文本确实显示每桶 100、front 五件非自有废料、现金 540、typing/wait=false、无 options；等待没有证明按钮已被 Reader 漏读。**机制静态确认，M4 工厂匹配为强推断，具体失去可操作状态的 UI 时点 PARTIAL。** 另有 `CreateGeorgioNuclearWaste (0x1b43)` 恢复以前倾倒的废料并重置计数，属于不同事件，不能混为一次数量交易。

所需组件是 `dialogue_choice` + 离散 quantity 语义 + `inventory_transfer`/cash effect verifier + native dismissal。数量与文案可见，真实接收量须以新物品归属、容器差异和现金核验；未来未知经济 callback 在授权/效果契约未建立前保持 unsupported。M5-A 未开放任何新剧情动作。

## 4. A3：通缉与 Aug 的能力边界

| 机制 | 玩家原生路径与结果 | 未来允许观察/适配要求 |
|---|---|---|
| 通缉资料 | `StorePoster.OnClick/PullUp (0x1f57/58)` 展示公告；wanted1..9，`WantedElement.OnEnable (0x1fe6)` 更新展示状态 | 读取当前可见海报头像/文字/标识，结合当前客户的可见外貌建立证据关系；不能用内部 wanted ID 或 realName/SetWanted 真值直接匹配 |
| 报警长按 | `StoreSecButton` 实现 IHoldClickable，通常 hold 0.4 秒；`ShouldRequireHold (0x1f70)` 根据 instantCallSecurity、客户、reported/arrestable、SEC_ALARM_IGNORED 决定；`OnClick (0x1f76)` 再检查并调 `OnCustomerReported (0x17de)` | `hold_control` 需要按下、持续时间、释放/取消、世界切换取消和幂等提交；不能直接跳过长按调用报警业务函数 |
| 报警异步完成 | `OnCustomerReported` 可能被 interception/确认 UI 拦截，另有 deferred callback 在 `StoreUIManager.OnGenericLeft (0x2840)` 等后续流程执行 | 输入提交、人物离场、治安对话和奖励分别核验；不能按按钮返回就写“举报成功” |
| Aug 判断 | 本版类型/工具使用 Aug/AUG；可见线索包括外观/身份展示不一致、idle freeze/seizure 等表现，但单条线索不证明身份 | 可呈现观察到的异常和置信度；隐藏 `isAug`、tier、生成/随机抽取结果不得进入 Agent DTO |
| Aug 扫描器 | `MiscItemDirectory.AugScanner (0x053f)` 创建工具；closures `0x412f/30/31` 限制使用并消耗 battery 25、设置扫描状态；`AugScanner.CreateTooltip (0x1309)` 展示 ready/detected/no-Aug 结果 | `inventory_tool`/可见 tooltip 适配器绑定工具、客户和电量；只公开实际展示的检测结果，不把预先读 hidden truth 当扫描 |
| 柜台武器 | `Shotgun.OnClick` → `StoreUIManager.EnterShootingMode`；`Arm.Update` 处理左键开火/右键取消；WoundAnimation/BodyFall → SEC shootout dialogue → `AugHelper.OnGoodKill/CleanupKill (0x0998/99)` | 需要模式进入、瞄准/目标、取消、单次击发和动画结束观察；授权等级独立于普通点击。这里只调查，未实现、未授权执行 |

正确通缉报告 callback `0x526b` 给 Security **基础 +5**，现金来自客户 reportReward 并受特性修正；客户派系还可能按 `reportImprovesFaction/reportDegradesFaction` 发生变化。错报 callback `0x526d` 给 Security **基础 -8**、wrongfulReportCount+1，现金≥100 时扣50；最终声望都还经过普通 modifier。不是固定净值。

Aug 举报先设 `isReportedAug` 再 dismiss，走专门 SEC 延迟对话并绕开普通举报奖励分支；dismiss 链可能执行 `OnAugLeftAlive (0x099a)` 改 Aug presence，它不是派系声望。正确击杀基础 Rev/BM/LL 各 +2、Security +8，奖励现金 `clamp(100+引入后天数,100,250)`，另经 Convict/ModelCitizen、AugMenace 等修正。误杀基础 Rev/BM -10、LL/Security -15，并向 SecData 累计 MANSLAUGHTER crime value：day<25 为250、25～49 为500、≥50 为750；**不是该方法即时扣掉这些现金**。结果跨派系，估算不能只记录一条 delta。个体/剧情例外与异步场景均待以后实机闭合。

## 5. A4：声望存储、阈值与特权

### 数据和显示

`PlayerStore.storeReputations` 是运行时列表；`StoreReputation.InitReputation (0x186a)` 默认注册 `FACTION_LOWER_LEVEL` 下层区、`FACTION_UPPER_LEVEL` 上层区、`FACTION_SECURITY` 治安部、`FACTION_BLACK_MARKET` 黑市、`FACTION_REVOLUTION` 革命军；startType==6（Chemist）另注册 `FACTION_CARTEL` 13区卡特尔。Tourist/Church 的 faction 常量不等于注册的声望。初值0，不能把所有客户 faction 硬编码为六条常驻记录。

`amount` 为 **double**，保留小数，`CheckReputation (0x1862)` clamp 到 **[-200,200]**。`GetReputationExact (0x185f)` 纯读该字段；`GetReputation (0x185e)` `conv.i4` 向零截断。FieldDef `0x0f80` 的 private amount 有 `ES3Serializable`；`PlayerStore.WriteSlotFile (0x1752)` 按 ES3 key `playerStore` 保存，`LoadGame` callback `0x5254` LoadInto；firstpass 中 `ES3Reflection.GetSerializableFields (0x0484)` 收录此 attribute，`ES3Type_double.Write/Read (0x02a7/02a8)` 使用 double primitive。读档后 `AddMissingPerksAfterLoad (0x186c)` 补缺项、`UpdateReputation (0x1864)` 重算解锁。这里只检查序列化代码，没有访问存档。

`ReputationUIManager.OpenUI (0x2753)` 遍历实际列表；`ReputationElement.Init (0x274a)` 数字用 **F2 + /200**，称号及条形图用截断整数，条高公式见 `GetBarHeight (0x2749)`。因此 UI 可见两位小数，而非只显示整数；精确内部 double 仍比玩家显示精细，M5-D 默认保留显示精度，不能把内部精度偷渡成观察优势。

称号由 `GetReputationStatus (0x1885)` 决定，以下为整数区间及同版本简中名称：

| 整数范围 | 称号 | 整数范围 | 称号 |
|---|---|---|---|
| -9..9 | 中立 | -10..-19 | 褒贬不一 |
| 10..19 | 略有耳闻 | -20..-29 | 不受待见 |
| 20..29 | 小有名气 | -30..-39 | 难以指望 |
| 30..39 | 得到认可 | -40..-49 | 形迹可疑 |
| 40..49 | 品质可靠 | -50..-59 | 令人生疑 |
| 50..59 | 受到推荐 | -60..-69 | 惹人非议 |
| 60..69 | 值得信赖 | -70..-79 | 声名狼藉 |
| 70..79 | 有口皆碑 | -80..-89 | 身败名裂 |
| 80..89 | 声名远扬 | -90..-99 | 避之不及 |
| 90..99 | 饱受敬仰 | -100..-149 | 全面抵制 |
| 100..200 | 声威赫赫 | -150..-200 | 黑名单 |

### 特权全表

`CreatePerks (0x186b)` 注册 **45项**：下层10、上层8、治安11、黑市6、革命6、卡特尔4；工厂定义 **47项**。`UL_HITMAN` 和 `SEC_CONTRABAND_TOLERANCE` 有定义/本地化但不在本版正常初始化列表。读档补齐并不等于删除历史多余对象，所以“新局未注册”不能外推为所有迁移存档绝对不可达。

统一生效/失效条件：正阈值 `amount>=threshold`，负阈值 `amount<=threshold`，越过反向边界即重新锁定；0阈值也要求非负，唯 `BM_HONOR` always-active。`IsPerkUnlocked` 在所有实际派系列表中按 ID 搜索，所以消费者不加 faction 判断时可跨派系生效。下表“入口”除工厂外给出消费方法，效果是实现摘要，不是本地化描述承诺。`P`=PARTIAL、`U`=UNKNOWN；未标者为 STATIC_CONFIRMED。夜间/电话/网络升级等只记知识，不增加当前动作。

| 派系 / ID | 阈值 / 游戏内名 | 实现摘要、条件与范围 | 工厂RID；消费入口 |
|---|---|---|---|
| 下层 LLREFERAL | +20 有口皆碑 | 普通客户数抽取成功+1；原生Roll(100) | 188d；StoreClientManager.GetNormalCustomerCount 1c60 |
| 下层 LLRAREGOOD | +40 稀有货物 | 每周余数1/5参与稀有客户生成，仍有抽取/分支条件 | 188f；HandlePerkClient 1c5e |
| 下层 LLTIPS | +60 市井传言 | 晚间关店Roll(4)成功后PickRumorEvent；晚间内容不等于当前可执行动作 | 188e；PlayerStore.OnLateShutterOpen 175d |
| 下层 LLTIGHTLIPS | +80 守口如瓶 | Lower犯罪证据修正整数除2；只针对该派系条件 | 1890；SecData.ApplyCrimeModifier 01dc |
| 下层 LLPILLAR | +100 下层区传奇 | 网络升级cost倍率×0.85；Dr. Jackson非自有待售物品上架加-15%购买feature；升级/服务不在本轮动作内 | 1891；NetworkUpgrade.GetCost 16b5；CreateInventorStorage callback5318 / DiscountBuy15 1e3d |
| 下层 LLPOORREVIEWS | -20 差评 | 普通客户数抽取成功-1；Roll(100) | 1892；GetNormalCustomerCount 1c60 |
| 下层 LLDISTRUSTED | -40 遭到猜疑 | Lower客户提供contraband时拒绝上架并销毁未放置对象 | 1893；PlayerStore.TryAddDirectSellingItemToTable 17b3 |
| 下层 LLTARGET | -60 沦为猎物 | 未投保盗窃抽取基数33加33，再受无保险天数等影响 | 1894；PlayerStore.HandleInsurance 1791 |
| 下层 LLAVOIDED | -80 狗嫌人厌 | PhoneClientType==1电话不应答；电话不在当前范围 | 1895；PhoneUIManager.HandleCall 270e |
| 下层 LLLYNCH | -100 暴民私刑 | 关店设置等待的mob客户；夜间遭遇另需适配 | 1896；OnLateShutterOpen 175d |
| 治安 SEC_REQUISITION | 0 征用令 | day>7且每周余数4安排requisition officer；0不是always-active | 1897；HandlePerkClient 1c5e |
| 治安 SEC_SURPLUS | +20 治安部盈余 | 每周余数3安排surplus商人 | 189f；HandlePerkClient 1c5e |
| 治安 SEC_PROTECTION | +40 治安部庇护 | 未投保盗窃概率基数减16，仍有其他项 | 189e；HandleInsurance 1791 |
| 治安 SEC_TRUSTED | +60 值得信赖 | 调查进度整数除2，检查冷却区间下/上界+2/+4，可知治安行动 | 18a2；SecData.OnNewDay 01c0；ComputeInspectionCooldownRange 1c46；CanPlayerKnowFactionOperation 07b0 |
| 治安 SEC_RELIABLE_VENDOR | +80 可靠供应商 | 所有useClientBudget客户的初始预算+50%，跨客户派系 | 18a0；StoreClient.ApplyBudgetModifier 1bff |
| 治安 SEC_VIP | +100 VIP | 常规inspection-client生成直接返回；亦影响检查预警/保险等，非“一切法律行为免疫” | 18a1；HandleInspectionClient 1c49；HandleInsurance 1791 |
| 治安 SEC_WATCHLIST | -20 重点盯防 | 每日调查进度×1.2，若priority未解锁 | 1899；SecData.OnNewDay 01c0 |
| 治安 SEC_ALARM_IGNORED | -40 应该是风声吧 | 报警按钮直接return；不绕开此原生规则 | 1898；StoreSecButton.OnClick 1f76 |
| 治安 SEC_PRIORITY | -60 头号通缉 | 每日调查进度×1.5，取代watchlist分支 | 189a；SecData.OnNewDay 01c0 |
| 治安 SEC_THOROUGH_INSPECTION | -80 彻底查抄 | 检查冷却上下界各-1再clamp≥1/≥2；到期特定inspection分支 | 189b；ComputeInspectionCooldownRange 1c46 / HandleInspectionClient 1c49 |
| 治安 SEC_IMMEDIATE_ARREST | -100 不择手段 | 关店安排sec等待客户；文案称立即逮捕，实际先进入原生客户/逮捕流程，端到端场景P | 189d；OnLateShutterOpen 175d |
| 治安 SEC_CONTRABAND_TOLERANCE | -100 毫不留情 | **未注册**；描述是更严惩处违禁品，实际效果U，不能列作本版新局特权 | 189c；未找到ID消费者 |
| 黑市 BM_HONOR | 0 盗亦有道 | **always-active**；文案参数1.5与实际普通BM负值×2不一致，以代码为准 | 18a7；StoreReputation.ModReputation 1861 |
| 黑市 BM_SMALL_TIME_BROKER | +20 小牌掮客 | 来访与货物由直接声望公式变化，未见ID gating；实际台阶不等于显示门槛20，见表后 | 18a6；GetBlackMarketClientChance 1c79；GetBMRepStockMult 19be；thief callback5324 |
| 黑市 BM_RELIABLE | +40 销赃信誉 | 每周余数1安排thief，另有Roll(20) petty thief条件 | 18a8；HandlePerkClient 1c5e |
| 黑市 BM_NETWORK | +60 地下网络 | 可知黑市行动；不等于允许Agent参加夜间行动 | 18a3；StoreOperationManager.CanPlayerKnowFactionOperation 07b0 |
| 黑市 BM_DISTRUSTED | -40 黑名单 | BM违禁品上架被拒；BeginDay跳exchange；清洁服务费用×2，**不是禁用清洁** | 18a4；TryAddDirectSellingItemToTable 17b3；BeginDay 175e；StoreService.UpdateCost 1904 |
| 黑市 BM_UNINSURABLE | -60 无法参保 | 保险已引入且非cartel protection时锁服务；加载有introduced修补 | 18a5；StoreService.UpdateCost/HealIntroducedAfterLoad 1904/1902 |
| 上层 UL_DISCERNING_TASTE | 0 品味不凡 | shopper chance=1+max(0,intRep)/5；另需奢侈品展示/冷却/日期条件；0非always-active | 18a9；GetDiscerningTasteChance 188b；ShowcaseHelper 1033；HandlePerkClient 1c5e |
| 上层 UL_TIPS | +20 小费 | Upper客户owned item上架附加Pourboire的+15%价值feature；不是固定额外交易声望 | 18ab；GameItem.OnAddedToWeightedArea 300a；ItemFeatureList.Pourboire 1e27 |
| 上层 UL_CHARITY | +40 上层区施舍 | 冷却允许且IsCharityComing时生成charity客户 | 18ac；HandlePerkClient 1c5e |
| 上层 UL_GOSSIP | +60 打听八卦 | 可知上层区行动；夜间行动仍未实现 | 18aa；CanPlayerKnowFactionOperation 07b0 |
| 上层 UL_HOUSEHOLD_NAME | +80 家喻户晓 | Roll(20)成功尝试追加Upper客户 | 18ad；HandlePerkClient 1c5e |
| 上层 UL_TOP_REVIEW | +100 好评如潮 | 所有useClientBudget客户的初始预算+25%，跨派系 | 18ae；ApplyBudgetModifier 1bff |
| 上层 UL_TOURISTTRAP | -40 旅游陷阱 | Upper客户chance基值8减3；正rep≥25/≥50另有+4/+3分支 | 18af；GetUpperLevelClientChance 1c7a |
| 上层 UL_PETITION | -60 联名检举 | 每日治安调查进度×1.5；所有普通Security负增量×2，**跨派系** | 18b0；SecData.OnNewDay 01c0；ModReputation 1861 |
| 上层 UL_HITMAN | -100 雇佣杀手 | **未注册**；关店虽查询并可安排hitman，新局正常列表不会命中；旧存档另议 | 18b1；OnLateShutterOpen 175d |
| 革命 REV_REQUEST | +20 革命军市场委托 | 每周余数5且Roll(80)成功生成革命请求 | 18b5；StoreRequest.StartDay 18be |
| 革命 REV_QUARTERMASTER | +40 革命军军需官 | 每周余数0安排军需官 | 18b7；HandlePerkClient 1c5e |
| 革命 REV_FRIEND | +60 革命军之友 | 可知革命军行动；夜间未实现 | 18b2；CanPlayerKnowFactionOperation 07b0 |
| 革命 REV_RAID_LOOT | +80 突袭所得 | 每周余数5安排raider客户 | 18b6；HandlePerkClient 1c5e |
| 革命 REV_BLACKLISTED | -40 黑名单 | Revolution客户chance直接返回0 | 18b4；GetRevolutionClientChance 1c7b |
| 革命 REV_HIT | -100 暗杀名单 | 关店调用SetupRevBombing；不可当普通日结无风险 | 18b3；OnLateShutterOpen 175d |
| 卡特尔 CARTEL_INCREASED_STIPEND | +25 增加津贴 | 周期PayOut基础150；解锁时再加4×截断后的Cartel声望，再ModCash；整条故事的其他奖品另计 | 18b9；BuildNextWeekChain嵌套PayOut callback53e8 |
| 卡特尔 CARTEL_RELIABLE_CHEMIST | +50 可靠化学家 | handler checkin声望费用-12改-6；不是默认激活 | 18ba；StoreClientListCartel.GetRepCost callback 1a05 |
| 卡特尔 CARTEL_AUTHORIZED_EXIT | +100 金盆洗手 | IsExitAuthorized返回perk unlocked；离开/结局不在柜台范围 | 18bb；StoreClientListCartel.IsExitAuthorized 19fd |
| 卡特尔 CARTEL_RETIRED | -100 强制退休 | 关店安排cartel等待客户；后续事件不在本轮执行范围 | 18b8；OnLateShutterOpen 175d |

两个预算特权在 `StoreClient.ApplyBudgetModifier (0x1bff)` 仅检查 `useClientBudget`，没有客户派系过滤；各自向**初始预算**加 round-even 的25%/50%，两项是相加，不是1.25×1.5。不代表商店有一个被全局修改的资金字段。Sycophant 的额外预算分支才另外检查 Upper 客户。`SEC_WATCHLIST` 与 `SEC_PRIORITY` 是 if/else，不能相乘。

`BM_HONOR_REPUTATION_LOSS_MULTIPLIER=1.5` 与 `repLossMultiplier` 的构造赋值确实存在，但未发现该字段被声望运算读取；普通 BM 负值在 `ModReputation` 直接乘 **2**。文案/命名常量不能覆盖执行代码。

`BM_SMALL_TIME_BROKER` 不能仅因没有ID消费者而判定“无效果”：`GetBlackMarketClientChance (0x1c79)` 直接按整数BM声望取概率参数，<0为4，0～24为7，25～49为14，≥50为17；`StoreClientList` cctor(0x19e4)把它用于黑市客户池。`GetBMRepStockMult (0x19be)` 对<40/40～59/60～79/80～99/≥100给1/1.25/1.5/1.75/2.5，thief callback0x5324用于多项货品抽取权重；它不是保证库存件数或单品质量翻倍。显示+20门槛与真正概率/货物台阶不同。Cartel津贴消费者位于多层嵌套callback，已递归定位并复核，不能靠浅层扫描判为UNKNOWN。

## 6. A4：声望增减与计算顺序

### 普通交易的可复核算法

`PlayerStore.OnItemBought (0x17bc)` / `SellItem (0x17be)` → `StoreReputation.OnItemTraded (0x1869)`。以下的“价值”是该函数的金额参数，**不是利润**；每件交易的调用/累计依原生链路进行，不能把多件总价先求和再只舍入一次。

1. `value=abs(arg1)`；另有 `wildFavor` 使用实际金额及 difficulty 的 tradeFavorRate 更新，它不等于 faction reputation。
2. 从客户取得方向倍率（buy/sell override 优先，否则 tradeReputationMultiplier）。玩家卖出且原两金额不等时倍率加1。买入入口两个实参同价；卖出入口 arg1 是议价价快照、arg2 是预算裁切后的实付款，故不等也可能来自预算上限，不能一律解释成主动让利。
3. `BargainUIManager.GetPreBargainValue (0x23c3)`：对存在且启用的 bargain features，累乘 `max(0,1+valueMod/100)`；积>0 时 `RoundEven(value/积)`，否则保留输入。先还原此值，再乘第2步倍率并舍入。交易倍率经过 float 转换，应按 IL 精度复现。
4. **客户卖给玩家**且有赃物标记时，乘 `StolenHelper.GetStolenItemTradingRepBonus (0x1064)`：STOLEN_VALUE_INT≤25 为1.5，26～50 为2，51～75 为2.5，>75 为3；无标记1。对更新后的值舍入。
5. 同方向且 serious contraband，再乘 `1+level/2`：Mid/High/Critical 的 level 2/3/4，即2/2.5/3；Low不属于serious。`ContrabandHelper (0x0a6d/0x0a7d)`；再舍入。赃物和违禁品可叠加，作用于每一步更新后的值。
6. Lower ×0.66；Upper ×float(1.2)（提升为 double 约1.2000000477）；其他不做该派系步骤。舍入。
7. Diplomatic 且当前值>0，×float(1.33)（约1.3300000429）再舍入；负值不适用。
8. 最终 value / **400d**，送入下面的普通 ModReputation。分母400只是最后换算；“每400信用点固定1声望”不成立。

`RNG.MultiplyAndRound (0x0fc6)` 在这些调用中 bool=false，使用 `Math.Round(double)` 的中点到偶数规则，再转整数；每个 `starg.s value` 写回下一步金额，不能错误地让后续乘法回到原始 arg1。负数也沿同一舍入规则。最终 double 增量累加，不逐次取整。示例（正常难度、普通 Lower、无其他修正）：基值100先 round(100×0.66)=66，贡献0.165声望，而不是0或固定0.25。

议价倍率不是任意连续相乘：`RecomputeTradeRepMultiplier/ComputeTradeRepMultipliers/ComputeDirectionalMultiplier (0x23b3/34/35)` 为两个交易方向重算。Threaten applied 返回0；已生效不利条款按百分比≤10/≤25/≤50/>50取 penalty 0.5/1.5/3/5（`0x23c1`），blackmail再加6，合计以负倍率返回；否则用 base，当 nice offered 且base>0时加0.65/1.2/2/2.9（`0x23c2`）。交易时的预算差额+1仍是另一步。生成的 override、条款是否已生效与实际 callback 结果不可混为一谈。

### 普通增量与 Raw 增量

`StoreReputation.ModReputation(double) (0x1861)` 依序：Blabbermouth 对 BM/Rev 正增量×0.5；Sycophant 对 Upper 正增量×1.25；BM负增量×2；全局 `UL_PETITION` 解锁且目标为Security的负增量×2；随后按正负乘 difficulty gain/loss；最后加入 amount、clamp[-200,200]、更新特权。零值不乘难度。普通难度表来自 `Difficulty.Vanilla (0x01f1)`：Cozy 1.5/0.5，Normal 1/1，Hard 0.75/1.25，Challenge 0.5/2。浮点字段的真实精度应保留。

`ModReputationRaw (0x186e/6f)` 绕过上述增量修正但仍截断范围。不能把开局 Raw 奖励、任务奖励和普通交易用同一个倍数统一推算。单个故事动作可以调用多个派系 wrapper，需以向量记录。

### 主要来源和损失（基值不是最终净变化）

| 来源 | 已确认规则/入口 | 剩余边界 |
|---|---|---|
| 普通买卖、赃物/违禁品 | 上述完整顺序；0x17bc/17be/1869 | 客户倍率/商品属性若不可见，估算 UNKNOWN，不读取隐藏配置补齐 |
| 议价失败离场 | OfferMarkup/DemandBuyingDiscount 的离场分支 -3；留下/特殊客户分支不等价；blackmail犯罪客户失败 -5，无辜分支可能 -7/-10；BargainUIManager 0x23a5/23a9/23aa/23ad/23b0 | P：触发/随机结果不能事先当作已发生 |
| 普通遣走 | 没有证明“一切拒绝都固定扣分”；现有普通dismiss仍按已审核回调 | 特殊客户 callback 可能另有副作用，P |
| 通缉/错报 | 0x17de；成功 callback0x526b Security基础+5；错报0x526d -8且现金门槛扣50；还可能影响客户派系 | 奖金/特性/report flags动态，P |
| 赃物举报 | `CreateStolenReportReward (0x17df)`/callback0x5274，cash由GetReportReward+50后修正；Security按最高违禁品级别；`ReportedClient (0x1870)` 每派系起始-8、以后每次多-2，最低-20 | 物品/客户配置动态，不能承诺固定奖励 |
| Aug | 正确/错误击杀多派系基值、报告和放走差别见第4节，0x0998/99/9a | trait helper与现场结果要在执行后重新核验 |
| 店铺检查 | `ContrabandHelper.StartInspection (0x0a5c)`：发现项按1+level累计；赃物基础价值>17另按<50/+1、50～84/+2、85～139/+3、≥140/+4；总penalty cap25；分支扣Security或严重情况逮捕；清白且无先前告警/隐藏物路径+3 | P：不能把所有critical分支都称扣声望；不能利用展示违禁品后“清白”检查刷收益 |
| 揭穿/对质 | `InspectionUIManager.OnConfrontFailed (0x259c)` 客户派系-2；`StoreClient.ClientExposeFeature (0x1bed)` -4；`NegociationHelper.OnFeatureExposed (0x2637)` -1 | 上下文/客户派系仍须可见 |
| 饮水/售货机、化学品等 | `VendingMachineHelper.SettleMislabelRep (0x10cf)` 单次loss cap20后取负；ChemicalFeedbackHelper/ChemicalProduct/WaterFeature/MachineAlarm等可调用声望 | P：累计来源、配方和逐项配置未全部闭合 |
| 药物赠送/销售 | `CheckSoldNaloxgone (0x1886)` Lower分支-4或+1 | P：依freeNormal/freeFree等状态，不泛化所有药物 |
| 任务/剧情/易货 | `StoreRequest.DispenseReward (0x18cb)`、`ModProgressReputation (0x187a)`及各story callback | P：奖励字段/任务配置和所有delegate触发条件未穷尽，禁止套固定75到全部任务 |
| 开局特性 | `StartingPerkContent.HandleNewGamePerk (0x15dd)` Raw：Convict Sec-30，ModelCitizen Sec+30，Sycophant Upper+15，RevSympathizer Rev+25，KnownSnitch Rev/BM各-10，WellConnected各注册派系+15，PublicEnemy各-15 | 只说明新局规则，不运行、不改存档；其他scenario配置P |

递归扫描了所有类型/嵌套类型对 rep mutator、wrapper、OnItemTraded/ReportedClient 的直接调用位点，索引保留162行；这证明**直接调用扫描覆盖**，不是所有故事配置、间接 delegate 和未来事件的数值语义都闭合。Debug赋值入口只作排除记录，不是生产收益来源。

### 外部交叉核验及冲突处理

以本机代码为裁决来源。外部资料只用于发现遗漏与版本差异，不反向覆盖本机定义。

- [开发者 Steam 公告](https://steamcommunity.com/app/4348910/announcements/detail/1843481262690182) 的历史更新提到 BM 清洁负面效果改动、展示违禁品后清白检查的收益修补；目标 DLL 确认 BM_DISTRUSTED 对清洁服务是×2费用，不能照旧攻略写“服务不可用”。公告跨度含多版本，不把每条历史 patch 当本 build 的直接证据。
- [All Things How 派系指南](https://allthings.how/probably-stolen-factions/) 列出47项并包括上层杀手/治安旧特权；本机是47定义、45正常注册。其定义存在不代表当前新局可解锁，冲突项按本机注册表处理。
- [Nicokobo faction standing](https://probably-stolen.nicokobo.com/en/wiki/terms/faction-standing) 可交叉支持注册差异、交易舍入和预算跨客户范围；关于数字显示应以 `ReputationElement.Init` 的F2为准，不能将等级整数化误写成整个UI只显示整数。
- [GameWikiStation 派系页](https://gamewikistation.com/games/probably-stolen/systems/factions/) 所标来源 build/hash 与本机不同，相关细节仅 `GUIDE_ONLY`。未把攻略附带任务/结局信息投射给运行 Agent。

## 7. A5：M5-B～D 可直接采用的接口设计（尚未实现）

### M5-B：组件适配与效果契约

建议每个适配器提供 `ObserveVisible(context) -> SurfaceObservation`、`BuildCandidates(snapshot)`、`ValidateCurrent(command,snapshot)`、`InvokeNativeInput(boundRef)`、`Verify(before,after,receipt)`；仍复用 ActionDriver 和当前命令链，不增加万能 `click(methodName)`。

| 适配器 | 只读职责 | 执行/核验职责 |
|---|---|---|
| dialogue_flow | 当前渲染行、typing/wait、可证明的pending flow | 正常advance/skip；输入ACK与endAction效果分开 |
| dialogue_choice | 主对话可见enabled选项、顺序/文本/图标 | 原生bubble入口；绑定option与surface revision，核验选项被接受及后续效果 |
| native_panel | 实际可见root、可见控件、modal遮挡、支持范围 | 审核过的close/use路由；close可触发续话，不能只确认窗口消失 |
| discrete_quantity | 选项呈现的1/3/5等数量，不读未来图 | 通过对应原生选项提交；不把requested当accepted |
| inventory_transfer | 可见前后物品/归属/容器/现金及容量知识范围 | 记录partial/zero acceptance、清理/移动/付款/离场；保留不可逆副作用 |
| hold_control（M5-F） | 可见按钮、enabled、规定hold时长 | 按下/释放/取消和单次触发；异步结果另验 |
| inventory_tool / weapon_mode（M5-F） | 可见工具、扫描结果、模式/目标 | 扫描消耗/结果、进入/取消/单次射击；另需用户范围授权 |

`SurfaceObservation` 建议字段：`surface_ref, kind, component_version, visibility, enabled, rendered_text, controls[], status, evidence, blocker_reason, expected_effects`。引用绑定 `broker_epoch/session_id/world_epoch/revision`、customer ref、可见root实例与内容fingerprint；序号只在绑定范围内有意义，不把Unity实例ID直接当跨会话长期引用。每次执行主线程重新解析可见root和控件，既有单写/幂等先行。

状态要区分：`progressing`（有已证明的原生排队/动画，附证据、起始revision和有界等待）；`awaiting_agent`（至少一个当前合法可见动作）；`awaiting_user`（人工检查/明确批准等待）；`unsupported`（可见面板/控件没有适配器或效果契约）；`unknown`（证据不足）。停留超时不能凭空把 unknown 变成可点；观察能力不足应生成阻塞事件，不能无限重复10秒等待。

效果契约分类如 `presentation, story_state, inventory_transfer, cash, reputation, day_change, external_scope`，包含可验证字段、金额/数量边界和partial语义。可见按钮不自动拥有经济执行授权：未知或含未授权领域效果必须停留unsupported/awaiting_user，不能因为走通用choice而绕过inspection handoff、现金上限或夜间/电话范围。机制契约可复用于同组件/同效果类型；不按NPC名字新增Handler。新剧情复用旧组件但效果尚未知时，观察可复用，写入仍需证明契约。

结果拆成 `input_receipt`（原生入口接受/选项ACK）、`effect_observation`（后续库存/现金/声望/窗口状态）、`verification`（confirmed/partial/unknown）。没有足够后置证据继续沿现有 `outcome_unknown` 阻塞，不重试原命令造成二次效果。

### M5-C：Decision Inbox / Run Context

Broker 持久化 `event_id, kind, first/last_seen_identity, surface/evidence refs, status, reason, command_id, required_resolution, resolution_evidence, superseded_by`，以及授权预算/计数、命令及结果、人工等待与显式恢复事实。事件状态至少 open/awaiting_user/resolved/superseded，不能因Agent重新开始一轮而消失。Inbox的存在不能重写journal命令终态。

Agent 自己的目标、备选策略、预期收益和自然语言推理是 `plan`，可保存但不是 Broker 授权事实；不能靠一段“用户大概同意”的计划恢复人工暂停。RunContext 应从未解决事件、journal和当前observe重建。监督器每轮优先展示人工/unknown写阻塞，再处理连续经营；不得自动调用 inspection resume。

读档/新世界/epoch变化使全部可执行surface/option refs失效；保留历史事件证据但标记stale/superseded，基于新快照重新建立关联。不能把旧command ID换新ID重放未知命令。恢复时先核对inflight/unknown和人工gate，随后才允许新计划。M5-C测试应包含agent进程重启、Broker重启、读档、人工并行修改和未解决事件重现。

### M5-D：声望观察、知识库与估算

建议只读 `ReputationObservation {identity, status, source_surface, factions:[{faction_id, display_name, displayed_amount, precision, status_label, visible_perks}], completeness, reason}`。只列当前实际注册且玩家可查看的派系；默认用UI两位小数及实际可见perk。直接读纯getter的精确double须先明确“等价玩家可得信息”规则，不默认公开更多精度。客户派系仍按当前可见证据，不能为了估算读取隐藏身份。

知识库每条 `rule_id, game_build, assembly_hash, localization_hash, native_methods/tokens, evidence_grade, registration, threshold/comparator, conditions, ordered_steps, numeric_precision/rounding, effect_targets, scope_dependencies, unknowns`。定义与当前注册分开；夜间/电话/未实现tool项标注 `executable_here=false`，规则存在不进入legal_actions。

`Estimate(action, visible_before, visible_terms, ruleset) -> {status: exact|range|unknown, deltas_by_faction, cash_or_other_effects, assumptions, missing_inputs, source_grade, rule_ids}`。未知预算/客户倍率/随机/隐藏属性不能补全为默认1；可用部分区间时说明来源，否则unknown。按每次原生交易、每阶段舍入和最后clamp顺序计算，保留负值和Raw路径差异。

实际记录 `ReputationDelta {command_id/event_id, before_ref, after_ref, displayed_before/after, precision, observed_delta_or_interval, attribution, estimate_comparison}`。显示精度不足时保留区间；并发人工/剧情或多步骤效果时 attribution=ambiguous，不能把整个时间窗变化都归功于按钮。规则 `STATIC_CONFIRMED` 不代表预测结果 `LIVE_CONFIRMED`；只有前后观察及结果验证能提升特定执行证据。

## 8. 验证结果与复现

| 检查 | 本机结果 | 原始日志 |
|---|---|---|
| M4 managed基线 | 554通过、0失败 | artifacts/m5a/baseline-managed.log |
| M4 Python基线 | 197通过 | artifacts/m5a/baseline-python-unrestricted.log |
| 最终managed全套 | **593通过、0失败** | artifacts/m5a/final-managed.log |
| 最终Python全套 | **202通过、0失败**（84.057秒） | artifacts/m5a/final-python.log |
| 正式Release构建 | **0错误、2个NU1900警告**；仓库内打包，没有安装 | artifacts/m5a/final-build.log |
| Skill validator | Skill is valid! | artifacts/m5a/skill-validation.log |
| git diff --check | 通过 | 最终提交前再次执行 |

总数 **795项（593+202）**，比基线751增加44；重复运行不累计。基线Python在受限Windows管道/临时目录权限下先有20 errors/1 skipped，原日志保留；在已授权本机正常子进程权限下197全通过，最终同条件202全通过。不是删除/跳过失败测试。构建NU1900源于无法访问NuGet漏洞服务，既有基线同类警告，编译与打包成功，但未完成在线漏洞数据查询。

复现顺序：设置项目 `.dotnet/.nuget` 环境后 `dotnet run --project tests/PawnPilot.CoreTests -c Release`（Python跨层测试使用此Release DLL）；`.venv/Scripts/python.exe -m unittest discover -s tests -v`；`./scripts/build.ps1`；`python -X utf8 C:/Users/MSI/.codex/skills/.system/skill-creator/scripts/quick_validate.py .agents/skills/pawnpilot-player`；`git diff --check`。Skill校验使用有PyYAML的系统Python及UTF-8，避免默认GBK读取中文失败。

## 9. 未验证事项和最小实机实验

本轮不申请逐步操作授权，也不执行实机。后续独立获准测试槽、Mod安装和具体经济行为后，建议：

1. 捕获开/关卷帘、客户离场、日结/次晨报告的相邻快照，证明原生状态与DTO临界时序下每份候选均被Broker接受，且正常整理恢复；内部坏快照应fail-closed，不产生静默失联写入。
2. 绿色名片事件同时保存当前渲染行、主dialogue/root、可见按钮、native pending-flow和unknown surface原因；复核准确合作入口、自动/手动推进与后续面板。不能用其他Joe场景替代验收。
3. 废料按独立授权验证1/3/5选择、front容量不足/零接收、清除非自有预览、实际归属与100×accepted、离场及原生拒绝。不能只测满容量成功一例，也不能默认允许接收危险物品。
4. 声望UI逐项核对实际注册派系、F2数字、整数称号、正负/零门槛；授权的普通交易验证舍入边界、负倍率、赃物/违禁品叠加、两预算perk跨客户范围、读档保持小数。不要用调试赋值制造结果。
5. 通缉/扫描/武器留到M5-F单独授权，先做可见资料/按钮与hold取消观察，再按许可验证异步结果；本阶段静态发现不授权报警或射击。

当前明确未知：M4 `updating` 的确切Unity时点/按钮可见性（绿色名片已有静态终态解释，废料仍需现场定位）；所有prefab序列化绑定；未注册perk的实际可达性及旧存档额外perk；全部故事/任务配置与间接callback的数值闭包；隐藏客户倍率导致的估算缺口。它们有明确降级路径，不阻止本轮A1修复和A2～A5静态研究验收，但阻止宣称“完整单日自主经营/特殊剧情MCP实机PASS”。

## 10. 本机证据索引与提交边界

`artifacts/m5a/` 已由既有 `.gitignore` 忽略，无需force-add。其下 baseline/final logs，`story/` 的定向IL/卡片及UI生命周期，`counter/` 的报警扫描与武器调用链，`reputation/` 的核心/工厂/消费者/直接调用索引，`localization/` 的提取脚本、限定key和值及隔离工具，供本机复查。旧研究笔记中的初步猜测不具有正文结论优先级；最终以本报告经交叉审查的结论为准。

只提交源码、测试、README/协议说明与本报告；不提交游戏DLL、bundle、完整反编译内容、密钥、存档或原始长文本。最终本地commit及工作树状态由交付回复给出；没有push。
