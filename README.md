# Codex 本地 MemOS

这套集成把记忆数据库、语义向量模型和 Qwen 本地大模型全部放在 D 盘。运行时不需要 MemOS、OpenAI 或其他云端 API Key。

## 各组件做什么

- **MemOS 2.0.12**：保存对话轨迹，组织短期/长期/抽象记忆，并执行记忆演化。
- **Qwen3.5-9B Uncensored HauhauCS Aggressive Q4_K_M**：本地理解对话，提取值得长期保存的事实、偏好和流程，完成归纳与评分。它由 `llama.cpp` 在 RTX 4070 Laptop GPU 上运行，默认关闭思考模式以避免结构化输出被思考内容占满。
- **all-MiniLM-L6-v2**：把文本转换成 384 维向量，用来快速查找“意思相近但字面不同”的记忆。它不负责生成回答。
- **MCP 适配器**：向 Codex 提供 `memos_recall`、`memos_remember`、`memos_health` 和 `memos_list_recent` 四个工具。

## 数据位置

- MemOS 数据库：`D:\codex\experiments\memos-local-codex\runtime\data\memos.db`
- 当前本地模型：`D:\codex\experiments\memos-local-codex\models\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf`
- 回滚模型（默认不启动）：`D:\codex\experiments\memos-local-codex\models\Qwen3-4B-Q4_K_M.gguf`
- 向量模型缓存：`D:\codex\experiments\memos-local-codex\runtime\transformers-cache`
- 本地模型诊断日志：`D:\codex\experiments\memos-local-codex\runtime\logs\llama-server.log`

MemOS 文件日志和专用 LLM 日志均已禁用；遥测与 MemOS Hub 已禁用；本地模型服务只监听 `127.0.0.1:18135`。验收阶段产生的旧日志只含合成测试内容，不作为运行时依赖。

## 使用

不修改全局 Codex 配置，单次启动带本地记忆的 Codex：

```powershell
& 'D:\codex\experiments\memos-local-codex\scripts\start-codex-with-memos.ps1'
```

传递 Codex 参数也可以，例如：

```powershell
& 'D:\codex\experiments\memos-local-codex\scripts\start-codex-with-memos.ps1' --cd 'D:\codex'
```

需要持久配置时，可参考 `codex-mcp-fragment.toml`；当前活跃任务没有改写用户级 `C:\Users\kow\.codex\config.toml`。

## 验证和维护

```powershell
cd 'D:\codex\experiments\memos-local-codex'
npm run audit:prod
npm run smoke
```

`npm run smoke` 会启动本地模型，写入一条验收记忆，再用不同措辞进行语义召回。首次加载模型需要一些时间和显存。
