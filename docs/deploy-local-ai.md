# 在本机部署 Local AI 客户端

这份说明给第一次克隆仓库的人。做完后应能打开 WinUI 客户端、加载本机 GGUF、在回环地址上聊天。  
GGUF 权重、`llama-server` 和运行数据不入库，必须按下面步骤放到仓库旁边的固定目录。

官方上游：

- 推理： [ggml-org/llama.cpp](https://github.com/ggml-org/llama.cpp)（Windows x64 CUDA 12 发行包）
- 聊天权重： [HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive](https://huggingface.co/HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive)（Apache-2.0）
- 汉化填字权重： [SakuraLLM/Sakura-GalTransl-7B-v3.7](https://huggingface.co/SakuraLLM/Sakura-GalTransl-7B-v3.7)（CC-BY-NC-SA-4.0，禁止商用）

不要用 Ollama（默认 `11434`）。客户端只连 `127.0.0.1:18135`。

## 1. 机器要求

| 项 | 聊天可用 | 说明 |
|---|---|---|
| 系统 | Windows 11，SDK `10.0.26100` | WinUI 目标框架 `net9.0-windows10.0.26100.0` |
| GPU | NVIDIA，CUDA 12 | 显存按你选的 GGUF 和上下文来；不够就在设置里降低 `context_size` / `parallel_slots`，或换更小量化 |
| 磁盘 | 聊天 GGUF 约 5.3 GiB；加填字再约 5.8 GiB | 不含 ComfyUI 视频权重 |
| 开发 | [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)、Windows 开发人员模式 | 用于 `deploy-winui.ps1` 注册 loose AppX |

聊天、填字、漫画 OCR/嵌字、视频各自会占 GPU。设置里默认开启互斥，也可关掉。

## 2. 仓库里有什么、没有什么

克隆后应看到 `local-chat/`、`scripts/`、`runtime/*.example.json`、`hanhua/`。  
**不会**出现：

- `models/*.gguf`
- `llama/bin/llama-server.exe`
- `runtime/model-service.json`、`runtime/model-profiles.json`（本机生成）
- `local-chat/data/` 聊天记录

客户端靠相对路径找项目根：必须存在名为 `local-chat` 的目录，并且**上一级有 `llama` 目录**（哪怕先建空目录）。

建议布局：

```
memos-local-codex/
  local-chat/          ← 源码与 deploy-winui.ps1
  llama/bin/           ← 解压 llama.cpp CUDA 包，这里要有 llama-server.exe
  models/              ← GGUF
  runtime/             ← 配置（由示例复制）
  scripts/
  hanhua/              ← 可选，汉化页 Python 脚本
```

## 3. 克隆并生成配置

```powershell
git clone https://github.com/luokow/memos-local-codex.git
cd memos-local-codex
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\bootstrap-local-ai.ps1
```

脚本会：

1. 创建 `llama\bin`、`models`、`runtime\locks`、`runtime\logs`
2. 若尚不存在，把示例复制为：
   - `runtime\model-service.json`
   - `runtime\model-profiles.json`
   - `runtime\video-model-profiles.json`
3. 检查 `llama-server.exe` 和聊天 GGUF 是否已就位

已有本机配置时不会覆盖。

系统 HTTP 代理会劫持 `127.0.0.1`。本机进程应设：

```powershell
$env:NO_PROXY = '127.0.0.1,localhost'
```

## 4. 下载 llama.cpp

1. 打开 [llama.cpp Releases](https://github.com/ggml-org/llama.cpp/releases)
2. 下载 **Windows x64 (CUDA 12)** 的 zip（资源名类似 `llama-bXXXX-bin-win-cuda-12.4-x64.zip`）
3. 解压到 `llama\bin`，保证存在：

```
llama\bin\llama-server.exe
llama\bin\ggml-cuda.dll
```

本仓库验证过的构建是 **b10208**。Qwen3.5 架构需要能识别 `qwen35` 的 llama.cpp；过旧的构建会报 `unknown model architecture`。CUDA 12 与发行包自带的 `cublas`/`cudart` DLL 放在同一目录即可，不必另装完整 CUDA Toolkit。

不要把 CPU 包或 Vulkan 包当成 GPU 聊天后端。

## 5. 下载模型

### 聊天（必做）

| | |
|---|---|
| 用途 | Local AI 聊天页 |
| Hugging Face | [HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive](https://huggingface.co/HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive) |
| 文件 | `Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf` |
| 体积 | 5 627 044 224 字节（约 5.24 GiB） |
| SHA-256 | `2CA636D9E81D3D23CA9B60C234FE185D30EC082EEBA69CE770FDB0C76559A4F5` |
| 许可 | Apache-2.0 |
| 放到 | `models\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf` |
| 档案 id | `qwen-local` |
| API 别名 | `qwen3.5:9b-uncensored-local` |
| 示例档案 | 见 `runtime/model-profiles.example.json`（上下文、GPU 层、并发可在设置里改） |

下载示例：

```powershell
curl.exe -L --fail --retry 5 -o models\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf `
  "https://huggingface.co/HauhauCS/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive/resolve/main/Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf"
Get-FileHash models\Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf -Algorithm SHA256
```

也可用 `huggingface-cli download`。校验必须与上表 SHA-256 一致。同一 Hugging Face 仓库还有 Q6_K、Q8_0 等量化，换文件后改档案里的 `model_path`。聊天默认不加载 `mmproj`。上下文和并发在设置里改，保存后重启模型。

### 汉化填字（可选）

| | |
|---|---|
| 用途 | 游戏/图字填字，**不要**拿来当聊天模型 |
| Hugging Face | [SakuraLLM/Sakura-GalTransl-7B-v3.7](https://huggingface.co/SakuraLLM/Sakura-GalTransl-7B-v3.7) |
| 文件 | `Sakura-Galtransl-7B-v3.7.gguf`（官方说明：无后缀 = Q6_K） |
| 体积 | 6 254 196 608 字节 |
| SHA-256 | `E1AE01B1735CFDCD00A3C7E2E5E06FF3A2756CC0D7592F4B4F5945638A8629F2` |
| 许可 | CC-BY-NC-SA-4.0，禁止商用 |
| 放到 | `models\sakura-galtransl-7b-v3.7\Sakura-Galtransl-7B-v3.7.gguf` |
| 档案 id | `sakura-galtransl-7b-v3-7` |

同一仓库还有更小的 `Sakura-Galtransl-7B-v3.7-IQ4_XS.gguf`，换文件后改 `model_path`。

填字走 `http://127.0.0.1:18135/v1/chat/completions`。模型别名不可包含 `qwen-mt`（否则会走 MT 协议、剥掉 `system`）。不要起 `qwen_mt_proxy.py` / 端口 `18765`。

### 视频（可选）

聊天不依赖 ComfyUI。要开视频页才需要：

- 本机 [ComfyUI](https://github.com/comfyanonymous/ComfyUI) 听 `127.0.0.1:8188`
- MiniMax H3 工作流节点与权重（见 `runtime\workflows\minimax-h3-api.json` 里的文件名）
- 把 `runtime\video-model-profiles.json` 里的 `comfyUiRoot` / `outputDirectory` 改成你的绝对路径（输出目录必须在 ComfyUI 根之内）

默认互斥开启时，开始视频会停掉当前聊天模型。

## 6. 构建并打开客户端

先打开 **设置 → 隐私和安全性 → 开发人员选项 → 开发人员模式**。

```powershell
cd local-chat
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy-winui.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\create-desktop-shortcut.ps1
```

`deploy-winui.ps1` 会跑核心测试、注册 loose AppX，并核对应 DLL 哈希。不要只用 `dotnet build` 然后去点桌面快捷方式。脚本可能关掉**这一份** AppX 窗口；正在跑的汉化不要为了热更中断。

桌面会出现 `Local AI.lnk`。打开后：

1. 不要点右上角「启动」当下一步——那只是手动拉显卡
2. 切到「聊天」，输入一句话发送；第一次会按需启动 `llama-server`
3. 底部状态变为已启动或已复用，且回复开始流出，即部署成功

服务只绑 `127.0.0.1:18135`。健康检查：`http://127.0.0.1:18135/health`（记得绕过系统代理）。

## 7. 配置文件（本机生成，勿提交）

| 文件 | 作用 |
|---|---|
| `runtime\model-profiles.json` | 文本档案列表；聊天默认 `qwen-local` |
| `runtime\model-service.json` | 当前正在用的那一份启动参数（与所选档案同步） |
| `runtime\video-model-profiles.json` | 视频档案；聊天也要有一份合法 JSON，否则窗口起不来 |
| `local-chat\data\settings.json` | 生成参数、汉化路径；首次运行自动写 |

改 GGUF 路径、端口、上下文、并发后，在设置里保存并**重启模型**。也可以在「设置 → 文本模型」里选一个只含单个 `.gguf` 的目录，客户端会生成新档案。

## 8. 可选功能

**汉化页**（`hanhua/`）：在设置里填写汉化工具包目录和 Python 3.12。入口是「开始汉化」，不是右上角「启动」。Unity 开始汉化只装插件/预填已抽出的句子，不会改正在开着的游戏窗口。填字用 GalTransl 档案。

**MemOS / Codex MCP**：`npm install` 后见仓库根 README 的记忆工具一节。不是打开聊天客户端的前置条件。

## 9. 常见失败

| 现象 | 处理 |
|---|---|
| 找不到 `local-chat` 项目根 | 确认 `llama` 目录与 `local-chat` 同级 |
| `unknown model architecture: qwen35` | 换更新的 llama.cpp CUDA 构建 |
| 健康检查 502 / 连不上 18135 | 设 `NO_PROXY=127.0.0.1,localhost`，不要走系统代理 |
| CUDA OOM | 停其它占 GPU 的进程；或在设置里降低上下文 / 并发，或换更小量化 |
| 窗口能开但一发送就失败 | `model-service.json` 的 `model_path` 是否指向真实 GGUF；`llama-server.exe` 是否在 `llama\bin` |
| 填字把 system 吃掉 | 别名含 `qwen-mt`，或误开了 18765 适配 |

## 10. 验收（聊天最小集）

- [ ] `llama\bin\llama-server.exe` 存在
- [ ] 聊天 GGUF SHA-256 与第 5 节一致
- [ ] `runtime\model-service.json` 与 `runtime\model-profiles.json` 已从示例生成
- [ ] `deploy-winui.ps1` 成功，桌面有 `Local AI.lnk`
- [ ] 发送一句中文，流式回复出现
- [ ] `Get-NetTCPConnection -LocalPort 18135` 仅 `127.0.0.1`
