# Qwen Local 与 MemOS 共享模型服务实施计划

状态：可执行
设计来源：`docs/superpowers/specs/2026-08-05-shared-model-service-configuration-design.md`
批准边界：方案 A；`auto_start_on_demand=true`；带 PID 的原子锁文件为跨进程主合同；不推翻 Q6、18135、退出三选一、复用可停和进程路径核验。

## 目标与边界

把模型路径、别名、回环端口和 `llama-server` 启动参数从 Qwen Local 聊天设置与 MemOS 源码中抽出，统一放入 `runtime/model-service.json`。Qwen Local 与 MemOS 各自保留模型管理入口，但共同遵守同一配置、规范化摘要、原子启动锁、身份检查和脱敏生命周期日志合同。真实 `recall`/`remember` 可以按需启动；`health`/`list_recent` 和后台维护不得启动。

不更换模型、量化或 llama.cpp，不新增 Windows 服务，不开放非回环监听，不启用云端回退，不改变退出三选一，不清理用户记忆或聊天数据。

## 文件职责地图

| 文件 | 职责 |
|---|---|
| `.gitignore` | 忽略真实模型服务配置、锁、日志、构建产物和本机数据；允许提交示例配置 |
| `runtime/model-service.example.json` | 无机器私有内容的 schema v1 示例 |
| `runtime/model-service.json` | 本机唯一模型服务配置；运行时生成/迁移，禁止提交 |
| `local-chat/src/QwenLocalChat.Core/ModelServiceConfiguration.cs` | C# 配置模型、严格校验、路径解析、规范化 SHA-256、原子保存和旧设置迁移 |
| `local-chat/src/QwenLocalChat.Core/ModelServiceStartLock.cs` | C# `FileMode.CreateNew` 启动锁、PID 存活检查、过期锁回收和有界等待 |
| `local-chat/src/QwenLocalChat.Core/ModelServiceLifecycleLog.cs` | C# 脱敏 JSONL 生命周期事件 |
| `local-chat/src/QwenLocalChat.Core/ModelServiceIdentity.cs` | 回环监听 PID、规范化进程路径与模型别名核验；输出 verified/partial/mismatch |
| `local-chat/src/QwenLocalChat.Core/AppPaths.cs` | 只发现项目根及配置/锁/日志位置，不再固定具体模型文件 |
| `local-chat/src/QwenLocalChat.Core/SettingsStore.cs` | 聊天设置与模型字段分离；旧字段仅供一次迁移，新保存不双写 |
| `local-chat/src/QwenLocalChat.Core/QwenServiceManager.cs` | 进程内门禁外增加跨进程锁、二次健康/身份检查和生命周期回执 |
| `local-chat/src/QwenLocalChat.Core/WindowsModelProcessLauncher.cs` | 完全按共享配置构造参数，保留停止前路径/模型核验 |
| `local-chat/src/QwenLocalChat.Core/MemosStdioClient.cs` | 删除 `MEMOS_REUSE_LLM_ONLY=1` 硬禁止；继续移除云端密钥 |
| `local-chat/src/QwenLocalChat.WinUI/MainPage.xaml.cs` | 启动时迁移并组合两份设置；保存时拆分；沿用现有重启与退出行为 |
| `local-chat/src/QwenLocalChat.WinUI/RuntimeSettingsPanel.xaml.cs` | 模型字段仍可编辑；重置不注入写死模型值 |
| `local-chat/src/QwenLocalChat/MainForm.cs`, `SelfTest.cs` | 旧 WinForms/自检入口同样读取共享配置，消除第二套常量 |
| `scripts/model-service.mjs` | Node 配置、规范化摘要、身份检查、原子锁、启动、日志和可测试依赖边界 |
| `scripts/mcp-server.mjs` | 在真实 LLM 工具前 ensure；只读工具不启动；把共享 endpoint/model 注入 MemOS 解析配置 |
| `scripts/model-service.test.mjs` | Node 配置/参数/锁/按需策略回归测试 |
| `scripts/smoke-test.mjs`, `package.json` | 增加共享配置检查与 `npm test` 入口 |
| `local-chat/tests/QwenLocalChat.Tests/Program.cs` | C# 配置迁移、无双写、参数、锁、身份和退出回归测试 |

## 全局接口合同

- 当前生产配置路径：`D:\codex\experiments\memos-local-codex\runtime\model-service.json`；代码按发现到的项目根组合相同相对位置，测试可用 `MODEL_SERVICE_CONFIG_PATH` 覆盖完整路径。
- schema v1 字段严格采用批准设计中的 13 个字段；未知 schema、未知字段、非回环 host、非法端口、空别名、缺失可执行文件或模型文件均失败，不使用安全默认值猜测。
- 相对路径统一以项目根解析；Windows 身份比较使用规范化绝对路径和不区分大小写语义。
- 配置摘要为按键名递归排序、UTF-8、无额外空白 JSON 的 SHA-256；C# 与 Node 对同一文档必须产生相同摘要。
- 锁路径固定为 `runtime/locks/model-service-start.lock`，字段只含 schema、PID、受控客户端、UTC 时间和配置摘要。
- 日志路径固定为 `runtime/logs/model-service-lifecycle.jsonl`；不得写提示词、回复、记忆正文、明文完整路径、密钥或环境变量。
- MemOS 配置先从 `runtime/config.yaml` 解析，再以内存副本覆盖 `llm.endpoint` 和 `llm.model`；不回写 YAML，不形成第二份人工配置。

## Task 1：建立可回滚源码基线与配置合同

**依赖：** 已有本地 Git，设计提交 `46830a7`。

**Consumes：** 当前 `.gitignore`、`local-chat/data/settings.json`、项目根定位规则。
**Produces：** 可审计源码基线；C#/Node 可共享的 schema v1 和真实本机配置迁移结果。

1. 扩充 `.gitignore`，明确忽略 `runtime/model-service.json`、`runtime/locks/`、`runtime/logs/`、`local-chat/data/`、`local-chat/diagnostics/`、`**/bin/`、`**/obj/`、发布 exe/lnk；只放行 `runtime/model-service.example.json`。
2. 在任何产品代码修改前，精确暂存 `.gitignore`、根文档与包清单、`scripts/`、`local-chat/src/`、`local-chat/tests/`、构建脚本和必要设计文档，运行 `git diff --cached --check` 与敏感信息扫描，提交本地源码基线；不得暂存模型、runtime 数据、聊天数据、日志、诊断包、node_modules 或 vendored package-static。
3. 先在 `local-chat/tests/QwenLocalChat.Tests/Program.cs` 添加失败用例：合法/非法 schema、非回环拒绝、相对路径解析、C# 规范化摘要固定向量、旧 `settings.json` 迁移和新保存不再含模型字段。
4. 新建 `ModelServiceConfiguration.cs`，实现 `ModelServiceConfig`、`ModelServiceConfigStore.Load/Save/LoadOrMigrate`；保存使用同目录临时文件、flush、原子替换，失败保留原文件。
5. 新建示例配置并以现有 Q6/18135 设置生成被忽略的真实配置；迁移后回读校验。旧聊天设置只读一次，成功后由 `SettingsStore.Save` 清除旧模型字段。

**命令：**

```powershell
dotnet run --project local-chat/tests/QwenLocalChat.Tests/QwenLocalChat.Tests.csproj -c Release
node --test scripts/model-service.test.mjs
git check-ignore runtime/model-service.json runtime/model-service.example.json
```

**预期与证据：** 红灯先因配置类型/迁移缺失失败；实现后固定向量一致，真实配置被忽略、示例可跟踪，聊天设置重新保存后不再出现模型服务字段。

## Task 2：Qwen Local 接入配置、锁和身份合同

**依赖：** Task 1 配置 API。

**Consumes：** `ModelServiceConfig`、现有 `QwenServiceManager`、退出三选一与设置保存流程。
**Produces：** Qwen Local 只按共享配置启动/复用/停止，跨进程竞争时至多一个启动者。

1. 先添加失败用例：两个 manager 竞争同一锁只启动一次；健康目标路径匹配可复用；路径不符拒绝复用和停止；别名不可取返回 partial；死 PID 锁可回收、活 PID 锁不可删；启动参数逐字段来自配置。
2. 新建 `ModelServiceStartLock.cs`、`ModelServiceIdentity.cs`、`ModelServiceLifecycleLog.cs`。锁等待使用有界 250 ms 轮询，取得锁后再次探测；只有 owner PID 不存在且服务不健康才回收。
3. 修改 `AppPaths`、`QwenServiceManager` 和 launcher，删除具体模型名与端口常量。健康复用必须同时检查回环监听 PID、server 路径和 `/v1/models` 别名；别名接口不可用可 partial 复用，但路径 mismatch 必须拒绝。
4. 修改 WinUI `MainPage`：启动时加载/迁移模型配置并与聊天设置组合；保存时先校验并原子保存模型配置，再保存聊天设置；失败恢复两份原内容。拥有模型时沿用现有重启，复用时提示下次模型重启生效。
5. 修改设置面板重置逻辑，使聊天默认值可重置而模型字段沿用当前共享配置；修改 WinForms `MainForm` 和 `SelfTest` 使用同一配置入口。
6. 保留 `MainWindow`/`AppCloseCoordinator` 三选一以及 `StopServiceAsync` 的停止前路径复核；为 start/reuse/stop/recover_lock 写脱敏日志。

**命令：**

```powershell
dotnet run --project local-chat/tests/QwenLocalChat.Tests/QwenLocalChat.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File local-chat/tests/verify-winui-ui-baseline.ps1
dotnet build local-chat/src/QwenLocalChat/QwenLocalChat.csproj -c Release --nologo
dotnet build local-chat/src/QwenLocalChat.WinUI/QwenLocalChat.WinUI.csproj -c Debug -p:Platform=x64 -p:WinAppRunNoLaunch=true --nologo
```

**预期与证据：** 全部测试/构建通过；源码启动路径中不再依赖具体 GGUF 名或固定 18135；退出按钮与事件绑定未改变；日志事件只有合同字段和哈希。

## Task 3：MemOS 改为真实调用按需启动

**依赖：** Task 1 配置合同；Task 2 锁/身份语义。

**Consumes：** `runtime/config.yaml` 的 MemOS 算法/存储策略、共享模型配置、MCP 工具请求。
**Produces：** Node 与 C# 等价的 ensure 行为；MCP 启动和只读工具不唤醒模型。

1. 先新建 `scripts/model-service.test.mjs` 失败用例：配置校验和固定摘要向量、完整启动参数（含 reasoning/jinja/parallel）、健康二次检查、并发锁、死 PID 回收、`auto_start_on_demand=false`、身份不符拒绝、日志脱敏。
2. 新建 `scripts/model-service.mjs`，所有文件系统、fetch、spawn、PID/监听查询边界可注入；Windows 监听 PID 使用 `netstat -ano -p tcp`，进程路径用固定 PowerShell 命令读取并规范化，任何查询失败只能返回 partial/错误，不能误杀。
3. 修改 `mcp-server.mjs`：删除具体 GGUF、alias、18135、reasoning off 和 `MEMOS_REUSE_LLM_ONLY` 分支；启动 MCP 时只加载配置并初始化 embedding/core，不 ensure 模型。
4. 通过插件公开配置加载器读取 YAML，构造内存副本覆盖 `llm.endpoint`/`model` 后传给 `bootstrapMemoryCoreFull({config,...})`；强制 `fallbackToHost=false`，继续删除三个云端 API key。
5. 仅在 `remember` 和 `recall` 进入 core 的 LLM 路径前调用 ensure；`health` 只报告 config/core/embedding/model 分项，`list_recent` 只读。设置 `autoRecovery:false`，避免启动后的后台恢复单独唤醒模型。
6. 更新 smoke test 与 npm 脚本；结构化错误至少包含稳定 `error_code` 和可读 message，不把启动失败伪装成功。

**命令：**

```powershell
npm test
npm run verify:embedding
node --check scripts/model-service.mjs
node --check scripts/mcp-server.mjs
rg -n "Qwen3\.5-9B.*Q4|MEMOS_REUSE_LLM_ONLY|--reasoning.*,.*off|const llamaHealthUrl" scripts local-chat/src
```

**预期与证据：** Node 测试先红后绿；扫描不再发现旧 Q4 或硬禁止；只读工具路径不调用 ensure；启动参数与共享配置逐项一致。

## Task 4：双客户端联调、部署和回滚证据

**依赖：** Task 1–3 全绿。该任务会启动/停止本地模型，但不修改记忆正文以外的数据；隔离验收记忆完成后精确清理。

**Consumes：** 当前 Q6 配置、Qwen Local WinUI、Codex MemOS MCP、生命周期日志。
**Produces：** 单进程、按需恢复、退出行为、隐私边界和无残留的当前证据。

1. 先记录基线：`llama-server` PID/路径/命令行、18135 监听、Qwen/MCP 进程和显存；拒绝非回环监听。
2. 运行 C#、Node、embedding、UI baseline 与构建全套；发布/注册 WinUI 时只停止精确 AppX 输出路径的应用进程，不停止模型。
3. 验证冷启动 Qwen 只出现一个 llama；另起 MemOS recall 复用；再停止模型并同时触发 Qwen ensure 与 MemOS recall，最终仍只有一个匹配路径进程。
4. 逐项操作退出三选一：取消不退出；仅退出保留监听；停止并退出释放监听和显存。停止后 `memos_health`、`memos_list_recent` 不唤醒，真实 recall 唤醒并成功。
5. 写入唯一标记的隔离验收记忆，完成同义召回、数据库/日志/监听核验后，用现有精确清理接口删除该标记对应数据；不得广泛清理历史。
6. 审计生命周期 JSONL 不含提示词、记忆正文、明文路径和密钥；检查无云端连接、无第二模型、无孤儿 MCP/llama 进程。
7. 精确暂存源代码、示例和计划，运行 diff check、敏感信息扫描与全量测试后本地提交。用户未授权 push，本阶段不推送。

**命令：**

```powershell
npm test
dotnet run --project local-chat/tests/QwenLocalChat.Tests/QwenLocalChat.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File local-chat/deploy-winui.ps1
Get-NetTCPConnection -State Listen | Where-Object LocalPort -eq 18135
Get-CimInstance Win32_Process | Where-Object Name -in @('llama-server.exe','node.exe','QwenLocalChat.WinUI.exe') | Select-Object ProcessId,Name,ExecutablePath,CommandLine
git diff --check
```

**预期与证据：** 真实 recall/remember 可按需恢复；只读调用不启动；退出三选一保持；任何时刻最多一个匹配配置的 llama；仅回环监听；测试记忆已精确清理；工作树只含预期源文件，生产配置/日志/数据未进入 Git。

## 自检结论

- 规格覆盖：D01–D23 均映射到配置、UI、锁、身份、日志、按需调用或验收任务。
- 接口一致：C# 与 Node 使用同一路径、字段、规范化摘要、锁内容和日志枚举；`runtime/config.yaml` 只作为 MemOS 策略源，endpoint/model 仅在内存覆盖。
- 授权边界：已授权本地实现、测试、必要启动/停止和本地 Git；未授权远端 push、云端回退、模型更换、广泛数据清理或新增常驻服务。
- 无占位符：路径、环境变量、文件职责、命令、预期结果和验收证据均已明确。
