# Codex 本地 MemOS

这套集成把记忆数据库、语义向量模型和 Qwen 本地大模型全部放在本机。运行时不需要 MemOS、OpenAI 或其他云端 API Key。源码以 MIT 开源；GGUF 权重、`llama-server` 二进制、MemOS 运行数据和 `package-static` 不入库，需在本机自行放置。

## 各组件做什么

- **MemOS 2.0.12**：保存对话轨迹，组织短期/长期/抽象记忆，并执行记忆演化。
- **本地生成模型**：由 `runtime/model-service.json` 指定 GGUF、别名、上下文、GPU 层数、并发、思考模式和 Jinja 模板。MemOS 使用它完成记忆提取、归纳与评分。
- **all-MiniLM-L6-v2**：把文本转换成 384 维向量，用来快速查找“意思相近但字面不同”的记忆。它不负责生成回答。
- **MCP 适配器**：向 Codex 提供 `memos_recall`、`memos_remember`、`memos_health` 和 `memos_list_recent` 四个工具。
- **汉化工具包**：`hanhua/` 是 Local AI 汉化页调用的 Python 脚本。不含游戏资源和第三方汉化软件。

## 数据位置

- MemOS 数据库：`D:\codex\experiments\memos-local-codex\runtime\data\memos.db`
- 共享模型配置：`D:\codex\experiments\memos-local-codex\runtime\model-service.json`
- 配置示例：`D:\codex\experiments\memos-local-codex\runtime\model-service.example.json`
- 向量模型缓存：`D:\codex\experiments\memos-local-codex\runtime\transformers-cache`
- 本地模型诊断日志：`D:\codex\experiments\memos-local-codex\runtime\logs\llama-server.log`

MemOS 文件日志和专用 LLM 日志均已禁用；遥测与 MemOS Hub 已禁用。共享配置只允许回环地址，当前实例使用 `127.0.0.1:18135`。验收阶段产生的旧日志只含合成测试内容，不作为运行时依赖。

## 共享模型服务

Qwen Local 和 MemOS 读取同一份 `runtime/model-service.json`。两端启动前会重新读取配置，并通过 `runtime/locks/model-service-start.lock` 协调，避免并发启动多个模型进程。锁文件包含 PID、客户端身份和配置摘要；只有死 PID 且服务不健康时才会恢复残留锁。

死锁回收使用同目录的 `.recovery` 原子守卫。守卫存在时，两端都会等待或复用已经健康的服务；死 PID 留下的守卫会按原始内容哈希生成 `.retired.<sha256>` 审计文件。Node 使用同卷硬链接和文件 ID 比较，C# 使用禁止覆盖的原子移动，随后再继续获取启动锁。`npm run accept:lock-cross-language` 会让 C# 与 Node 同时回收同一组遗留主锁和守卫，并验证只有一个启动所有者。

`auto_start_on_demand` 控制 MemOS 的真实 `recall` / `remember` 调用能否在冷状态启动模型。`memos_health` 和 `memos_list_recent` 保持只读，不会启动模型。Qwen Local 设置页的“记忆调用按需启动模型”开关会更新该字段。

模型启动、复用、停止和失败事件记录在 `runtime/logs/model-service-lifecycle.jsonl`。日志保存路径哈希和配置摘要，不记录提示词或记忆正文。

## 使用

不修改全局 Codex 配置，单次启动带本地记忆的 Codex：

```powershell
& 'D:\codex\experiments\memos-local-codex\scripts\start-codex-with-memos.ps1'
```

传递 Codex 参数也可以，例如：

```powershell
& 'D:\codex\experiments\memos-local-codex\scripts\start-codex-with-memos.ps1' --cd 'D:\codex'
```

需要持久配置时，可参考 `codex-mcp-fragment.toml`；当前活跃任务没有改写用户级 `%USERPROFILE%\.codex\config.toml`。

## 验证和维护

```powershell
cd 'D:\codex\experiments\memos-local-codex'
npm run audit:prod
npm test
npm run accept:lock-cross-language
npm run accept:lifecycle:read-only
```

`accept:lifecycle:read-only` 要求模型先处于停止状态，并验证健康检查和最近记忆列表不会启动服务。`accept:lifecycle:recall` 会执行真实召回，验证共享配置、进程身份和按需启动。`accept:lifecycle:remember` 在系统临时目录建立隔离的 MemOS 数据库，执行真实 remember 和同义 recall，关闭测试 MCP 后删除整个临时目录。`npm run smoke` 会写入当前运行库，只在需要持久化读写验收时使用。
