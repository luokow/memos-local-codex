# 共享模型配置设计 — 确认清单结论

状态：**已批准、已实施并通过验收**
日期：2026-08-05
依据：

- 设计稿：`docs/superpowers/specs/2026-08-05-shared-model-service-configuration-design.md`
- 既有产品事实：`local-chat/docs/2026-08-04-session-ux-parallel-and-streaming-md-handoff.md`
- 运行参数边界：`local-chat/docs/2026-08-03-runtime-settings-and-long-reply-stability-design.md`
- 实施前核验：`local-chat/data/settings.json` 已是 Q6 + 端口 `18135`；`scripts/mcp-server.mjs` 当时仍硬编码已不存在的 Q4 文件名
- 关窗/复用/停模型等历史决定（Grok 工作区记忆与 08-04 handoff）

批准记录：用户于 2026-08-05 明确批准方案 A，并选择 `Q1=A`（`auto_start_on_demand=true`）与 `Q2=A`（锁文件为跨语言主合同）。

---

## 0. 读历史后的总判断

### 0.1 问题本质（与设计稿一致）

不是“模型坏了”，而是**双消费者配置真值分裂 + 生命周期半失效**：

| 侧 | 实施前状态 | 后果 |
|---|---|---|
| Qwen Local | `settings.json` 已切到 Q6、端口 18135、完整启动参数 | UI 正常、可复用/可停 |
| MemOS MCP | `mcp-server.mjs` 写死 Q4 路径与启动参数；启动时只 ensure 一次 | 模型停后 MCP 仍在，调用空端口；想重启也找不到文件 |

### 0.2 历史里已经“写死”的产品约束（本轮不得推翻）

这些来自 08-04 会话与 handoff，应直接继承为本结论的前提：

1. **退出三选一必须保留**：停止模型并退出 / 仅退出软件 / 取消；**复用态也要弹**，不能只看 `OwnsModel`。
2. **停模型必须路径可核验**：只杀“本机配置端口上、路径等于配置的 `llama-server.exe`”的进程。
3. **Qwen Local 是主可视化入口**：设置卡片分组、即时/需重启语义、并发槽位 = 可同时回复会话数。
4. **隐私边界不可放宽**：仅 `127.0.0.1`、无云端回退、无密钥继承。
5. **不引入常驻 Windows 服务**；安装包仍可继续延后。
6. **ContentDialog 禁用**（原生崩溃路径）——设置/退出仍用页内覆盖层。

### 0.3 架构方案

**确认采用方案 A**（共享配置 + 两端各自管理 + 跨进程锁），不采用统一 CLI 命令（B）或常驻管理服务（C）。

理由：关窗/复用/杀进程核验已在 C# 侧验证过；本轮目标是消掉配置分裂与 MemOS 半失效，不是重写生命周期。

---

## 1. 确认清单结论表（D01–D23）

图例：

- **采纳推荐**：按设计稿推荐值确认
- **采纳并细化**：方向对，实施时按“建议补强”执行
- **改用建议值**：相对设计稿推荐有更优选择

| 编号 | 确认点 | 结论值 | 判定 | 依据 / 说明 |
|---|---|---|---|---|
| **D01** | 是否建立单一共享模型配置 | **是** | 已确认 / 采纳 | C01–C02；消除双真值 |
| **D02** | 共享配置位置 | **`runtime/model-service.json`** | **采纳并细化** | 见 §2.1 |
| **D03** | Qwen Local 是否为模型设置可视化入口 | **是** | 采纳 | UI 已成熟；MemOS 不另做设置窗 |
| **D04** | 聊天设置移除模型服务字段并停止双写 | **是；一次性旧格式只读迁移** | 采纳 | 迁移源 = 现 `settings.json` 模型字段 |
| **D05** | MemOS 每次 ensure 是否重读共享配置 | **是** | 采纳 | 长期 MCP 进程不能缓存旧路径/端口 |
| **D06** | 停止模型后是否允许按需重启动 | **是**（受 `auto_start_on_demand` 控制） | 已确认 / **采纳并细化** | 见 §2.2（与 `MEMOS_REUSE_LLM_ONLY` 调和） |
| **D07** | 哪些操作允许启动模型 | **仅确实需要 LLM 的前台工具调用**（`memos_recall` / `memos_remember` 及未来同等语义工具） | 采纳 | 禁止“顺手 ensure” |
| **D08** | 后台重评分 / 维护重试是否唤醒模型 | **否** | 采纳 | 用户停模型 = 释放显存/CPU 的明确意图 |
| **D09** | `health` / `list_recent` 是否只读 | **是**；**不启动模型** | 采纳 | health 分项报告核心 / embedding / 端点 / 配置 |
| **D10** | 是否保留退出三选项 | **是** | 已确认 | 08-04 C1–C5 硬约束 |
| **D11** | 是否增加跨进程启动锁 | **是** | 采纳 | 进程内锁挡不住 C# + Node 双启 |
| **D12** | 锁实现 | **以带 PID 的原子锁文件为两端共同主路径**；C# 可**额外**使用命名互斥体作加速，但不得替代锁文件合同 | **改用建议值** | 见 §2.3 |
| **D13** | 复用前是否验证进程路径与模型身份 | **是** | **采纳并细化** | 见 §2.4 |
| **D14** | 配置非法是否允许安全默认继续启动 | **否**；明确失败 | 采纳 | 禁止静默回退旧模型/旧端口 |
| **D15** | MemOS endpoint/model 与共享配置衔接 | **运行时覆盖**（读共享配置后注入；不生成第二份人工真值） | 采纳 | `runtime/config.yaml` 可留占位，运行时以共享配置为准 |
| **D16** | 旧配置迁移 | **一次性：备份 → 迁移 → 回读验证 → 新版本不双写** | 采纳 | 禁止改名/复制 GGUF 伪装兼容 |
| **D17** | 全本地、回环、无云端回退 | **是** | 既有边界 | SECURITY_REVIEW 与既有删除 API key 逻辑保留 |
| **D18** | 是否新增常驻 Windows 服务 | **否** | 采纳 | 方案 C 否决 |
| **D19** | 是否替换现有 Qwen Local 模型管理器 | **否**；在其上接共享配置与锁 | 采纳 | 方案 A |
| **D20** | 按需启动失败时 Qwen Local 行为 | **本轮无记忆 + 明确提示；主聊天继续** | 采纳 | 仅当 `use_memos=true` 且本轮需要记忆时 |
| **D21** | 按需启动失败时 Codex 直连 MemOS | **结构化错误，不伪装成功** | 采纳 | MCP `isError` + 可读原因（配置/端口/文件/锁超时） |
| **D22** | 是否记录启动/停止回执 | **是**；写 `runtime/logs/model-service-lifecycle.jsonl` | **采纳并细化** | 见 §2.5 |
| **D23** | 正式代码归属与 Git 位置 | **本实验目录保留为独立工作树；私有远端为 `luokow/memos-local-codex`** | **已实施** | 见 §2.6 |

---

## 2. 相对设计稿的补强与更优建议

### 2.1 D02 — 配置路径与定位（细化）

**确认**：`D:\codex\experiments\memos-local-codex\runtime\model-service.json` 为唯一真值文件。

**补强规则**：

1. **项目根解析**：两端统一“从已知锚点向上找根”（例如存在 `runtime/` + `llama/bin/llama-server.exe` + `models/`），再解析相对路径；禁止各端各猜 cwd。
2. **相对路径字段**（相对项目根）：`server_executable`、`model_path`；落盘用反斜杠或正斜杠均可，读取时 `Path.GetFullPath` / `path.resolve` 规范化。
3. **测试专用覆盖**：仅允许环境变量 `MEMOS_MODEL_SERVICE_CONFIG`（绝对路径）指向临时配置，**仅测试/诊断**；生产入口文档不宣传，避免第三真值。
4. **不提交仓库**：`runtime/model-service.json` 进 `.gitignore`（与 db/logs 同类）；仓库可放 `runtime/model-service.example.json` 作字段样例（无本机绝对路径依赖）。

### 2.2 D06 — 与 `MEMOS_REUSE_LLM_ONLY` 的冲突（重要）

实施前状态：`MemosStdioClient` 启动 MemOS 时固定 `MEMOS_REUSE_LLM_ONLY=1`，而 `mcp-server.mjs` 在该标志下**禁止**启动模型。

这与 C04/D06「真实记忆调用可按需重启」直接冲突：用户在 Qwen Local 停模型后，若子进程 MCP 仍存活且带该 env，按需启动会被永久挡住。

**结论（改实现语义，不改产品意图）**：

| 旧语义 | 新语义 |
|---|---|
| `MEMOS_REUSE_LLM_ONLY=1` → 绝不启动 | **废弃硬禁止**；统一为：先健康检查 → 跨进程锁 → 读 `auto_start_on_demand` |
| Qwen 子进程“只能复用” | Qwen 子进程与 Codex MCP **同一套 ensure 合同**，靠锁防双启 |

兼容期可选：

- 仍接受 env，但仅映射为「默认不抢先启动、优先复用」的日志提示；**真正开关是共享配置里的 `auto_start_on_demand`**。
- 或迁移完成后删除该 env 分支，避免双开关。

**UI 建议（强烈推荐，小改动高收益）**：

- 在设置「本地服务」卡片增加只读/可写项：**「MemOS 按需启动模型」** ↔ `auto_start_on_demand`。
- 默认 **true**（符合 C04）。
- 用户若希望“我停了模型就彻底安静，直到我再开 Qwen Local”，可关此项；关闭后 `recall`/`remember` 应返回明确错误（D21），不得云端回退。

### 2.3 D12 — 锁实现（建议改主路径）

设计稿：Windows 命名互斥体优先，Node 搞不定再用锁文件。

**更优建议：锁文件作为跨语言合同的主路径。**

理由：

1. C# 与 Node 对 **同一命名互斥体** 的约定容易踩坑（全局 vs Local、权限、.NET 与 Win32 名称空间）。
2. 锁文件可调试：看得见 PID、时间戳、配置摘要；崩溃后可用“PID 不存在则回收”。
3. 两端实现成本接近，行为易对表写测试。

**合同草案**（实施时可再落代码）：

- 路径：`runtime/locks/model-service.start.lock`
- 内容：JSON `{ "pid", "created_at_utc", "port", "model_path_hash", "owner": "qwen-local|memos-mcp" }`
- 获取：`O_EXCL` / `FileMode.CreateNew` 原子创建；失败则读锁并检查 PID 存活
- 持锁期间：二次 health → 必要时 spawn → 以端口健康为准释放锁
- **锁文件不是服务真值**；健康检查 + 进程路径才是

C# 可**额外**使用 `Global\MemOSLocal.ModelService.Start` 之类命名互斥体减少同进程竞态，但 Node 侧只认锁文件即可。

### 2.4 D13 — 身份核验（细化）

复用 / 停止前最低核验：

1. 监听地址仅为回环（`127.0.0.1` / `::1`）。
2. 监听进程映像路径与配置 `server_executable` **规范化后全路径相等**（Windows 大小写不敏感）。
3. 若 `/v1/models` 或等价接口可取别名：与 `model_alias` 一致才算完整复用；取不到则标记 `identity=partial`，UI/health 提示“已复用进程，未能校验模型别名”，**不**因此误杀。
4. 仅 HTTP 200 `/health` **不足以**认定可复用到“正确模型”。

### 2.5 D22 — 生命周期日志（细化）

- 文件：`runtime/logs/model-service-lifecycle.jsonl`（一行一事件）
- 字段：`ts`、`actor`、`action`（ensure/start/reuse/stop/reject）、`pid`、`port`、`result`、`reason`、路径**摘要**（basename 或 hash，避免无意义绝对路径噪声）
- **禁止**：提示词、会话正文、记忆内容、API key
- 供故障回放与 §13 验收，不替代用户可见错误文案

### 2.6 D23 — Git / 归属（实施结果）

实施结果：

1. `D:\codex\experiments\memos-local-codex` 已建立独立 Git 工作树与回滚基线；`.gitignore` 覆盖模型、运行数据、日志、依赖和发布产物。
2. 正式私有远端已建立为 `https://github.com/luokow/memos-local-codex`，本地分支跟踪 `origin/main`。
3. 实现提交为 `a240db9` 与 `554c54c`；同步核验时本地 `HEAD`、`origin/main` 与远端 `main` 均为 `554c54c5c03065d19a4053b300e8143e2dd11fb9`，ahead/behind 为 `0/0`。

### 2.7 设计稿未单列、但建议写入实施合同的补充项

| 编号 | 建议 | 理由 |
|---|---|---|
| **S01** | 共享配置字段与设计 §7.2 一致；首迁默认值取自当前 `settings.json`（Q6、18135、ctx 8192、gpu 99、parallel 1、reasoning on、jinja on、timeout 180） | 与实机一致，避免迁回 Q4/旧超时 |
| **S02** | MemOS 启动参数中 `reasoning` 必须跟共享配置，不得再写死 `off` | 实施前 `mcp-server.mjs` 写死 `--reasoning off`，与 Qwen Local 默认/用户设置不一致，会导致“同一端口服务参数以谁先启动为准”的隐蔽分裂 |
| **S03** | `parallel_slots` 进入共享配置后，Qwen Local 客户端并发生成上限继续绑定该值 | 08-04 产品语义：槽位 = 可同时回复会话数 |
| **S04** | 迁移回执写入 `runtime/logs/model-service-migrate-<timestamp>.json`（无敏感内容） | 满足设计 §12 第 8 步，便于验收 |
| **S05** | 实施四阶段不变；**阶段 0** 可加：本地 git init + 备份 `settings.json` / `mcp-server.mjs` / `config.yaml` | 失败可回滚 |
| **S06** | Qwen Local 设置里模型字段保存路径改为写共享配置；UI 文案仍在「模型启动 / 本地服务」卡片，用户无感 | D03+D04 |
| **S07** | Codex 侧 MCP 与 Qwen 子进程 MCP **共用** ensure 实现（抽到 `scripts/lib/model-service.mjs` 或等价模块） | 避免再复制一份硬编码 |

---

## 3. 批准结论与实施状态

### 3.1 可直接视为最终决定（无需再讨论）

D01, D03, D04, D05, D06（含 §2.2 语义调和）, D07, D08, D09, D10, D11, D14, D15, D16, D17, D18, D19, D20, D21
以及方案 A、C01–C05。

### 3.2 已批准并实施的补强项

| 编号 | 已采用的补强 |
|---|---|
| D02 | `runtime/model-service.json` + 项目根解析 + 测试 env + example 样例 |
| D12 | **锁文件主路径**（相对设计稿的唯一明显改动） |
| D13 | 规范化路径 + 别名 partial 策略 |
| D22 | jsonl 回执路径与字段白名单 |
| D23 | 独立 Git 工作树 + 私有远端 `luokow/memos-local-codex` |
| S01–S07 | 尤其 **S02 reasoning 跟配置**、**S07 抽取 ensure 模块** |

### 3.3 已批准的二选一结果

**Q1. 停模型后的“安静程度”默认值**

| 选项 | 含义 | 推荐 |
|---|---|---|
| **A** | `auto_start_on_demand=true`：真实 `recall`/`remember` 可唤醒（设计 C04） | **推荐** |
| B | 默认 `false`：只有 Qwen Local 显式启动才有模型；MemOS 失败即报错 | 更“省电/省显存”，但 Codex 记忆会更脆 |

批准结果为 **A**，并已在 UI 暴露开关（§2.2），让 B 成为用户可选而非全局默认。

**Q2. 锁主路径**

| 选项 | 含义 | 推荐 |
|---|---|---|
| **A** | 锁文件为跨语言合同 | **推荐**（本结论 D12） |
| B | 命名互斥体优先（设计稿原文） | 仅当你强烈希望 Windows 原生锁且愿意为 Node 写原生绑定 |

批准结果为 **A**。

---

## 4. 实施验收结果

以下验收锚点均已通过：

1. **配置**：磁盘上只有一份 `runtime/model-service.json`；`mcp-server.mjs` 源码无具体 GGUF 文件名；`settings.json` 无模型服务字段（或仅迁移期只读残留）。
2. **单进程**：两端并发 ensure → 仅一个 `llama-server`。
3. **退出**：三选项行为与 08-04 一致；停模型后无监听。
4. **按需**：停止后 `memos_recall` 能按共享配置拉起 Q6；`memos_health` / `list_recent` 不拉起。
5. **安静开关**：`auto_start_on_demand=false` 时 recall 结构化失败且不启动。
6. **隐私**：仅回环监听；无云端；无第二模型进程。
7. **参数一致**：MemOS 按需启动的 CLI 与 Qwen Local 使用同一组共享字段（含 reasoning / jinja / parallel）。

---

验收证据包括 Node 10/10、C# 58/58、离线 embedding、跨语言锁恢复、UI 基线、构建与发布、安装版退出复用，以及真实 `recall` / `remember` 的冷启动调用。生命周期日志不记录提示词或记忆正文，端点保持回环监听。

## 5. 批准与实施记录

批准文本：

> **批准共享模型配置结论草案（2026-08-05），按方案 A 四阶段实施；Q1=A，Q2=A。**

实施提交：`a240db9`、`554c54c`。正式私有远端：`https://github.com/luokow/memos-local-codex`。

---

## 6. 文档关系

| 文档 | 角色 |
|---|---|
| `2026-08-05-shared-model-service-configuration-design.md` | 问题、架构、合同、测试全景（源设计） |
| **本文** | D01–D23 选择结论 + 相对设计的补强建议 |
| `README.md` 与源码 | 当前运行合同、验证命令和已实施行为 |

当前状态：方案 A 已实施并同步；后续修改继续以共享配置、跨进程锁、回环网络和真实记忆调用按需启动为兼容边界。
