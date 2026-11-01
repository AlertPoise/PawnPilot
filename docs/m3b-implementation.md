# M3-B 基础自主议价

状态：**M3-B PASS（普通买入方向自主议价后正常结束）**。真实 Codex CLI 经MCP自主提议降价，识别接受、读到265→238的新报价，并因原容量限制合法结束。没有实际成交；议价后的现金结算、卖出方向、拒绝与离场随机分支仅自动化/原生调用链覆盖，不能宣称已实测。

## 基线与边界

从干净的 `feature/m3a-basic-sell` / `3d5e4d9949ab26c325b2018e1c311a1cd35a7481` 开始，核对同名远端一致后创建 `feature/m3b-bargain`。M3-A基线重跑377 managed +149 Python =526通过。当前游戏程序集与M3-A同哈希 `6398d44aa1675e1f02b63bc668868f970e0f5d3ae5a50f2aced8d06ca084e3b5`，未修改原程序集。

保留双层目录、五个MCP工具、wire v3/business schema2、ActionDriver、broker、单写、去重和未知不重放。普通客户白名单沿用M3-A：SELL为scavGeneral/shadyPharma/food_scav_story；BUY为spacerFood/spacerFoodStory。没有新增鉴定、批发、易货、特殊业务、威胁、勒索、格子规划或日结能力。三个非Astra子Agent分别协助原生与Runtime、解锁与Python契约、成交审查与CLI脚本，主Agent审核并实现Unity适配。

## 原生机制核查

当前DLL的聚焦Cecil证据保存在忽略目录 `artifacts/m3b/research/`。只检查议价UI、选项、解锁、可见提示和既有结算衔接，不调用运行时方法进行调查。

| 问题 | 当前版本结论 |
|---|---|
| 开启/返回 | NegotiationTabletFrame.bargainButton.onClick → NegociationUIManager.OnBargainClick → BargainUIManager.ToggleUI，再刷新平板控件。议价面板停靠于交易平板旁，关闭不撤销条款 |
| 可用条件 | 实际已显示的议价按钮需要顾客可议价、室内、已介绍议价、非Awful Negotiator；选项在对话、移动、主对话将开始或交易锁定时禁用 |
| 正常解锁 | 新局可见Skip Intro选项通过HandleSkipIntro正常解锁第1天议价；未跳过序章的教学介绍在第8天。测试无需改存档或跨日 |
| 选项 | 从实际构建的Group和TabletButton读取；本版正常百分比表为10/25/50/75，但实现不构造百分比菜单，只输出当前可见选项 |
| 玩家卖出 | Discount让步降售价，Markup争取加售价；只用于普通BUY客户 |
| 玩家买入 | BuyingMarkup让步增加付款，BuyingDiscount争取降低付款；只用于普通SELL客户 |
| 随机性 | 争取有利价格的正常提议可能接受、拒绝留场或离场；主动让步走正常接受分支。实现不读取随机数、真实预算或计算概率 |
| 可见提示 | 正常悬停可显示接受/拒绝/离场概率及声望预览；只读取已经渲染且归属于当前选项的tooltip，未显示时null。repLine读取当前可见声望文字 |
| 价格影响 | 原生临时特征作用于对应方向柜台商品，并附带正常后续上柜回调；不是仅当前选中物品。普通footer和结算都使用生效价格，不自行计算百分比金额 |
| 结果 | offered/selected在随机结果前就设置，只证明提议提交。接受/拒绝由当前显示台词、报价和离场观察提供证据；不读取隐藏applied字段 |

重要只读边界：CanOffer、公有offered属性、成功率和tooltip计算方法可能经Refresh改写跟踪状态/声望覆盖，观察不调用它们。只读反射限已构建groups的UI绑定、trackedClient身份、selected可见高亮、repLine及当前tooltip owner；不导出其他私有业务数据。

## 观察和动作

`business.details.bargain` 为schema2可选扩展，旧记录缺失/null可读，schema1保持原契约。字段包含available、phase、NPC direction、terms_revision、可见options、reputation_text/hint_text和最近提议的last_option_ref/result/result_text。选项有短期引用、显示文字、语义kind、带正负号的price_change_percent、enabled/selected；不把百分比当成功率。

| 动作 | 精确参数 | 后置核验 |
|---|---|---|
| open_bargain_panel | customer_ref, mode=buy/sell | 同顾客/世界、现金不变、新revision，面板由closed到choosing且存在可见普通选项 |
| choose_bargain_option | customer_ref, mode=buy/sell, option_ref | 当前完整正常报价及非空商品绑定；执行真实button.onClick一次，读取同顾客该按钮selected高亮，记录提议已提交及terms_revision增加 |
| close_bargain_panel | customer_ref, mode=buy/sell | 面板由choosing/responding正常返回closed，不撤销议价条款 |

三个动作只按各自条件开放。选项绑定世界、顾客、方向、可见内容/状态与条款版本；关闭、换顾客、变化后旧引用失效。Native callback前重新核对实际顾客对象、面板及按钮可用性。只读观察可跨停靠的议价面板读取交易信息，成交、搬运和普通遣走仍要求先关闭它；不会因为无关全局UI覆盖未知阻止正常议价。

`choose`的succeeded含义是已观察到提议提交，不是客户接受、更不是成交。result为responding时等待当前对话；price_changed/unchanged比较同一柜台集合的实际正常总价，unchanged可能包含取整，不能单独当作拒绝。回应时客户实际清空才记customer_left；后续正常成交台词不会覆盖已记录议价台词。原生离场分支先把同一客户意向改为LEAVE，允许已确认回应的关闭及必要普通对话，不开放新的交易/议价。

## 成交衔接与Agent决策

仍用 `purchase_visible_offer` 和 `sell_visible_offer`，mode=normal表示普通支付入口，包含已生效的议价调整。每次已确认提议均轮换offer_ref，即使拒绝或取整后总价相同；集合指纹仍绑定真实商品和当前金额。原生临时特征不被错误当作不支持物品，保留原容量、售前再检查、预算、单件卖出及现金/物品身份验证。

策略留给Codex：依据当前可见报价、现金、已知成本与经营目标，决定不议价、有利报价提议、主动让步或放弃；不写最大折扣策略，不预测隐藏结果。CLI专项 `--m3b --max-spend <批准上限>` 限每次运行一次议价提议、一次经济交易，并累计顾客/写动作预算；处理完实际结果即停止，不追逐随机样本。

## 自动化与实机证据

原始日志、存档、游戏DLL及密钥仅保留本机忽略目录，不提交Git。

自动化：**436 managed +166 Python =602通过，0失败**。Release构建成功；NuGet漏洞信息获取出现NU1900网络警告，不影响编译。保留M2/M3-A全量买入、卖出、拒绝、默认整理和生命周期回归。新增议价可用性、两方向语义、可见选项、同顾客引用、旧报价/旧选项、价格变化/不变/离场、重复投递不重复回调、正常结算金额，以及blocked议价UI与专属legal_actions共存的完整快照测试。面板人工恢复覆盖持久化重启，选择和成交恢复仍被拒绝。最终审查还补齐Python在议价面板存在时对close_menu的原条件例外，避免与C#快照不一致。

日志：`artifacts/m3b/research/managed-tests.log`、`python-final.log`、`build-live-fix.log`。Python stdio/journal测试在宿主权限运行；沙箱WinError5尝试不计入PASS。

### 本轮授权与实机修复

用户明确批准最多2个独立学徒/普通/无额外特质测试槽，正常Skip Intro解锁第1天议价；每槽最多4位普通顾客、24次MCP写入，全轮最多2次议价（每槽1次）、2笔普通交易，累计买入不超过550。CU只用于启动、测试环境、初始设置、观察和清理；不点击议价选项或替代MCP成交。不跨日、不开特殊业务、不整理、不加载原进度或修改存档。

第一次CLI误用响应外层schema2而不是state.schema_version3，start请求在adapter拒绝，未入队。随后一次open_bargain_panel原生已打开，但Capture错误生成blocked+operable=true快照，完整协议拒绝，命令 `bargain-1b-open-bargain-4` 保留为outcome_unknown；当时没有议价选择、商品转移或交易。修复仅使operable遵守原blocked不变量，面板专属legal_actions仍正常可用；补完整快照回归，未放宽协议约束。

正常退出旧Mod、按收据重装修复版、重载本轮save_14后，核对新session/world、550现金、无顾客、闭门和无菜单。既有人工恢复白名单最小扩展到wire3的open_bargain_panel/close_bargain_panel，并同步journal持久化校验；所有期限、新会话、快照和版本条件保留。choose及所有经济动作仍不可人工解除。原unknown未变success，单独记录manual_state_alignment；没有清空journal或重放旧ID。证据：`artifacts/m3b/live/player-open-unknown.log`、`open-unknown-status.json`、`pre-reconcile-state.json`、`open-manual-alignment.json`。

### 真实MCP结果

第一槽save_14累计14次act提交、3次接待（包含修复重载后的接待），0次议价选择、0笔交易。Luna客户端除首轮schema错误外，还出现两次库存到达动画期间stale准备、一次version字符串抄错，均未入队执行；后续正常开/关议价面板和遣走成功。一个农产品双向顾客为other，只做正常遣走，不扩展其特殊交易；当日再无可接待顾客。没有为随机议价结果重开，因为尚未发生任何选择。

第二槽save_15使用正常Skip Intro，真实 `gpt-5.6-sol` Codex CLI + PawnPilot MCP。仅1位普通拾荒卖家customer-123，7次act均succeeded/postcondition_observed：

| 命令ID | 动作 | 实际证据 |
|---|---|---|
| bargain-2-start-001 | start_business | 开门，现金550 |
| bargain-2-receive-002 | receive_next_customer | 普通NPC SELL，到达后自动完成介绍 |
| bargain-2-prepare-003 | prepare_purchase_items | 当前商品上后柜；完整正常报价265，8件待购商品，offer-151 |
| bargain-2-openbargain-004 | open_bargain_panel | 原生打开，观察到实际可见+10%和-10% |
| bargain-2-choice-005 | choose_bargain_option | Agent选择bargain-option-153的-10%；terms_revision 0→1，selected只作为提议确认 |
| bargain-2-closebargain-006 | close_bargain_panel | 回到普通交易；完整新报价238、offer-160、result=price_changed |
| bargain-2-dismiss-007 | dismiss_customer | 正常遣走，顾客absent/leaving，现金仍550 |

Agent先说明265的普通报价和550现金，认为降价10%能降低收购成本，主动加价10%会增加支出；概率和声望提示未显示，因此明确风险未知。随后只提交一次提议。玩家可见回应为“……好吧。成交。”，结合实际新报价238确认接受，没有把命令succeeded冒充议价成功。报价由游戏读取，没有自行按百分比生成；旧offer-151失效，新offer-160绑定更新后的条款。

成交前再次观察：既有容量检查返回 `capacity_unsupportednestedcontent`，没有合法purchase_visible_offer。因此没有提交购买，没有绕过嵌套容器限制；Agent按当前合法dismiss_customer结束。8件NPC商品离场，玩家原有物品/数量保留，上柜报纸保持自有，现金550→550。这里的265→238是有效交易报价变化，**不是已支付金额或已实现利润**。

全轮2槽、21次act提交、4次接待、1次议价选择、0笔买卖、买入支出0。没有CU点击议价或代替MCP业务动作。原始证据：`artifacts/m3b/codex/bargain-1*.jsonl`、`bargain-2.jsonl`及stderr，`artifacts/m3b/live/command-summary.json`、`player-slot14-final.log`、`player-slot15-final.log`。五个MCP工具均经真实CLI使用，不以诊断HTTP或模拟测试作为实机通过证据。

### 覆盖限制

- 买入方向完成真实提议、接受识别、新报价与合法结束；议价后实际购买被既有nested-content容量限制阻止，未产生现金结算样本。
- 卖出方向、主动让步、拒绝留场及议价导致离场由原生调用链与自动化覆盖，本轮未取得真实样本，不为凑随机结果追加选择。
- 当前屏幕停靠位置只显示±10%两项，其余屏外选项不暴露；未增加拖动/滚动/悬停动作。无已显示风险tooltip或声望文本，因此本轮未验证非空提示和声望变化。
- 继续保留已有普通买入、单件卖出、默认整理与M3-A open_trade_panel；本轮未重做这些功能的实机成交/整理，自动化全部通过。

## 清理与原数据保护

已通过游戏UI删除save_15、save_14，列表仅余原第7天存档；没有加载或删除原进度。已在Mod管理中停用PawnPilot，正常退出游戏，按本轮收据逐一匹配哈希后移除DLL/manifest/session三个安装文件及空目录。broker正常停止，18765无监听，游戏进程不存在，测试存档无残留。备份、journal、未知结果及原始日志均保留本机。

`artifacts/m3b/live/protection-check.json`核对以下SHA256前后一致：

| 原数据 | SHA256 |
|---|---|
| save_0.es3 | 06130A952E024DD113747EB563459F0395886F96C8092BC229767B6FA13B95F7 |
| save_0 day5历史备份 | DA891F60E6FB70EC6F1553148C2D5CD3C547F5394AD93E897E32583DE662B1BE |
| save_0 day6历史备份 | 488D48FDF1C695996D6B1DD5DB84A11FC0C1D19167033B91AA692330E0408F94 |
| Assembly-CSharp.dll | 6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5 |

正常游戏操作更新了saves_index、Player/Player-prev日志，并轮换移除了两份旧GameLog；初始20份根目录文件备份仍在`artifacts/m3b/live/backup-before`。本轮正常Mod权限为network/reflection-external，开发者模式关闭；允许范围内的启停/信任记录和日志不作未经要求的回滚。安装及修复收据在同目录install-receipt*.json，清理游戏日志为player-cleanup.log。

## Git

本轮在feature/m3b-bargain开发，未合并main、未force push、未发布Release。提交范围仅源码、契约、测试、README、Skill和本报告，不包含游戏DLL、存档、密钥或原始日志。
