# Qwen Local Chat（Sol Luna / WinUI）需求与问题沉淀

> **日期**：2026-08-04  
> **项目路径**：`D:\codex\experiments\memos-local-codex\local-chat`  
> **技术栈**：C# / WinUI 3 / QwenLocalChat.Core + QwenLocalChat.WinUI  
> **用途**：完整记录本轮对话中的产品需求、用户反馈、缺陷根因、已落地修复与验收点，供后续用 GPT **沉淀 / 优化 Skill**、写回归清单或继续开发时交接。  
> **关联文档**：  
> - `docs/2026-08-03-runtime-settings-and-long-reply-stability-design.md`（设置生效时机、长回复稳定性）  
> - `docs/winui-ui-baseline.md`（UI 基线与 Markdown 合约）  
> - `docs/qwen-local-chat-native-crash-handoff-20260804.md`（ContentDialog / 原生崩溃边界）

---

## 1. 产品上下文（不要丢）

### 1.1 产品是什么

本地桌面聊天壳：**Qwen Local**，只连本机 `127.0.0.1` 上的 llama-server（Qwen3.5-9B），可选 MemOS 长期记忆与磁盘日志。  
WinUI 为主界面；多会话、设置侧栏、关窗停模型、会话落盘等均在 WinUI 路径交付。

### 1.2 用户在本轮之前已有的能力（背景）

| 能力 | 状态 |
|------|------|
| 多会话（顶栏下拉 + 新建/删除） | 已有 |
| 生成中可切换会话（原设计：后台一路继续） | 已有 |
| 输入草稿按会话保存 | 已有 |
| 思考模式开关闪退修复 | 已修 |
| 运行参数设置面板（生成 / 上下文 / 模型启动 / 本地服务） | 已有 |
| 会话列表落盘 `data/sessions.json` | 本轮前/初完成 |
| 会话排序可配置 `session_sort_mode` | 本轮中完成 |
| **禁止**设置/退出使用 WinUI `ContentDialog`（原生崩溃路径） | 硬约束 |

### 1.3 本轮对话的主线

用户从「恢复上次聊天」切入，连续验收 UI/交互，暴露：

1. 文案与排版（输入区快捷键、设置分区、退出对话框）  
2. 关窗是否提示停模型（「已复用」时不提示）  
3. **并发槽位与多会话并发生成的产品语义错位**  
4. **多会话下对话 1 双回复**  
5. **流式输出是否可实时 Markdown**

---

## 2. 需求清单（按主题）

### 2.1 会话持久化（前序结论，本轮继续依赖）

| ID | 需求 | 验收 |
|----|------|------|
| S1 | 关应用再开：会话列表、消息、草稿、当前选中会话恢复 | `data/sessions.json` 原子写；切换/新建/删除/发送完成/清空/关窗 Persist |
| S2 | 落盘时机：切换/删除**完成之后**再写，避免错误 active 或残留已删会话 | 无竞态脏写 |
| S3 | 安装包暂不做 | 用户明确「先做落盘」 |

### 2.2 会话列表排序

| ID | 需求 | 验收 |
|----|------|------|
| S4 | 原先「最近更新」导致顺序乱跳；先改为按创建时间 | 新建在上、聊天不重排 |
| S5 | 排序方式可配置 | 设置 → 会话 → 列表排序：`created_at` / `last_updated_at` |
| S6 | 保存后立即重排，无需重启模型 | `session_sort_mode` 写入 `settings.json` |

### 2.3 关窗与模型生命周期

| ID | 需求 | 验收 |
|----|------|------|
| C1 | 部分设置需重启模型才生效；关窗应能选择是否停模型服务 | 三选一：停止模型并退出 / 仅退出软件 / 取消 |
| C2 | **不能**用 ContentDialog | 主界面内联 `ClosePromptOverlay` |
| C3 | 状态为「已复用」时也要能提示（旧逻辑仅 `OwnsModel` 才弹） | 服务健康即弹；复用文案不同 |
| C4 | 选「停止模型」时：拥有则 `StopOwned`；复用则仅当端口监听进程可校验为**本机配置的 llama-server 可执行文件**时再停 | 不误杀无关进程 |
| C5 | 退出对话框观感：不要过大双行卡片按钮 | 普通高度主/次按钮 + 底部取消；说明放标题下 |

### 2.4 系统消息

| ID | 需求 | 验收 |
|----|------|------|
| M1 | 「已复用当前 Qwen…」不要重复出现 | 生命周期 SYSTEM 不落盘；恢复时过滤；追加时去重/清残留 |
| M2 | 会话级系统提示（新会话已开始等）仍保留 | 与生命周期类区分（`SystemNoticePolicy`） |

### 2.5 输入区快捷键提示

| ID | 需求 | 验收 |
|----|------|------|
| I1 | 「Enter 发送 / Shift+Enter 换行 / ↑↓ 历史 / 生成中停止」不要贴在一起 | 用 `StackPanel Spacing` 分项，**不要靠字符串里的空格**（雅黑易挤） |
| I2 | 不要 `·` 分隔符 | 用户明确要求去掉中间点 |

### 2.6 设置面板信息架构

| ID | 需求 | 验收 |
|----|------|------|
| P1 | 不同设置组要有清晰**边界线/卡片** | 生成 / 会话 / 上下文与记忆 / 模型启动 / 本地服务 各为圆角边框卡片 |
| P2 | 「何时生效」说明要人话，禁止难懂黑话 | 删掉「按区块颜色：浅色边框分区互不影响」 |
| P3 | 「即时 / 需重启」标签与箭头列举不要互相打架 | 顶部只解释两个词的含义；具体项看各分区副标题 |
| P4 | 并发槽位语义要写清 | 文案/Tooltip：**模型同时处理请求的路数 = 可同时回复的会话数**；需重启模型 |

### 2.7 多会话并发（产品关键语义）

| ID | 需求 | 验收 |
|----|------|------|
| X1 | 用户把「并发槽位」理解为「两个会话可以同时聊」 | 客户端并发生成上限 = `parallel_slots`（重启模型后） |
| X2 | 会话 A 生成中，可切到 B 再发；两路各自回复 | 每会话独立 generation job；历史快照在发送时固定 |
| X3 | 回复不得串会话：B 的输入不得写进 A | 见缺陷 D2 |
| X4 | 达并发上限时明确提示 | 提示当前几路、涉及哪些会话标题 |
| X5 | 「停止」只停**当前会话**的生成 | 不取消其它会话后台 job |

### 2.8 流式 Markdown

| ID | 需求 | 验收 |
|----|------|------|
| R1 | 不要等全部输出完才 Markdown | 流式过程中用 `MarkdownPresenter` |
| R2 | 控制刷新频率，避免每 token 全量重绘卡顿 | 约 120ms 节流 |
| R3 | 未闭合语法短暂难看可接受 | 结束后稳定为完整 Markdown |

---

## 3. 缺陷清单（用户现象 → 根因 → 修复）

### D1 — 系统提示「已复用…」重复

| 项 | 内容 |
|----|------|
| **现象** | 同一会话出现两条相同「已复用当前 Qwen3.5-9B 服务…」；用户当时未能稳定复现 |
| **根因** | 模型就绪 SYSTEM 写入 `sessions.json` → 冷启动 `ApplySessionToUi` 恢复 → `InitializeModelAsync` 再 `AppendSystem` 一次 |
| **修复** | `SystemNoticePolicy`：生命周期类不 `ShouldPersist`；恢复时跳过；追加时去重并清理残留 |
| **关键文件** | `Core/SystemNoticePolicy.cs`，`MainPage.xaml.cs`（AppendSystem / Capture / ApplySession） |

### D2 — 关窗无退出选项

| 项 | 内容 |
|----|------|
| **现象** | 状态「9B 已复用」时关窗直接退出，无三选一 |
| **根因** | `RequestCloseDecisionAsync` 仅 `OwnsModel == true` 才弹窗 |
| **修复** | 模型健康（拥有或复用）均弹；`StopServiceAsync` 支持端口上可校验的 llama-server |
| **关键文件** | `MainPage.xaml.cs`，`MainWindow.xaml.cs`，`QwenServiceManager.cs`，`MainPage.xaml`（ClosePromptOverlay） |

### D3 — 输入区快捷键贴死 / 有 `·`

| 项 | 内容 |
|----|------|
| **现象** | `Enter 发送 ·Shift+Enter…` 视觉上粘连；用户不要 `·` |
| **根因** | 空格分词在雅黑下不可靠；中间点无必要 |
| **修复** | 多 `TextBlock` + `Spacing`；去掉 `·` |
| **关键文件** | `MainPage.xaml` |

### D4 — 设置说明看不懂 / 与「即时·重启」矛盾

| 项 | 内容 |
|----|------|
| **现象** | 「按区块颜色…」用户不懂；标签 + `→` 列举重复且别扭 |
| **根因** | 顶部说明既想当图例又想当分区列表，信息过载 |
| **修复** | 顶部只解释「即时 / 需重启」两词；分区卡片自带副标题 |
| **关键文件** | `RuntimeSettingsPanel.xaml` |

### D5 — 并发槽位=2 仍不能两会话同时聊（语义缺口）

| 项 | 内容 |
|----|------|
| **现象** | 改并发为 2 并重启模型，仍感觉不能同时两路对话；或以为已并发但回复错乱 |
| **根因** | **分层错位**：`parallel_slots` 只传给 `llama-server --parallel`；UI 层全局 `_generation` **单例**，第二路发送被挡 |
| **修复** | `_generations: Dictionary<sessionId, SessionGeneration>`，上限 `Max(1..8)=ParallelSlots`；每 job 独立 CTS |
| **产品说明** | 服务端槽位与 UI 并发上限应对齐；设置文案写清 |
| **关键文件** | `MainPage.xaml.cs`，`WindowsModelProcessLauncher.cs`（启动参数），`RuntimeSettingsPanel.xaml` |

### D6 — 对话 1 输入后再在对话 2 输入，对话 1 回复了两次（严重）

| 项 | 内容 |
|----|------|
| **现象** | A 发消息 → 切 B 再发 → 回 A 看到**两条**助手回复（或同一轮重复） |
| **根因（叠加）** | ① 切换时 `Capture` 把半成品 `你`+半截 `Qwen` 写入 `sessions.json`；② 生成完成再整段 `Add` 一对 → 双份；③ 流式更新用「列表最后一个 streaming 气泡」`FindStreamingReplyEntry()`，多会话易绑错；④ 恢复会话时把「最后一条 Qwen」误当成当前流 |
| **修复** | 见下表 |
| **关键文件** | `MainPage.xaml.cs`（generation 绑定气泡、Capture 策略、Upsert、Rebuild）、测试 `generation-transcript-upsert-is-idempotent` |

**D6 修复要点（实现合约）：**

1. **每 job 绑定 UI**：`SessionGeneration.SubmittedEntry` / `ReplyEntry`，禁止全局「最后一个 streaming」。  
2. **生成中 Capture 不落盘本轮半成品**：切换会话时从 persist 中排除当前 live 的 `你`/streaming `Qwen`；最终只靠 `UpsertGenerationTranscript` 写一次。  
3. **`UpsertGenerationTranscript` 幂等**：按 `VisibleUserText` 删掉旧的你/Qwen 对，再写入最终对。  
4. **`RebuildVisibleTranscriptFromSession`**：重建已完成 turns；live gen 的气泡单独 `EnsureLiveGenerationBubbles`，绝不把历史完成行当成当前流。  
5. **完成后若正在看该会话**：用会话权威 transcript 重建 UI，清掉陈旧流式行。

### D7 — Markdown 只能全部输出完才渲染

| 项 | 内容 |
|----|------|
| **现象** | 流式阶段纯文本，结束后才 Markdown |
| **根因** | `TranscriptEntry`：`IsStreaming` 时 `StreamingVisibility=Visible`（纯文本）、`FormattedVisibility=Collapsed`（Markdown） |
| **修复** | 始终显示 `MarkdownPresenter`；纯文本流式层 Collapsed；流式更新约 120ms 节流 |
| **注意** | 未闭合 `**`/代码块中途可能短暂难看；与 `winui-ui-baseline`「一条消息一个可选文本所有者」仍兼容（仍是一个 Presenter） |
| **关键文件** | `TranscriptEntry.cs`，`MainPage.xaml.cs`（节流），`MarkdownPresenter.cs` |

### D8 — 退出对话框大按钮难看

| 项 | 内容 |
|----|------|
| **现象** | 双行大卡片按钮观感奇怪 |
| **修复** | 普通 `PrimaryButtonStyle` / `SecondaryButtonStyle` 全宽；取消透明底；说明一行小字在标题下 |
| **关键文件** | `MainPage.xaml`，`MainPage.xaml.cs`（文案） |

---

## 4. 架构与实现要点（给 Skill / 后续 agent）

### 4.1 硬约束（违反即事故）

1. **禁止** ContentDialog / 易崩溃的 Popup 设置路径（见 native-crash handoff）。  
2. 模型只监听 `127.0.0.1`；不向子进程传云 API 密钥。  
3. 停复用模型时必须校验进程路径 = 配置的 `llama-server`，禁止按端口裸杀。  
4. UI 改动走 `deploy-winui.ps1`（含 UI baseline + 核心测试 + AppX 注册）。  
5. Markdown / 字号 / 角色标签遵循 `docs/winui-ui-baseline.md`。

### 4.2 多会话生成模型（当前正确心智）

```
用户发送
  → 快照 Active 会话 ID、History、UserMessage（之后切换会话不得改写该 job）
  → 若该会话已有 job → 拒绝
  → 若 _generations.Count >= ParallelSlots → 拒绝并提示
  → 注册 job；流式只更新 job.ReplyEntry（且仅当 IsViewingSession(job.SessionId)）
  → 完成 → 只写 job.Session（History + Upsert Transcript）
  → 若正在看该会话 → Rebuild UI from session
```

**错误心智（已废弃）：**

- 「并发槽位只是服务端参数，UI 永远只能一路」  
- 「Capture 当前屏幕 transcript 到任意 session 都安全」  
- 「LastOrDefault(IsStreaming) 就是当前回复」

### 4.3 关键路径速查

| 区域 | 路径 |
|------|------|
| 主界面逻辑 | `src/QwenLocalChat.WinUI/MainPage.xaml.cs` |
| 主界面 XAML | `src/QwenLocalChat.WinUI/MainPage.xaml` |
| 设置面板 | `src/QwenLocalChat.WinUI/RuntimeSettingsPanel.xaml(.cs)` |
| 关窗协调 | `src/QwenLocalChat.Core/AppCloseCoordinator.cs`，`MainWindow.xaml.cs` |
| 模型进程 | `src/QwenLocalChat.Core/QwenServiceManager.cs`，`WindowsModelProcessLauncher.cs` |
| 会话模型/排序 | `src/QwenLocalChat.Core/ConversationSession.cs` |
| 会话落盘 | `ConversationSessionStore` + `AppPaths.SessionsFile` → `data/sessions.json` |
| 系统消息策略 | `src/QwenLocalChat.Core/SystemNoticePolicy.cs` |
| 流式 Markdown | `TranscriptEntry.cs`，`MarkdownPresenter.cs` |
| 部署 | `deploy-winui.ps1` |
| 核心测试 | `tests/QwenLocalChat.Tests/Program.cs` |

### 4.4 设置生效时机（复述，避免再写错文案）

| 类型 | 含义 | 例子 |
|------|------|------|
| **即时** | 保存后下一句对话用新值 | 温度、max tokens、流式、历史轮数、MemOS topK、会话排序 |
| **需重启** | 须停止模型再开，参数才进 llama 进程 | 上下文窗口、GPU 层、**并发槽位**、端口、思考模式、模型路径 |

关窗「停止模型」是用户让「需重启」类设置生效的重要入口。

---

## 5. 用户原话摘录（便于 Skill 学语气与优先级）

1. 「安装包暂时不需要，先做（会话落盘）」  
2. 「新建会话应该按顺序排列…关软件提示退出还是也退出服务…输入窗口发送换行历史贴在一起…设置模型提示不好理解」  
3. 「会话顺序能选择排序方式」  
4. 「系统提示重复了…发送换行历史还是贴在一起…关闭没提示…设置做好不同设置的边界线」  
5. 「为什么还要有 · 这个符号，去掉」  
6. 「保存说明『按区块颜色…』不懂；即时跟重启和箭头矛盾；退出三个选项不好看」  
7. 「并发改 2 并重启并不能同时两对话；对话 2 输入却在对话 1 回了」  
8. 「对话 1 输入再对话 2 输入，对话 1 回复了两次」  
9. 「md 渲染只能全部输出完才做吗，能不能实时渲染」  
10. 「把最近需求和问题详细记录到文档，让 gpt 沉淀优化 skill」← 本文档触发句

---

## 6. 建议回归清单（手工）

### 6.1 会话与落盘

- [ ] 新建/切换/删除后重启应用，列表与草稿正确  
- [ ] 排序：创建时间 vs 最近更新，保存立即生效  

### 6.2 关窗

- [ ] 「已启动」关窗出现三选一  
- [ ] 「已复用」关窗也出现三选一  
- [ ] 停止模型后 GPU/端口释放；仅退出则服务仍在、下次可复用  

### 6.3 文案与设置

- [ ] 输入区无 `·`，四项间距清晰  
- [ ] 设置分区卡片边界清晰；顶部说明无「区块颜色」黑话  
- [ ] 并发槽位 Tooltip/副标题可读  

### 6.4 并发生成（ParallelSlots≥2 且已重启模型）

- [ ] A 长回复未结束时，B 可发送  
- [ ] A/B 各自一条用户消息、一条助手回复，**无双回复**  
- [ ] 达上限时发送被拒且提示会话名  
- [ ] 在 A 点停止只停 A，B 继续  

### 6.5 流式 Markdown

- [ ] 生成过程中可见粗体/标题等格式变化  
- [ ] 结束后格式稳定，可复制整条  

### 6.6 系统消息

- [ ] 冷启动「已复用/已启动」只出现一次  

---

## 7. 自动化测试已覆盖（节选）

| 测试名 | 意图 |
|--------|------|
| `system-notice-policy-filters-model-lifecycle` | 生命周期 SYSTEM 不落盘 |
| `generation-transcript-upsert-is-idempotent` | 半成品 + 两次 commit 不会双 Qwen |
| `conversation-session-sort-modes` | 排序模式 |
| `window-close-waits-for-cleanup-before-approval` | 关窗协调器 |
| UI baseline script | 字号、Markdown 样式、设置宽度等 |

**缺口（尚未自动化、Skill 可提醒）：**

- 真实双会话并发生成 E2E  
- 关窗 overlay 视觉/点击路径 UI 测试  
- 流式 Markdown 中途未闭合语法的观感（仅人工）

---

## 8. 给「沉淀 Skill」的建议结构

若用本文档生成/优化 Grok Skill，建议 Skill 至少编码：

### 8.1 触发场景

- 改 Qwen Local / local-chat / Sol Luna WinUI  
- 多会话、并发、流式 UI、设置生效、关窗停模型  
- 用户反馈「串台」「双回复」「并发没用」

### 8.2 必查清单（agent 动手前）

1. 改动是否触及 ContentDialog？→ 否，用内联 overlay。  
2. 是否把「服务端 parallel」当成「UI 已支持多路」而未改 `_generations`？  
3. Capture 是否在 generation 中把半成品写入 sessions？  
4. 流式 UI 是否用全局 last streaming 而非 job 绑定气泡？  
5. 生命周期 SYSTEM 是否会再次 Persist？  
6. 是否跑 `deploy-winui.ps1` 而非只 `dotnet build`？

### 8.3 用户沟通原则（从本轮验证）

- 设置文案用「下一句生效 / 需停模型再开」，禁止内部隐喻（「区块颜色」）。  
- 分隔符能靠布局 Spacing 就不要字符 `·`。  
- 退出操作：主操作 / 次操作 / 安静取消，避免三块等高「大卡片」。  
- 报 bug 时先对齐：**服务端参数 vs 客户端队列** 两层语义。

### 8.4 可复制的决策表

| 用户说… | 真实含义 | 优先查 |
|---------|----------|--------|
| 并发=2 没用 | UI 可能仍单 job，或模型未真正重启 | `_generations`、OwnsModel/复用、启动参数 |
| 串会话 / 在 1 里回了 | job 未绑 session 或 Capture 写错 | SessionId 快照、Commit 目标 session |
| 回了两次 | 半成品 Persist + 最终再 Append | Upsert、Capture 排除 live |
| 关窗没提示 | 多半是「已复用」且只判断 OwnsModel | RequestCloseDecisionAsync |
| 设置说明看不懂 | 生效时机文案，不是控件坏了 | 顶部说明 vs 分区副标题 |

---

## 9. 未做 / 明确不做

| 项 | 状态 |
|----|------|
| 安装包 / Store 打包 | 用户暂不做 |
| 并发生成时 MemOS 双路 stdio 并发安全强化 | 未专门加固（MemOS 默认关；多路同时开 MemOS 有风险） |
| 流式 Markdown 增量解析（只 parse 尾部） | 未做；当前全量 parse + 节流 |
| 超过 2 路的 UX（会话列表上显示「生成中」角标） | 未做；仅有 Notice 文案 |

---

## 10. 变更文件索引（本轮主要落地）

便于 code review / skill 扫描：

```
src/QwenLocalChat.Core/
  SystemNoticePolicy.cs          (新)
  QwenServiceManager.cs          StopServiceAsync / 端口 PID
  AppCloseCoordinator.cs         AbortCleanup 等（前序）
  ConversationSession.cs         排序
  SettingsStore.cs               session_sort_mode 等

src/QwenLocalChat.WinUI/
  MainPage.xaml(.cs)             多路 generation、关窗 UI、快捷键、Capture/Upsert/Rebuild
  RuntimeSettingsPanel.xaml(.cs) 分区卡片、保存说明、并发提示
  TranscriptEntry.cs             流式始终 Markdown
  MainWindow.xaml.cs             关窗流程

tests/QwenLocalChat.Tests/Program.cs
  system-notice-policy-filters-model-lifecycle
  generation-transcript-upsert-is-idempotent
  conversation-session-sort-modes …
```

---

## 11. 一句话总结（给 Skill 摘要字段）

> Qwen Local WinUI：会话要落盘与可配置排序；关窗在「已启动/已复用」都能选是否停模型且禁用 ContentDialog；设置分区卡片化、生效说明用人话；**并发槽位必须同时约束 llama-server 与 UI 多会话 job**；生成中切会话禁止半成品双写 transcript，流式气泡必须绑定 job 而非全局 last；Markdown 流式实时渲染并节流。用户文案与观感反馈优先级高，忌内部黑话与装饰性 `·`。

---

*文档结束。可直接粘贴给 GPT 做 Skill 蒸馏；若蒸馏产物落盘，建议路径：`~/.grok/skills/` 或项目 `.grok/skills/`，并引用本文为 source of truth。*
