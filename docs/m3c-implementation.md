# M3-C：人工商品检查接管

结论：**M3-C PASS（人工接管、明确确认、MCP恢复及防重复购买路径）**。真实 Codex CLI 经 PawnPilot MCP 完成接管观察与恢复；用户人工处理后商品已自有、顾客已离场，Agent重新观察并停止，没有重复购买。本轮未执行 Agent 恢复后的正常购买，不能将人工阶段的成交算作 MCP 自动购买验收。

基线：`feature/m3b-bargain` / `a63a9b7aa156d136707350494216418c78fcc9e2`，本地与远端一致，初始工作区干净。开发分支 `feature/m3c-inspection-handoff`。实际上轮基线为 436 managed + 168 Python；任务中的 166 Python 是动作版本提示修复前的数量。

## 已核实的商品标识

当前 `Assembly-CSharp.dll` SHA-256：`6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5`。用当前 DLL 的注册项、工厂初始化和原生本地化 shared entry ID 联接核实名称；不是根据中文猜 identifier。本机静态证据保存在 `artifacts/m3c/research/identifiers.md`、`item-identifiers-il.txt`、`instance-and-inspection-il.txt`、`localization-selected.txt`，不提交游戏 DLL 或原始日志。

| identifier | 当前中文名称 | 核实依据 |
|---|---|---|
| `packet_red_cigarette` | 一包海明威香烟 | `AmenitiesItemDirectory.PacketCigaretteRed`，`InspectableHelper.InitInspectableItem`；真/假/伪装预制均使用同一目录键 |
| `water_pitcher` | 水罐 | `AmenitiesItemDirectory.WaterPitcher`，`WaterHelper.InitLiquidContainerItem` |
| `water_jug` | 桶装水 | `FoodItemDirectory.WaterJug`，液体容器初始化及原生 `WaterBottleIds` |
| `bottled_water` | 瓶装水（标准） | `FoodItemDirectory.BottledWater`，同上 |
| `small_bottled_water` | 瓶装水（小） | `FoodItemDirectory.SmallBottledWater`，同上 |
| `large_bottled_water` | 瓶装水（大） | `FoodItemDirectory.LargeBottledWater`，同上 |
| `mini_bottle` | 瓶装水（微型） | `FoodItemDirectory.MiniBottle`，同上 |

历史“水罐”是 `water_pitcher`，不能误对应为 `water_jug`。香烟真假和水质/误标变体沿用同一物品 identifier，由其他实例特征表示；分类器不读取这些特征。`georgio2_cigarette1/2` 是故事唯一标记，不是物品 identifier。

本轮不纳入 `bottled_water_premium`、`water_ration`、`water_ration_small`，其已检查工厂没有同样的液体检查初始化证据；也不纳入色卡、指南、试纸和扫描工具。不根据名称含“水”或“海明威”扩大分类，不声称覆盖全部检查类商品。

## 实现边界

`BusinessReader` 仅对原有可见、父级/容器匹配的真实 `GameItem` 读取 `identifier`。其 getter 只是读取 backing field。该值留在进程内分类输入，不增加到 MCP 商品 DTO；不调用工厂、鉴定、随机、水质、真实价格或隐藏真假 getter。

只检测当前 NPC SELL、前柜/后柜的非自有来货。可选 `business.details.inspection_handoff` 记录等待/已恢复、handoff 引用、触发时顾客和商品引用、可见名称及通用原因。用户手动买入或遣走后仍保持等待；触发引用是历史记录，不要求还在当前柜台。

等待时只保留 `resume_inspection_handoff(handoff_ref)` 写动作；其他经营写入在合法动作、执行门禁和进入原生操作前被阻断。只读工具保持可用。不暂停 Unity、打开 ESC、控制放大镜或禁用玩家输入，也不取消已进入原生执行的命令。

恢复只修改 Mod 内接管记录，不调用游戏经济逻辑。`last_resumed_handoff_ref` 提供已确认状态证据，即使新商品立刻触发下一次接管，也能核对前次恢复。按世界、实际顾客到访和商品对象去重；新实例、新顾客或新世界重新判断，不建立跨存档真假知识库。broker 的 `manual_action_required`、未知结果阻塞和 journal 保持独立。

handoff_ref 使用每个 Projection 的随机 namespace 和递增序号，避免 Mod 重启后旧确认误匹配新接管。保留全部商品引用；名称累计超过 2048 字符时仅将超额名称置 null，避免大批商品的重复标题使等待状态无法传输或恢复，不放宽原有消息大小限制。

Codex 必须提醒用户在游戏内手动检查，并等待本会话明确“检查完成，继续”。普通实机授权不等于这次恢复确认。恢复后重新 observe；已购买或已离场时不重复成交。Agent Skill 和真实 CLI 验证脚本分别约束这一流程。

测试构建 `scripts/build.ps1 -M3CInspectionTest` 输出独立 `artifacts/m3c/package/pawnpilot_m0`。只有该编译条件才调用一次性触发路径：选择当前真实普通 NPC SELL 的完整待购报价中的商品，使用相同接管状态和门禁。正式构建关闭，不提供任意触发 MCP 动作；不改 identifier、价格、归属、库存、顾客或存档。安装与清理由 `install-mod.ps1 -M3CInspectionTest` 的独立收据管理，仍需本轮实机授权。

## 验证与实机

托管自动化：Release **506 passed / 0 failed**，证据 `artifacts/m3c/research/managed-tests.log`。覆盖七个精确标识、同家族真伪共用键、真实普通商品及检查工具负例、NPC BUY/自有商品排除、前柜无报价触发、手动离场、新实例/到访/世界、重复确认、原生执行中出现接管、旧引用/去重及既有购买/卖出/整理/议价回归。

Python 全套 **178 passed / 0 failed / 0 skipped**（84.079 秒），连同托管共 **684 项通过**。证据 `artifacts/m3c/research/python-tests.log`。专项 **10 passed**，其中 fullchain 用 Release 托管导出器为七个 identifier 分别生成生产 Projection 快照，再通过真实 MCP stdio 逐一 observe 并拒绝购买请求，最后验证恢复确认和新批次再次接管。首轮全套 178 项中此链曾失败，原因是误用了 nonce 修复前的旧 Release 导出器；重建后专项及最终全套均已通过，失败日志保留为 `python-tests-stale-exporter.log`，不把旧产物失败计作通过。

正式包和测试包构建均为 0 errors。NuGet 漏洞源因本机网络不可达产生 NU1900 警告，未阻止编译；没有更改网络/安全设置。静态检查最终 DLL：正式包 `ProbeBehaviour.Capture` 对测试触发器的调用为 **0**，M3-C 专用测试包为 **1**（`package-checks.json`）。Skill validator 通过，CLI 脚本语法检查通过。

真实 CLI 分为 `--m3c HANDOFF` 和 `--m3c RESUME --confirmed-handoff <ref> --max-spend <授权上限>`。第一阶段检测等待后退出并提醒；主会话等待真人确认后才能运行第二阶段。不能在同一提示中模拟用户已经确认。

用户已在本会话批准完整 M3-C 范围：最多2槽、每槽4位普通顾客/24次写请求、全轮一次测试 handoff、最多一笔正常购买/支出550，含两次预期拒绝的门禁探测。Computer Use 仅用于启动、槽位准备、普通 Mod 权限及观察/清理；不代替 MCP 接管或交易，也不代替用户检查。已备份26个原存档/配置/日志文件并逐一核对备份哈希；原 `save_0` 及两份历史备份与上轮基线一致。Steam一度显示运行中但无游戏进程/窗口，使用已安装exe正常启动，未强杀其他进程。

实机阶段一（`artifacts/m3c/codex/slot16-handoff.jsonl`，真实 Codex CLI `gpt-5.6-sol` + PawnPilot MCP）：独立 `save_16`，学徒/普通/无额外特质/正常跳过开场。Mod 普通 network/reflection-external 启用，开发者模式关闭。CLI 自主开门、接待一位拾荒客；首次来货动画中 prepare 因旧版本被拒，重新观察后第二次准备成功。没有用桌面点击接待/商品准备。正常报价285，现金550，5件待购：奥克西莫注射器、免疫宁注射器、滤水器、化学用品（普通）、刮刮乐彩票。

一次性测试 handoff 选择真实“奥克西莫注射器”（item-36），reason=`test_inspection_handoff`，ref=`inspection-handoff-69165b57ed2241c98e39bdc5f9125966-1`。revision15的真实 observe 返回 waiting_for_user，唯一合法动作 resume，购买/议价/遣走/接待消失。CLI停止并输出提醒，主会话也明确提醒用户手动处理并回复“检查完成，继续”。用户随后在本会话实际回复 **“检查完毕，继续”**，这是本次明确恢复确认，没有模拟或自行推定确认。

门禁实机证据 `artifacts/m3c/live/gate-probes.json`：通过真实 MCP stdio 分别提交零支出上限购买探测及 open_bargain_panel 探测，均明确返回 `inspection_handoff_pending`，对应 get_action_status 为 not_found，未进入命令队列。没有提前恢复或成交。

实机阶段二（`artifacts/m3c/codex/slot16-resume.jsonl`）：收到用户确认后才运行单独 RESUME 阶段。首次新 observe（revision20）仍为 waiting_for_user，现金已为324、顾客 absent，五件商品均为 owned=true。说明人工操作未被 Mod 禁用，且人工处理、顾客离场没有自动解除接管。现金较触发时减少226；不将这一人工阶段变化归因于 MCP，也不声称它是原价285的普通购买。

命令 `slot16-resume-01` 通过 MCP 提交精确 handoff_ref，结果 succeeded / postcondition_observed；revision20→21，last_resumed_handoff_ref与本次确认一致。之后 CLI **再次 observe**，status=resumed、requires_manual_inspection=false，现金仍324；五件商品保持自有，顾客仍 absent，正常接待/整理合法动作重新出现。CLI没有提交 purchase、receive 或 sort，只报告当前状态后停止。证据也保留在 `artifacts/m3c/live/resume-observed.json`。

累计：**1个测试槽、1位顾客、7次MCP写请求**（开门1、接待1、prepare2含一次stale拒绝、门禁拒绝探测2、resume1），一次测试强制触发；Agent实际成交0笔、支出0。手动处理阶段现金550→324，恢复动作前后324不变。不继续创建第二槽或消耗另一个顾客样本。

## 清理与原文件保护

按本轮已批准范围，通过游戏 UI 删除 `save_16`，载入列表只保留原第7天进度；未加载原进度。UI停用 PawnPilot，开发者模式始终关闭，正常退出游戏。按本轮收据逐文件核对哈希后移除 DLL/manifest/session 和空安装目录；本轮 broker 已按记录的PID与命令行停止，127.0.0.1:18765 无监听。清理脚本首次遇到 conhost 子进程时停止而未误杀；只读确认后仅停止属于本轮的Python broker父子进程。

`artifacts/m3c/live/protection-check.json` 最终核对：原存档、两个历史备份、游戏程序集均不变；测试槽/Mod已移除，游戏/broker已退出。保留26文件备份、测试槽本机副本、journal和原始CLI/MCP/游戏日志，不提交这些原始文件。游戏正常运行更新了索引和Player日志，轮换了一个旧GameLog及旧Unity遥测缓存；原文件均在启动前备份中，没有为还原日志去覆盖用户设置。

| 受保护文件 | 最终SHA-256（与启动前一致） |
|---|---|
| `save_0.es3` | `06130A952E024DD113747EB563459F0395886F96C8092BC229767B6FA13B95F7` |
| `save_0_day5_20261031-060251.es3` | `DA891F60E6FB70EC6F1553148C2D5CD3C547F5394AD93E897E32583DE662B1BE` |
| `save_0_day6_20261031-112207.es3` | `488D48FDF1C695996D6B1DD5DB84A11FC0C1D19167033B91AA692330E0408F94` |
| `Assembly-CSharp.dll` | `6398D44AA1675E1F02B63BC668868F970E0F5D3AE5A50F2ACED8D06CA084E3B5` |

## 覆盖边界与遗留

尚无海明威香烟或六种水容器的独立实机触发样本；确定性 fixture 和七ID真实MCP自动化证明生产分类链路，本轮任意商品实机接管不证明该测试商品本身需要鉴定。

本次实机覆盖“用户手动处理并离场→确认→恢复→不重复买入”。“用户只检查、留着商品→恢复后Agent正常购买”尚未取得实机样本；原普通买入、卖出、默认整理、议价保持自动化回归，本轮没有重新实机成交这些路径。没有新增自动鉴定、批发、特殊顾客业务、GUI经营或跨日能力。

Git 提交与推送：待完成。
