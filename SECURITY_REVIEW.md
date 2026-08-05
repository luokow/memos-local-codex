# 本地 MemOS 安全审查

审查对象是 `@memtensor/memos-local-plugin@2.0.12`、MCP 适配器、`llama.cpp b10208`、Qwen3.5-9B Uncensored HauhauCS Aggressive Q4_K_M、保留的 Qwen3-4B 回滚模型以及本地向量模型。目标是确认日常运行时只在 D 盘与本机回环地址内工作。

## 威胁模型

- **资产**：Codex 对话内容、长期记忆数据库、记忆提炼结果、本地模型与原生运行库、用户级 Codex 配置。
- **入口**：Codex 通过 stdio 传入的 MCP 工具参数；本地 `llama-server` HTTP 接口；安装阶段下载的 npm 包、原生模块、模型与运行库。
- **信任边界**：Codex 到 MCP；MCP 到 MemOS；MemOS 到 SQLite、向量模型和 Qwen；安装阶段本机到 npm、GitHub 与 Hugging Face。
- **最坏影响**：对话外传、记忆库被篡改、恶意原生代码执行、服务暴露到局域网、用户级 Codex 配置被污染。

## 证据与结论

### 1. 默认遥测能力——已缓解

- **能力**：MemOS 包含遥测发送器，默认配置为启用，并含有 Aliyun ARMS 上报实现。
- **可达性**：若按包默认配置启动，正常初始化路径可到达该发送器。
- **执行证据**：本项目从未用默认配置启动；没有观察到默认遥测实际发送。
- **影响**：理论上会向外部服务发送匿名事件，和“完全本地”目标冲突。
- **缓解**：`runtime/config.yaml` 明确设置 `telemetry.enabled: false`，引导参数还传入 `telemetry: null`。
- **剩余风险**：低。后续升级 MemOS 时必须重新审查默认值与配置读取路径。

### 2. 包内云端 LLM/嵌入供应商代码——配置路径不可达

- **能力**：依赖包支持 OpenAI 兼容接口及其他远程供应商，因此源代码中存在公网端点能力。
- **可达性**：当前 LLM 端点固定为 `http://127.0.0.1:18135`，嵌入供应商固定为本地模型，`fallbackToHost`、Hub 均禁用；运行阶段向量库设置 `allowRemoteModels=false`。
- **执行证据**：最终验收将按进程 PID 检查 TCP 连接；只接受回环监听/连接。
- **影响**：配置被错误修改后可能恢复联网能力。
- **缓解**：配置和 MCP 入口固定在项目目录，运行前检查模型缓存完整；不读取云端 API Key。
- **剩余风险**：低到中。配置文件仍由当前 Windows 用户可写。

### 3. 原生二进制未做 Authenticode 签名——有执行、无恶意影响证据

- **能力**：`better-sqlite3` 原生模块和 `llama-server.exe` 能在本机执行原生代码。
- **可达性**：两者分别在数据库初始化和本地 LLM 启动时被调用。
- **执行证据**：SQLite 原生模块已成功执行查询；`llama-server.exe --version` 已成功执行。两者 Authenticode 状态均为 `NotSigned`。
- **影响**：如果供应链制品被替换，可能导致本机代码执行。当前没有恶意行为或影响证据。
- **缓解**：npm 包签名与证明已核验；生产依赖审计为 0；llama.cpp 与 Qwen 文件按上游公布 SHA-256 固定并校验；所有版本固定。
- **剩余风险**：中。上游未提供 Windows Authenticode 签名，文件哈希只能证明与上游制品一致，不能消除上游供应链风险。

### 4. 本地 HTTP 服务暴露——已缓解

- **能力**：`llama-server` 提供 HTTP API，MemOS 也包含本地查看器。
- **可达性**：服务端显式传入 `--host 127.0.0.1`；查看器配置为 `127.0.0.1` 且当前 MCP 路径不启动查看器。
- **执行证据**：验收时只接受 `127.0.0.1:18135` 回环监听。
- **影响**：若绑定 `0.0.0.0`，同网段其他主机可能调用模型或观察信息。
- **缓解**：启动参数硬编码回环地址，Hub 禁用。
- **剩余风险**：低。同一 Windows 用户上下文中的其他本地进程仍可访问回环接口。

### 5. 普通信息日志包含任务摘要——已缓解

- **能力**：即使 `logging.llmLog.enabled: false`，MemOS 的普通 `info` 日志仍可能记录任务摘要、最终回答和反思文本；实测行虽带 `_redacted: true`，内容仍可读。
- **可达性**：当 `logging.file.enabled: true` 且日志级别为 `info` 时，正常记忆提炼路径会到达这些日志语句。
- **执行证据**：合成验收短语曾出现在 D 盘 `runtime/logs/memos.log`；没有网络发送证据。
- **影响**：同一 Windows 用户下能读取该文件的本地进程可看到记忆内容副本。
- **缓解**：最终配置同时关闭 MemOS 文件日志与专用 LLM 日志。llama.cpp 诊断日志经搜索未发现验收提示文本。
- **剩余风险**：低。记忆正文仍按功能要求保存在本地 SQLite 数据库中。

## 供应链固定值

- `@memtensor/memos-local-plugin@2.0.12` tarball SHA-256：`FDE80A07A512697B2497F966B93BDF55537BFB5E7E8179A912810E674ED6EBB4`
- llama.cpp `b10208` CUDA 12.4 主包 SHA-256：`7A7BB3E942C315F400AE26DF5DA5297DA81C7BB63C3FB5B7F1B8492B111462BF`
- llama.cpp CUDA 运行库 SHA-256：`8C79A9B226DE4B3CACFD1F83D24F962D0773BE79F1E7B75C6AF4DED7E32AE1D6`
- 当前 Qwen3.5-9B Uncensored HauhauCS Aggressive Q4_K_M SHA-256：`2CA636D9E81D3D23CA9B60C234FE185D30EC082EEBA69CE770FDB0C76559A4F5`
- 当前模型上游仓库提交：`HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive@0a41c68809d375475f954be12ba7c40efa56c2a9`
- 保留的 Qwen3-4B Q4_K_M SHA-256：`7485FE6F11AF29433BC51CAB58009521F205840F5B4AE3A32FA7F92E8534FDF5`
- `better_sqlite3.node` SHA-256：`C4B95A94A963258A9EF41B12FD3BB86F0B08A2AC5C5B499FE94DE57A5443682D`

## 授权边界

动态执行仅用于本地模型、SQLite、MCP 和记忆端到端验收；不修改防火墙、沙箱、AppX、用户级 `.codex` 或其他 Codex 控制面状态。

## 原 Qwen3-4B 最终验收证据（2026-08-01）

- MCP 四个工具可列出并调用，`health.ok=true`。
- 本地 Qwen 与本地向量模型均记录成功调用时间，无回退、无错误。
- 写入合成中文记忆后，使用不同措辞召回成功；验收代号和 D 盘约束均命中。
- 运行期间 MCP 与 llama.cpp 只有回环已建立连接；非回环已建立连接为 0，监听地址仅 `127.0.0.1:11435`。
- 客户端退出后 MCP、llama.cpp 进程和 11435 端口数量均为 0。
- 关闭文件日志后的再次验收没有增加 `memos.log` 或 `error.log`；llama.cpp 诊断日志不含合成验收短语。
- npm 生产依赖漏洞为 0；离线向量验证通过；用户级 Codex 配置 SHA-256 保持 `9FE8D76B2AADC6C9AC220975AC4CE4D92E19F3B7299CAC6044F7CF225CEBE515` 且无 MemOS 条目。

## Qwen3.5-9B 替换验收证据（2026-08-01）

- 5,627,044,224 字节 GGUF 文件的完整 SHA-256 与 Hugging Face LFS 和腾讯云 CNB 镜像记录一致。
- `llama.cpp b10208` 在 8,192 上下文、全 GPU 层和关闭思考模式下约 4.45 秒完成加载；验收时整卡显存占用约 5,884 MiB，剩余约 2,065 MiB。
- 普通中文回答完整；类似 MemOS 的结构化提取返回有效 JSON。两次实际生成速度分别约 42.15 和 42.72 tokens/s。
- `npm run smoke` 通过：四个 MCP 工具可列出，`health.ok=true`，LLM/嵌入模型均有成功调用时间，记忆写入、演化刷新、换措辞召回和最近轨迹读取成功。
- 当前模型别名为 `qwen3.5:9b-uncensored-local`，服务端点为 `http://127.0.0.1:18135`；默认关闭思考模式，避免思考内容耗尽短输出预算。
- 社区作者关于“0/465 拒答”和“零能力损失”的说法未被本项目独立验证；本项目只确认本地运行、普通中文生成、结构化输出和 MemOS 端到端功能。
