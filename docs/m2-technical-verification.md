# M2 技术疑点专项核验

2026-10-31。**整体 PARTIAL，Gate Q/C/V 均为 PARTIAL，暂不开放经济 MCP 动作。** 已完成静态调查、208项自动化测试、经本轮明确授权的 Computer Use 实验与清理。实际使用一个独立测试槽（重载一次），观察两组普通报价247/285，并通过正常UI完成一次285普通购买，现金550→265。第二组8行报价与探针一致，但物品明细过滤未取得有效样本，完整身份/所有权/容量证据仍缺失。不能把现金对账或截图效果解释为自动成交核验已通过。生产 wire v2、五工具、通信方向及 `close_menu` allowlist 均保持不变。

## 1. 基线与证据规则

开始 HEAD `6eadbf6bd7d88c83b14b5dbaf25f23aaa14ad101`，已有 M2-B 的 18 个 tracked 修改、14 个 untracked 文件，全部保留，没有重置、暂存或提交。重新读取 M2-A/M2-B 报告、v2 契约、C# probe/executor、broker/journal/MCP 及测试。本轮重新运行 **87 Python + 92 managed = 179 通过、0 失败**；日志位于忽略目录 `artifacts/m2verify/baseline-*.log`。

本机 Steam build/TargetBuildID 均 `26337366`；游戏 `0.46D / VERSION 100-Rev6`；Assembly-CSharp SHA-256 `6398d44aa1675e1f02b63bc668868f970e0f5d3ae5a50f2aced8d06ca084e3b5`，与此前相同。静态证据来自本机 DLL 的 Mono.Cecil 只读反汇编，没有加载游戏对象执行业务方法。下文 MethodDef token 省略公共前缀 `0x06000000`，IL 偏移针对该版本。旧报告是问题索引，新缓存重新核对关键方法。

证据分类：`STATIC_CONFIRMED` 仅证明方法体/调用链；`TEST_CONFIRMED` 仅证明被测纯模型/投影逻辑；`LIVE_CONFIRMED` 必须有实际游戏观测；不足部分为 `PARTIAL/UNKNOWN`；证实某条候选路径不满足要求才用 `UNSUPPORTED`。原始 IL、截图、日志、配置与备份全部保留在 `.research/m2verify/` 或 `artifacts/m2verify/`，不纳入 Git。

## 2. 七项结论总表

| 问题 | 当前判断与等级 | 可交付的工程结论 | 尚缺的证据 |
|---|---|---|---|
| 1 报价 | 条件可行；STATIC_CONFIRMED / LIVE_CONFIRMED（局部）/ PARTIAL | 两组可见报价247/285；第二组8行及footer读数与截图一致，普通扣款285 | 完整行→业务物品集合绑定；第一组探针被错误阻挡，无有效逐行样本 |
| 2 容量 | 通用原生查询路线不合格；UNSUPPORTED；受限模拟 PARTIAL | CanAccept 会排序真实库存；一格非堆叠、规则可证明的纯集合占用模型可研究 | 特定物品/容器回调、购买后形状不变及原生差分验证 |
| 3 单件 | 尚未证明稳定可用；STATIC_CONFIRMED / PARTIAL | 两次GUI拖拽均无转移；集合按钮两次有效；快捷修饰键+鼠标不受当前工具支持 | 正确quick-transfer命中与目的地分支实测；不因此认定原生路径不可行 |
| 4 UI | 已知阻挡条件可行；STATIC_CONFIRMED / LIVE_CONFIRMED（局部）/ PARTIAL | ESC、关门确认、实际对话可区分；发现并修复协程列表残留导致的永久误报 | 全局层级/射线、动态窗口与真实可点击性；库存几何过滤原因 |
| 5 身份 | 简单完整移动可相关；STATIC_CONFIRMED / PARTIAL | 对象身份与短期决策ref分开；拆分新对象，合并保留目标，重载全失效 | 指定商品的移动/购买相关性和部分接收实测 |
| 6 成交 | 联合核验设计可用；STATIC_CONFIRMED / TEST_CONFIRMED / LIVE_CONFIRMED（现金及画面）/ PARTIAL | 普通购买550→265，待售图形移入主库存；缺任一完整证据仍unknown | 逐件身份/数量/owned与完整目标成员、原生调用次数和回调边界 |
| 7 顾客 | 受限能力判定；STATIC_CONFIRMED / LIVE_CONFIRMED（首日）/ PARTIAL | 首日拾荒客正常准备及购买后自动离场；普通价不使用旁列批发价 | 其他顾客运行时流程及特殊委托排除，不按SELL泛化 |

## 3. 报价：数据源、刷新与纯读取

普通玩家买入由 `BuildSelling 0x266b` 建立。它通过 `GetNonOwnedBackInvItems 0x2654` 获取后柜非自有项；`GetBackInvItems 0x37bb` 返回后柜直接 `childItems` 快照，过滤 `GeneralHelper.IsItemOwned 0x0afb → GameItem.IsTag 0x2fb3 → TagSystem.IsTag 0x312a` 为只读查表链。每项 `GetNegociatedValue 0x2ff3` 经 `Credits(...)` 写入 `BuildItemRow`，总额 `SumNegociatedValue 0x2659` 对同一列表求和。没有批发offer时写 `ui_nego_total`；有可选批发时走 `IL_0184–0235` 分支，只写 `ui_nego_total_wholesale`，其中第一金额为普通价，斜杠后第二金额为批发价及折扣，再跳过普通footer分支。不能要求普通footer标签必然存在，更不能把第二个金额当普通按钮成交额。

`OnBuyAllClick 0x266c` 会重新取得当时集合并重新求和、检查现金，不是接受 UI 上冻结的价格。`BuyMultipleItem 0x267d` 倒序逐项调用 `BuyItem 0x17bb`；后者在 `IL_0014–0019` 再求该项价格并转 Int32。因此“同源”不等于“先前显示值永不变化”。集合顺序、数量、所有权、顾客条款、临时购买特征、事件、物品内容与UI状态任一变化均须使旧报价失效；还须核对正常价按钮与批发/议价分支不同。

**不能调用报价getter做通用只读采样。** 除已知 debug TMP 写入外，`GetNegociatedValue → GetItemMiscValue(true,true,true,false,true)` 可进入子物品价值和活动事件链；`StoreEventManager.GetActiveEvents 0x1d19 → SortEventByPriority 0x1d1a` 会重建并写回活动事件列表。部分电源/液体路径的 `GetTagReadonly → TagSystem.GetTag 0x312b → InitTagString` 在缺少tag时 `dict.Add`。这些是实际写入，不因方法名含 Get/Readonly 或 debug面板关闭而消失。探针不读取隐藏 unitValue、预算、真伪或事件表，也不复制整套定价公式成为第二个报价权威。

可用的 UI 候选来源是当前 `NegotiationTabletFrame.content` 直接子项 `TabletListRow`：普通 `Row/Visuals/Name`、`Row/Visuals/Right`；展开行 `SelectedRow/Texts/Name`、`SelectedRow/Texts/Right`。只取这两个字段，排除展开行其他特征。Footer 固定 `footer/FooterRow/Label` 与 `Value`，按标签区分普通总额和可选批发总额。`TabletListRow` 没有公开 item 字段；私有 cachedRows/listedItems 不经反射读取。静态上 BuildItemRow 调用顺序及缓存复用的 SetAsLastSibling/DropUnusedRows 支持 ordinal 映射候选，但刷新过程中可能旧行与新集合同数量；诊断不会把这个候选当作已验证绑定。

**文本读取也有副作用边界。** 当前 TMP 的 `text` getter (`0x0431`) 在 backing string dirty 时重建字符串并清 dirty；本轮改用已存在 `textInfo` (`0x04b2`，纯字段getter) 的公开字符数组，不调用 text、ForceMeshUpdate、parse 或 rebuild。只读字符上限256，拒绝 pending properties、截断、溢出、未显示字符/行/页；不读未来对话。货币 sprite 的字符编码不能直接当金额解析，必须结合控件语义和截图识别货币。缓存未完成自然渲染时不强制刷新。

UI `Update 0x2642` 约每0.2秒排队，`RefreshUI 0x2660` 写私有 refreshQueued，`LateUpdate 0x2643` 才调用 RefreshNow。采样带 Unity frame/realtime；等待超过一个刷新周期的相同集合/文本只能提高观察置信度，不能替代原生重新求价的边界验证。实机第二组在frame17915已改变目标、按钮尚为购买0；17916出现完整8行及285总额。重新打开平板的frame26132文本暂不可读，26133恢复相同报价。购买frame29195现金已变265，但旧M2-A按钮仍为购买8，29196才变购买0。证明至少存在跨采样帧的UI/业务不同步，不能拿同帧旧按钮证明当前集合。Gate Q仍PARTIAL：只有第二组具备逐行探针与扣款对照，业务对象绑定未建立。

## 4. 容量：原生查询不纯，受限模型的前提

`GraphUtils.CanAccept 0x2cdb` 直接调用目标的 `TryFindOneValidInventorySlot`。`GameGridInventory.TryFindOneValidInventorySlot 0x2da1` 在 `IL_000d–0032` 对真实 `items` 执行 Sort，比较器 `0x5bff` 按 modifiedShape.minX/minY 排序。它还检查堆叠/穿透、MaxNumRemove/Add、状态有效性、parent modifier及 slot priority 委托。`TryInventorySlot 0x2d9a` 不是纯数学查询。禁止在探针执行这些函数。

真实 `TryAcceptAll 0x2cde` 最多40轮寻找slot并调用 `SlotMarker.TryAcceptOnce 0x30ff`；会堆叠、拆分、Expel、修改shape、UncheckedAccept、执行回调。返回未接收数量；null参数也可能早退0。`BuyItem` 店内目标是当前 `EmporiumEntry.invElement`，店外分支是 afterhourInventory；没有在该方法内发现“放不下自动安全转仓/回滚”。`IL_005e` 调用后 `0063 pop` 丢弃余量；仍留后柜时另有清feature/weighted callback，不能将部分接收视为无事发生。

可以安全复制的输入包括具体 GameGridInventory 的直接 `childItems 0x2d63`（new List，不递归）、`inventoryShape 0x2d75`、item parent/count/modifiedShape backing getters。shape接口是虚调用，诊断只接受已审读的精确 `GridShapeBuilder` 实例；其 minX/minY/GetLocal读取自身存储。minX/minY仅标记为 `shape_min_x/y`，不冒充已验证的屏幕像素或完整可用网格占用。裸 `HasFreeCell` 不证明给定形状能放入。

最窄纯算法候选：准确的店内主库存，完整平面矩形网格，物品1×1、unitCount=1、非堆叠/非容器，所有源移除与目标接受/状态/回调规则已证明，购买移除特征后形状与接收资格不变。复制全部占用到本地集合，按原生倒序逐项预留空格；全部能预留才候选通过。禁止多个项目各自检查同一个空格。未知形状、重复/越界格、隐藏或嵌套内容、回调或上限未知、源/目标/条款改变全部拒绝。

`tests/test_m2_capacity_model.py` 的5项独立fixture验证这些数学前提、共享空间、逆序、碎片化反例与输入不变。它不读取DLL、不等价于游戏接收器，更不能给当前真实库存发放“容量通过”。**Gate C PARTIAL；没有已证明可安全开放的具体商品/容器allowlist。** 最小追加证据应是一个精确物品定义和接受容器规则的静态闭包、完整只读形状快照、正常受控接收差分；无需先故意做满仓扣钱实验。

## 5. 单件转移：输入语义与集合路线

`ItemQuickTransferHandler` 的 quickTransferKey (`0x36d8`) 经 KeyBindHelper取 `quick_move_inventory`；默认键码306（LeftControl），但用户配置优先，EnsureLoaded会读/改设置缓存，不由诊断调用。`OnEventPress 0x36da` 保存射线命中的currentItem及modifier状态；`OnEventRelease 0x36db` 要求同一handler key、quick标志、抬起时仍命中同一个GameItemElement、MayRemove通过，随后按窗口/归属/经营状态选择目的地。TryAcceptOnce返回余量也未成为操作成功证据。

所以正常“Ctrl+实际配置的鼠标键点击同一物品”是静态候选，不是已验证可直接调用的语义API。拖拽需 press→mouse move→release 的同一状态机；`EndDrag`、UncheckedAccept和改parent/list全部排除。StoreTransferButton一次复制并搬运整个前柜集合，另有教程通知；其副作用覆盖不能自动等同于quick-transfer。

本轮普通拖拽两次均无转移（瓶状商品、盒状商品各一次），之后停止；集合按钮两次均搬运成功。载入提示可见Ctrl/Shift/Alt+点击映射，但当前Computer Use的键盘chord与鼠标API不能表达按住修饰键点击，因此未执行quick-transfer，也不伪造这种测试。结论仍是“暂未证明稳定可用，首版保留受限集合路线”，不等于确认游戏不支持单件操作。

## 6. UI阻挡：可读信息和不可证明部分

`ResolveEscape 0x281e` 先写lastEscapeResolvedFrame，再按规则关闭选项、日结、ESC、网络、取消选择/拖拽或打开ESC。`CloseTopmostEscapeTarget 0x281f` 只比较部分可移动PixelWindow、paper直接子项及docked panels的focusStamp，不是完整Canvas/raycast系统。其调用的 `UIDragHandler.GetActiveDockedPanels 0x2355` 对共享注册表 RemoveAll，再排序，因此也不能当只读getter。

可直接采集已知ESC/普通确认/平板confirmRoot/选项/议价/战斗锁、当前对话UI根和等待标志；缺失manager不等于无阻挡。PixelWindow.IsVisible (`0x35a5`) 只读activeSelf，另需activeInHierarchy、alpha、屏幕和mask裁剪。局部 Button.interactable、CanvasGroup、focusStamp都不能证明全局点击命中。对话start/end/choice可能执行业务委托；不能把“正在打字/等待推进”当一个无副作用的Next动作。

**实机纠正：协程列表数量不是对话活跃计数。** 第一版reader使用 `isConversationActiveFlag != 0 || activeCoroutines.Count > 0`，导致介绍已结束、报价可操作时仍永久blocked。全模块IL复查发现前一字段没有读写指令引用；`ScheduleFlow 0x24a0` IL_0021–003e追加协程句柄，正常 `EndConversation 0x24b6` 不移除句柄，只清等待、隐藏气泡/选项并置currentDialog=null；`StopAllTrackedDialogueCoroutines 0x24cc` 仅由InterruptDialog调用清列表。修复后只检查currentDialog非空、文本/姓名气泡和现存选项GO的activeInHierarchy、manualwait，不读取对话正文/未来选项业务数据。此处只表示已知对话UI，不证明所有异步流程结束，UnknownUi始终true。修复实机已看到介绍结束解除blocked、成交对话重新blocked、对话结束再解除。

库存初始及成交后items仍为空。静态确认数据源为 `EmporiumEntry.invElement` 与 `GameGridInventory.childItems`，但精确是哪一项窗口/Mask/屏幕/类型过滤排空尚不确定。本轮不放宽过滤，不将空数组当作空库存；后续最小实验应增加不含物品内容的过滤原因标记，并对照可见根/遮罩几何，而不是直接跳过可见性检查。

本轮投影策略：已知阻挡→blocked并清明细；其他一律unverified，不产生consistent或写许可。未知窗口的局部矩形不能证明无遮挡，诊断明细明确为 `visibility_basis=geometric_candidates_require_screenshot` 的本机几何候选，必须由同场景截图逐项确认后才可标为LIVE可见；不作为正式MCP状态，不含隐藏容器或未来信息。若无法证明完整可见或被未知窗口遮住，Gate保持不通过。纯fixture只能测试分类政策，不能证明实际Unity事件顺序。

## 7. 物品身份：相关性不等于可复用决策引用

`GameItem.uniqueId` getter (`0x2f53`) 纯读取；`GetUniqueID 0x302a` 在零值时递增玩家计数并写字段，禁止作为读函数。完整无堆叠移动可以保留同一对象；部分接收可能 `GetSplitItem 0x2fd5` 先减少原数量、执行回调，再通过工厂创建新物品/新ID；不能假定两个分片沿用原owned/feature状态。`StackItemUnchecked 0x30fe` 增加目标、减少来源，来源耗尽后被Expel，目标对象/ID保留。

购买先改owned和临时特征，再接收，因此可能保留原对象、拆分或合并；单看ID出现不足以证明数量和归属转移。SaveManager EncodeNode (`0x3976`) 保存uniqueId及图内uuid；DecodeNodesInternal (`0x397b`) 创建新对象后恢复数值ID，数值相同不代表原runtime对象。

建议分离两个概念：本机研究的对象相关token可以帮助比较相邻可见快照；未来 `item_ref` 是 session/world/revision绑定的决策引用。任何数量/归属/容器/报价/选择变化重发决策ref；拆合堆清旧引用，不能用字符串相似或名称相同重建血缘；顾客替换使待售引用失效，世界/加载/会话变化全失效。诊断不导出真实ID；被阻挡、缺失、读取异常后清相关token，故它本身不能填补未观察期间的交易证据缺口。

## 8. 成交结果：PurchaseVerifier设计与证据缺口

静态精确次序是 `BuyMultipleItem` 倒序循环→`BuyItem`求价→`OnItemBought`→扣现金→TryAcceptAll→残留后柜分支；结束后RefreshUI和接受对话。`OnItemBought 0x17bc`：先清tag，`IL_0023 StoreClient.OnItemSold`（先自定义委托、后可选预算变化），然后声望、买价记录、统计，`IL_0090 SetItemOwned(true)`，清event/temporary-buy特征。**owned=true可能早于扣款和接收**。异常可发生在任意中段，没有事务回滚。

`PurchaseVerifier` 必须保存一份独立不可变证据：command ID/完整参数指纹、原生入口尝试次数、mod/session/world/customer/context、真实选定普通价条款及其生成/显示frame、完整有序源集合与数量/owner、源/目标对象与全部成员、目标接收规则/形状快照、现金、允许的顾客回调范围和容量证明。原生调用前再次核对；“存在方法名”或“某对象count=1”不能替代Q/C/V前提。

| 阶段/观测 | 结果 |
|---|---|
| 未进入入口且可证明未发生相关副作用，前后状态一致 | rejected |
| 标记即将进入，或入口已开始 | may_have_side_effects=true；不重试 |
| 同世界/顾客执行边界，调用次数恰好1，所有预期源项消失、目标对应对象/数量出现、owned正确、现金差等于报价、其他成员/资金变化可解释 | succeeded候选；还须原生完成与回调范围证明 |
| 仅现金减少、仅owned改变、仅列表清空或顾客离开 | 证据不足，不能成功 |
| 某项滞留后柜/少量接收、身份拆合无法核对、委托异常、现金额外变化、采样中断/换世界、执行期限到 | outcome_unknown；保存部分证据并保持写锁 |
| 成交效果已完整确认，后面还要Agent推进对话 | 当前动作终态结束；新的交互阶段单独报告，不无限占写锁 |

不得仅靠一次当前库存快照确认崩溃前命令：原前置/报价/完整成员或调用次数丢失时，同名物品可能原已拥有或经其他动作进入；净现金相同也不能排除抵消的副作用。broker可归档原Mod保留的完整结果，不能从“现在看着对”重造原命令成功。未知不通过自动重试或删除journal解除。

当前公开 ModHook 事件表覆盖生命周期/生成/tooltip等，未找到普通购买逐项完整结算事件。`StoreClient.onItemSold` 发生太早且不是接收完成事件；订阅或包裹它会改变委托状态，不属于只读字段采样。下一阶段优先在自己受限dispatcher的入口前/返回后捕获证据，复用M2-B调用次数门禁；若必须观察逐项净变化，则需新的最小原生hook方案、异常边界与Mod安全扫描验证。本轮不加入Harmony、反射补丁或自定义游戏委托。帧级采样不能重建同一帧内部的部分成功序列。

研究fixture `tests/test_m2_purchase_evidence.py` 覆盖全联合证据、扣钱但未接收、额外物品/金额、拆合歧义、丢失原始证据、世界/顾客变化、调用前拒绝及重复调用。独立复查纠正了“invocations=0就rejected”的反例：若观测已变仍必须unknown。fixture只是验证判据，不能制造真实quote/capacity/callback证明。Gate V PARTIAL。

## 9. 普通顾客范围

首日跳过序章的 `CreateScavGeneral 0x19b8` 有SELL意图和可选批发优惠；主对话endAction `0x536e` 调AddScavGeneralStock再OnIntroduced，商品集合随机。这些属性不证明普通价分支不能使用，也不证明与所有SELL完全相同。要区分普通OnBuyAll与批发接受，不把批发总额错配普通按钮；M2-A只给该场景一次普通购买LIVE正例。

其他普通池候选涉及整箱食品、水、可检查商品、预算、SELLNBUY和加权区回调，不能按SELL泛化。较窄研究候选 `scavStamp` 的工厂无显式批发/预算和自定义onItemSold/onDealAccepted设置，但末行回调按随机/日期增加印花，仍要核对实际上下文；不为了碰到它刷顾客或注入物品。

能力allowlist应包含精确顾客工厂/实例与当前普通条款、已审查的start/end/choice/onItemSold/onDealAccepted目标、无未支持预算/特殊业务、当前完整商品集合、对话是否已完成准备。基础 `OnDealAccepted 0x1be3` 先执行可选onDealAccepted，再决定后续；普通allDone endAction可自动Dismiss，另有延迟CurrentClientLeave分支。拒绝/叫号可能经过hold/确认/intercept及自动下一位，不能当无害UI关闭。本轮首版不宣称多顾客完整流程已验证。

## 10. Gate Q/C/V与范围决策

| Gate | 当前状态 | 通过所需范围证据 |
|---|---|---|
| Q 报价 | PARTIAL | 已读第二组8行及285总额并与扣款一致；仍缺完整业务对象绑定及第一组逐行探针 |
| C 容量 | PARTIAL | 原生查询副作用已证实，纯模型测试通过；仍无一种真实商品/目标容器的完整规则及接收差分证明 |
| V 结果 | PARTIAL | 已有现金550→265与画面转移；仍缺逐件身份、数量、owned、完整目标成员和原生入口次数证据 |

三Gate尚未全部通过，**当前没有可开放的M2-D经济写动作范围**。可以在另行批准的正常UI实验中观察普通价购买，但不是授权MCP新动作或认定容量安全。候选缩窄为：已审查普通SELL条款、同一完整可见集合恰好一个、1×1非堆叠非容器、购买后形状/资格不变、完整目标占用与规则可证；未知则拒绝，不取消检查。

## 11. M2-C可以直接进入的编码范围

可实现严格类型的“观察候选/不可验证原因”、已审查纯字段读取、bounded已渲染文本、完整集合身份与revision失效、已知UI阻挡分类。未知/缺失用null+原因，不用空列表伪装无商品；诊断局部子集不直接作为正式完整库存。正式quote必须补完行绑定与显示更新的证明；正式capacity必须补完精确接收规则。所有业务引用与条款同次快照生成，执行前复查。

## 12. 最小诊断开发与测试

沿用 `-M2AProbe` 显式构建开关，新增本地 `VerificationDiagnostic` 与 reader，只输出日志 `PAWNPILOT_M2_VERIFY_READ_ONLY`；无新增网络字段、正式动作或协议升级。它捕获frame/realtime/cash及有限直接容器几何候选、固定行/总额、已知阻挡；不读隐藏库存、真值、未来队列或未来对话。真实对象仅用于进程内opaque相关token，异常消息不导出；64项目/行、8footer、256字符硬边界，异常整样本unavailable。缺失/遮挡后不保留旧内容充当当前事实。

旧 `TradeDiagnosticReader` 的文本读取也改用只读已渲染字符helper，避免原TMP懒缓存写入。没有调用价格、CanAccept、TryFind、TryAccept、UI resolver、强制文本刷新或新增游戏hook。默认构建不执行这些采样。每帧几何检查有额外分配和开销，需实机少量观察；本轮不承诺性能生产可用。

最终完整回归 **100 Python + 108 managed = 208通过、0失败**，其中容量研究模型5项、成交证据模型8项、纯诊断投影16项。日志为 `artifacts/m2verify/final-python.log`、`final-managed.log`。原179项基线已单独重跑；fixture不等于Unity运行时验证。默认与诊断构建均0警告0错误；实机发现对话误判后，只改Unity reader的字段判定，重新完成两种构建并通过实机前/后对话阻挡转换核验（`build-*-live-fix.log`），纯投影/协议代码未再改变。中途编译纠正了conversation flag为int、IsVisible为方法的实际签名；离线构建的NuGet审计网络告警通过项目restore关闭该次网络审计后消除，没有升级依赖。

## 13. 本轮实机授权范围与实际结果

使用 Computer Use 控制 Steam 与 Probably Stolen，最多新建两个独立空白测试槽，不加载原有进度。实机前备份/哈希原slot及保存目录元数据；游戏退出时仅安装已构建诊断DLL、manifest、session.json至 `Mods/pawnpilot_m0`，正常Mod/network，不开开发模式；启动本轮本机broker，不发任何经济MCP动作。

批准范围包括首日正常拾荒客、少量单件/集合搬运、至少两种非空普通报价、ESC及确认取消、最多两次现金足够的普通UI购买。用户明确回复“批准上述完整范围”，随后在先前Computer Use暂停后回复“继续computeruse”。实际仅新建一个 `save_5.es3`，普通难度、学徒、跳过序章、不选无尽；只接待首日拾荒客，未加载原槽。重载一次自己的初始测试槽后生成不同的首日外观/商品组合，构成第二报价场景，无需第二新槽。

正常购买实际 **1次**，金额285≤550，点击“购买8物品”，未点“接受批发”。没有经济MCP调用、议价、销售、满仓/失败实验、注入/改钱、拆合堆或内部移动。两次单件拖拽无效后未重复；集合按钮共2次有效。仅利用正常UI及只读日志，没有通过Mod绕过输入。

首个安装DLL SHA256为 `82F7722A8FAF5BCAB20978A0F1412D0862984D5FE02B0687078BD1B3788F14EC`；修复对话误判后，正常退出再仅更新DLL，最终为 `F8FF2188A420689CFB49DDE1F216ED5668E96546C31A09F811670F086A82A727`。manifest v0.1.0、session保持原值；Mod正常扫描加载、network权限，开发者模式关闭，Harmony补丁0。两版收据均保留，未改原游戏程序集。

证据统一在忽略目录 `artifacts/m2verify/live/`。第一轮world为 `c42a53efee1d4d89bc3ba4b566967eb5`，第二轮为 `30f20e68a2754090b4f4d946c4bc6b3b`。frame只在对应游戏进程内比较；截图与附近稳定样本对照，不声称二者原子同步。

| 场景 | 观察与证据 | 结果边界 |
|---|---|---|
| 新槽 | `02-newgame-options.png`、`03-slot5-created.png`，day1/cash550；`before-files.json`无save_5 | LIVE：独立新槽；原day7/cash394未加载 |
| 报价A | `04-stock-ready.png`、`06-quote-a-247.png`；努特加85、助熔剂（基础）4、电子元件（普通）33、一包海明威香烟（赝品）50、水罐25、化学用品（普通）50；普通247、批发198（20%）；第一轮frame64719→64720按钮0→6 | LIVE：屏幕6行求和247。仅记录原生可见行名，不展开鉴伪功能；该组合不满足未来简单商品allowlist。诊断受对话误报阻挡，没有有效A逐行/物品探针 |
| 单件/集合 | `05-single-drag-no-transfer.png`及前后截图；两个普通drag无效，绿色集合按钮转移到后柜 | 单件0/2，集合2/2；没有快捷修饰键实测 |
| 对话误判与修复 | `player-first-run.log` frame57744后持续dialogue_active；`player-purchase-run.log` 修复后frame14798阻挡、15958解除、15982读到footer | 原问题确认为协程句柄残留，修复后正常解除；所有ui_consistency仍unverified |
| 报价B | `10-quote-b-285.png`、`13-purchase-before-550.png`；一板零嚼8、合成奶酪20、螺丝刀25、撬棍75、3D打印耗材12、一瓶净水药片55、医疗用品（普通）40、化学用品（普通）50；frame17916和26133相同8行 | LIVE：8行和footer285/223(-22%)与截图一致，8项求和285；绑定业务对象仍unverified |
| ESC/确认 | `07-esc-blocker.png`、`16-esc-after-fix.png`；第二轮frame34268为escape_menu。`11-confirmation-blocker.png` frame21818为shutter_confirmation，点“否”后frame23526恢复，cash550 | LIVE：已知遮挡分类及清明细；未证明全局无遮挡 |
| 非预期导航 | 第一次点击ESC“主菜单”立即返回，无确认框；未购买、未保存营业进展，之后仅重载本轮day1测试槽（`08-only-test-slot-reload.png`） | 不能将此步骤算确认弹窗测试；真正确认取消使用上述关门对话框 |
| 普通购买 | `12-before-purchase-inventory.png`、`13/14-purchase-*.png`、`15-after-purchase-inventory.png`；一次UI点击，frame29195 cash265，差285；8行清空，主库存出现对应图形，柜台剩报纸 | LIVE：现金/画面效果吻合；不是完整PurchaseVerifier成功，items始终空，未取得owned/单位/全成员或原生调用次数证据 |
| 后续对话 | frame29195 dialogue_active，29784解除；29808 customer=null，画面随后无人 | LIVE：本顾客成交后自动离场；不能推广所有SELL顾客 |

按预先目标归类，实机共有 **6项通过、2项失败/未完成、1项PARTIAL**：通过为正常Mod加载、集合搬运、两组屏幕报价、修复后B逐行/footer读取、已知ESC/确认/对话分类、授权清理及哈希保护；失败/未完成为单件拖拽和可见库存明细采样；PARTIAL为完整成交联合验证。另记录1次普通UI购买现金核对成功，不重复计入完整V通过。首版对话误报是已修复的实机缺陷；通用容量与拆合堆未做实机实验，不计成功。整体不是因完成一次购买而升级PASS。

## 14. 尚未解决与最终状态

最小后续验证顺序：先定位库存可见性过滤的失败条件并证明完整直接集合；将已渲染行与业务对象绑定且处理刷新失效；选择一个精确1×1、单单位、非堆叠非容器商品及目标容器闭包；最后在受控dispatcher保存原生入口前/返回后联合证据。任一条件未知即拒绝。M2-C可先实现受限文本候选、已知UI阻挡、unknown原因与ref失效模型；正式完整quote/capacity仍暂缓。当前M2-D**可开放经济MCP动作数量为0**，受控正常UI实验不等于生产写许可。

收尾已完成：通过UI删除本轮save_5（`17-test-slot-removed.png`仅剩day7原进度）、停用Mod（`18-mod-disabled.png`）、正常退出游戏；校验最新安装收据后删除且仅删除DLL、manifest、session.json及空目录，`cleanup-receipt.json`记录target_absent=true。停止本轮broker后连接127.0.0.1:18765失败，`broker-stop-check.json`记录。一次误用/health得到404、一次重复启动被journal独占锁拒绝，均未删journal或重置状态。未修改其他Mod或原程序集，未覆盖保存索引以抹掉变化。

`file-comparison.json`确认以下原存档完全不变：

| 文件 | SHA-256（前后相同） |
|---|---|
| save_0.es3 | `06130A952E024DD113747EB563459F0395886F96C8092BC229767B6FA13B95F7` |
| save_0_day5_20261031-060251.es3 | `DA891F60E6FB70EC6F1553148C2D5CD3C547F5394AD93E897E32583DE662B1BE` |
| save_0_day6_20261031-112207.es3 | `488D48FDF1C695996D6B1DD5DB84A11FC0C1D19167033B91AA692330E0408F94` |

游戏Assembly-CSharp哈希也与§1相同。保存目录内SaveFile.es3、mod-trust-list.json、steam_autocloud.vdf、旧6份GameLog未变；saves_index.es3、Player.log/Player-prev.log改变，新增两份本轮GameLog。Mod启用/信任与Steam运行记录发生正常授权副作用，最终为停用；未尝试抹除这些记录。忽略目录保留原存档备份、两版收据、截图和日志。

本轮新增6文件：本报告、`src/PawnPilot.Probe/VerificationDiagnostic.cs`、`VerificationDiagnosticReader.cs`、`tests/PawnPilot.CoreTests/VerificationDiagnosticTests.cs`、`tests/test_m2_capacity_model.py`、`tests/test_m2_purchase_evidence.py`。在已有成果上修改4文件：`ProbeBehaviour.cs`（opt-in日志）、`TradeDiagnosticReader.cs`（只读已渲染文本）、CoreTests的csproj和Program（引入诊断fixture）。其余M2-B修改保留。本轮未改变正式动作或协议。

使用6名非Astra子代理开展报价、容量、物品、UI、顾客及独立证据审查；复查纠正成交fixture拒绝判据、可见候选措辞和实机协程误报。最终Git为19个tracked修改、20个untracked文件（含原M2-B成果），HEAD仍为§1值，未暂存、未commit、未push。`git diff --check`通过（仅既有CRLF转换提示）。关键阻断为完整集合、容量与成交联合证据缺失；不能用泛化容量getter、单一cash差或UI消失替代。
