# PawnPilot — M1

《深空当铺：可能是偷的》的本机 MCP 工具链。仅支持基础观察和通过原生 EscapeUIManager.OnResume 关闭已打开的 ESC 菜单；不提供交易、自主经营或任意方法执行。

Codex → MCP stdio Adapter → loopback HTTP broker ← Unity 主线程主动轮询 Mod。

Adapter 与 broker 独立进程；Adapter 不启动 broker，Codex 退出不销毁结果。保留 M0 目录及安装 ID pawnpilot_m0，桥接版本 0.1.0。[M0 证据](docs/m0-feasibility.md)保留，[M1 报告](docs/m1-implementation.md)记录实际验收。

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

pyproject.toml 固定官方 MCP SDK 2.3.0、Pydantic 2.12.5；uv.lock 固定传递依赖及哈希。本次官方 PyPI 下载过慢、GitHub TLS 失败，锁文件使用清华 PyPI 镜像。setup 使用 frozen 锁文件，缓存及 .venv 均在项目内。SDK v2 正式入口名为 MCPServer。

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

只安装 DLL、manifest、session.json，并保留清理收据。游戏内正常启用、批准 network 后重启，不使用开发者信任模式。

将[配置示例](docs/codex-config.example.toml)的表合并进项目 .codex/config.toml，填写绝对路径后新开 Codex 会话。不覆盖全局配置。stdio 命令为项目 .venv\Scripts\python.exe -m pawnpilot_mcp，cwd 为仓库；可用 PAWNPILOT_CONFIG 指定密钥文件，端口仍固定。broker 不在线也可注册工具、返回结构化不可连接状态。

## MCP 契约

| 工具 | 行为 |
|---|---|
| health_check | 无参数；连接、兼容性、loaded/ready、阻塞原因；health_check.can_act 表示当前 close_menu 可用 |
| observe | 无参数；快照、legal_actions、等待/人工处理原因；失联快照是历史数据 |
| act | command_id、action="close_menu"、broker_epoch、expected_world_epoch、expected_revision、expected_version；提交后返回状态 |
| get_action_status | 原 command_id；规范状态、before/after、原 broker epoch、人工对齐证据；not_found 不证明执行失败 |

参数和返回结构都有 JSON Schema。业务错误结构化返回，区分 broker、protocol、connection、configuration、internal。日志不污染 MCP stdout。[Agent Skill](.agents/skills/pawnpilot-player/SKILL.md)规定一次一个动作、未知先查询、人工暂停不得自行恢复。

schema_version=1 是结构版本；broker_epoch 每次 broker 启动改变；session_id 是 Mod 进程身份；world_epoch 在场景切换、载入 Hook 或存档身份改变时更新；revision 随可见快照变化递增；version 必须等于 world_epoch:revision。

loaded 表示当前游戏场景中存档已初始化；ready 额外要求必要 UI/库存组件存在并稳定两个 Unity Update 帧。动作前再次核对身份和状态。loaded=false 时现金 0 是无效占位。两个 can_act 字段作用域不同：health_check.can_act 表示当前 close_menu 可用；observe.snapshot.can_act 保留 broker 门禁可用性，具体合法动作以 legal_actions 为准。菜单已关闭且没有其他阻塞时，前者 false，后者 true，而 legal_actions 为空。

## 命令状态与恢复

| 状态 | 含义 |
|---|---|
| pending | 已持久化、未交付；到期或状态改变则拒绝 |
| running | 已先落盘再交付，可能已执行；不会再次交付 |
| succeeded | C# 后置条件验证成功 |
| rejected | 未通过门禁/未交付；重新观察后才考虑新动作 |
| failed | 已报告错误，不能据此假设没有副作用 |
| outcome_unknown | 结果未知，阻塞新的写动作，只能查询原 ID |

同 ID 同完整内容返回已有状态；内容不同明确拒绝。HTTP 超时或提交 5xx 按未知处理，查询原 ID，不能换 ID 重试。

journal 使用独占进程锁、同目录临时文件、flush/fsync 与原子替换。命令先落盘后交付，结果先落盘后确认。重启 pending 转 rejected，running 转 unknown；原 Mod 保留结果可按原 epoch/session 归档，不重放。已完成结果跨重启可查。上限 4096 条/16 MiB，满载、损坏、写失败停止写入，不淘汰 ID。此方案不宣称 exactly-once，不能抵御人为删除记录或断电磁盘损坏。

若 Mod 退出使结果丢失，不能删除 journal 清零。操作者核对当前世界、正常关闭菜单、重新 observe 并记录画面证据。原命令已到期、快照新鲜就绪且版本前进后，才可执行：

~~~powershell
python bridge/client.py reconcile --id '<原ID>' --broker-epoch '<当前broker>' --world-epoch '<当前world>' --revision '<当前revision>' --expected-version '<当前world>:<当前revision>' --operator-note '<人工核对世界、菜单及证据位置>' --confirm-no-replay
~~~

将示例占位符全部替换为刚观察到的真实值，revision 必须是整数，expected-version 必须与同次观察一致。该入口不暴露给 MCP；保留 outcome_unknown，另存人工对齐证据后解除门禁，不声称原动作成功。迟到有效结果仍可归档；旧 ID 不会重放。无法核对则继续阻塞。

## 验证与清理

~~~powershell
.\.venv\Scripts\python.exe -m unittest discover -s tests -p 'test_*.py' -v
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet'
$env:NUGET_PACKAGES = Join-Path $PWD '.nuget'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet run --project tests/PawnPilot.CoreTests/PawnPilot.CoreTests.csproj -c Release
# 真正 Codex CLI，默认只读，需要已登录账户：
python scripts/verify-codex.py --label read-check
# 仅用于已批准的独立测试槽、正常打开的 ESC 菜单：
python scripts/verify-codex.py --label live-check --close-menu
~~~

验证脚本使用临时 CLI 配置，只对本次已授权的 PawnPilot 调用自动批准，不改全局设置。默认模型是本次账户可用的非 Astra gpt-5.6-luna，可用 --model 指定其他可用非 Astra 模型。证据位于 artifacts/m1/codex，同名标签不覆盖。Windows 沙箱若拒绝 SDK stdio 管道（WinError 5），在正常终端运行，不能把沙箱失败算 PASS。

结束时在游戏 UI 删除本次测试槽、禁用 Mod、正常退出，再按收据清理：

~~~powershell
./scripts/install-mod.ps1 -GameRoot 'E:\SteamLibrary\steamapps\common\Probably Stolen' -Action Remove -Apply
# broker 终端 Ctrl+C；保留配置及 journal 用于后续结果查询。
~~~

清理只删除收据哈希匹配的三个文件和空目录。原存档、其他 Mod、游戏本体不改动。缓存、密钥、journal、日志、artifacts 中的截图和备份、存档、调查副本均由 Git 忽略。

## 版本管理范围

上传源码、测试、构建/安装脚本、依赖锁文件、配置示例、Agent Skill 和 docs 中的脱敏验收摘要。原始实机证据与备份保留在本机 artifacts；游戏程序集、local.props、真实 session.json、broker journal、虚拟环境及工具缓存不上传。本机 .codex 配置也忽略，使用 docs/codex-config.example.toml 分享配置模板。

图片和压缩包不按扩展名一概忽略，便于后续纳入合法项目资源；测试截图、下载包和发行产物应放入 artifacts、.tools 或 dist 等已忽略目录。不要使用 git add -f 上传这些本机资料。
