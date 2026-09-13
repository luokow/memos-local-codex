# Local AI / memos-local-codex

Windows 本机客户端：用 [llama.cpp](https://github.com/ggml-org/llama.cpp) 在回环地址跑 GGUF，WinUI 里聊天；可选游戏汉化填字、MemOS 记忆和 MiniMax H3 视频。不需要 OpenAI / MemOS 云 Key。源码 MIT；**权重和 `llama-server` 二进制不入库**。

别人克隆之后要能用，请按这份走完：

**[docs/deploy-local-ai.md](docs/deploy-local-ai.md)** — 硬件、目录、llama.cpp、要下哪些模型（含 SHA-256）、生成配置、编译 WinUI、第一次聊天。

```powershell
git clone https://github.com/luokow/memos-local-codex.git
cd memos-local-codex
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\bootstrap-local-ai.ps1
```

然后下载 CUDA 版 `llama-server` 到 `llama\bin`，下载聊天 GGUF 到 `models\`（表在部署文档），再：

```powershell
cd local-chat
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy-winui.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\create-desktop-shortcut.ps1
```

## 组件

| 组件 | 作用 | 打开客户端是否必须 |
|---|---|---|
| WinUI `local-chat` | Local AI 窗口（聊天 / 视频 / 汉化） | 是 |
| `llama-server` | OpenAI 兼容 HTTP，只绑 `127.0.0.1:18135` | 是 |
| Qwen3.5-9B Q4_K_M | 聊天 | 是 |
| Sakura-Galtransl-7B v3.7 | 汉化填字 | 否 |
| `hanhua/` | 汉化页调用的 Python 脚本 | 否 |
| MemOS + MCP | Codex 记忆工具 | 否 |
| ComfyUI + MiniMax H3 | 视频页 | 否 |

不要用 Ollama `11434`，不要用 MT 适配 `18765`。聊天、填字、视频默认互斥，可在设置里关。

## 模型（摘要）

完整体积、SHA-256、许可和放置路径见 [部署文档第 5 节](docs/deploy-local-ai.md#5-下载模型)。

| 用途 | 上游 | 文件 |
|---|---|---|
| 聊天 | [HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive](https://huggingface.co/HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive) | `Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf` |
| 填字 | [SakuraLLM/Sakura-GalTransl-7B-v3.7](https://huggingface.co/SakuraLLM/Sakura-GalTransl-7B-v3.7)（非商用） | `Sakura-Galtransl-7B-v3.7.gguf` |

示例档案在 `runtime/model-profiles.example.json`。上下文、GPU 层、并发按本机显存在设置里改，保存后重启模型。

## 配置示例

| 入库示例 | 本机复制为 |
|---|---|
| `runtime/model-service.example.json` | `runtime/model-service.json` |
| `runtime/model-profiles.example.json` | `runtime/model-profiles.json` |
| `runtime/video-model-profiles.example.json` | `runtime/video-model-profiles.json` |

`bootstrap-local-ai.ps1` 会在目标不存在时复制。本机 json、锁、日志、GGUF 已在 `.gitignore`。

共享服务只允许回环。Qwen Local 与 MemOS 抢同一份 `model-service.json` 和启动锁。生命周期写在 `runtime/logs/model-service-lifecycle.jsonl`，不含提示词。

## 客户端习惯

- 应用打开时不加载模型；第一次发送或点「立即启动」才拉起
- 汉化入口是「**开始汉化**」。右上角「启动」只动显卡
- 界面说明：[`local-chat/README.md`](local-chat/README.md)

## MemOS / Codex（可选）

记忆库、向量模型和 MCP 工具（`memos_recall` / `memos_remember` / `memos_health` / `memos_list_recent`）给 Codex 用，不是聊天客户端的前置。

```powershell
npm install
npm test
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\start-codex-with-memos.ps1
```

`accept:lifecycle:read-only` 要求模型先停着，并确认健康检查不会启动服务。持久化 MCP 片段见 `codex-mcp-fragment.toml`；不要改用户级 `%USERPROFILE%\.codex\config.toml`，除非你明确要长期接上。

安全边界见 [`SECURITY_REVIEW.md`](SECURITY_REVIEW.md)。
