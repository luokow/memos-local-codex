# MiniMax H3 模型能力

对照官方产品和本机 Local AI 部署。提示词写法见 [`minimax-h3-prompt-guide.md`](minimax-h3-prompt-guide.md)。

## 1. 官方 H3 是什么

MiniMax 于 2026-07-31 发布 **MiniMax H3**（亦称 Hailuo 3.0）。官方定位是 **通用多模态生成模型**，但产品落点仍是 **一段视频 + 原生立体声音轨**，不是聊天、不是独立生图、不是独立 TTS。

- **输入**：文本、图、视频、音频可以混在一个上下文里理解。
- **输出**：4–15 秒、24 fps 的音视频；官方 API 提供 768P 和 2K。
- **预训练**做过文生图、文生音频、图生图等任务，用来提升泛化；**开源权重和官方 API 都没有单独的生图/配乐/聊天接口**。

MiniMax 其他产品线不要和 H3 混用：

| 产品 | 做什么 |
|---|---|
| MiniMax M2 / M2.5 / M3 | 语言 / Agent |
| MiniMax H3 | 音视频生成 |
| Speech / Music | 语音、音乐 |

## 2. 官方系统拆成三块

开源和云端不是同一整包：

| 组件 | 作用 | 是否开源 | 本机有没有 |
|---|---|---|---|
| **H3-Context-IR** | 读懂多模态输入，改写成标准提示词 | 否，仅云端 | 无。提示词要自己按手册写 |
| **H3-Base** | 真正采样出视频+音频 | 是。两套任务权重：FL2VA、Ref2VA | 有量化过的 FL2VA 包；参考模式走同一套量化权重，不是官方 Ref2VA 原文件 |
| **H3-Regenerate-2K** | 把符合规格的 768P 成片升到 2K | 否，仅云端 | 无。本机最高按档案像素上限出片 |

## 3. 官方任务与输入上限

来自 [官方视频生成文档](https://platform.minimax.io/docs/guides/video-generation) 和 [模型卡](https://huggingface.co/MiniMaxAI/MiniMax-H3)。

| 官方模式 | 输入 | 典型用途 |
|---|---|---|
| Text-to-Video（T2VA） | 只提示词 | 从零生成 |
| First/Last-Frame（I2VA / FL2VA / L2VA） | 提示词 + 0/1/2 张关键帧 | 钉住开头或结尾 |
| Reference Generation（Ref2VA） | 提示词 + 参考图/视频/音频 | 跟长相、动作、运镜、音色 |
| Video Regeneration | 已有 768P H3 成片 + 原 content | 升 2K（仅云端） |

官方参考入口上限：

| 项 | 官方上限 |
|---|---|
| 参考图 | ≤ 9 |
| 参考视频 | ≤ 3 段；每段 2–15 秒；合计 ≤ 15 秒 |
| 参考音频 | ≤ 3 段；每段 2–15 秒；合计 ≤ 15 秒 |
| 混合文件 | ≤ 12 个 |
| 提示词 | ≤ 7000 字符 |
| 关键帧图尺寸 | 边长 256–5760；宽高比 2:5–5:2 |
| 官方时长 | 4–15 秒整数 |
| 官方分辨率 | 768P / 2K |
| 帧率 | 24 fps |
| 成片音频 | 原生立体声 |

官方支持的参考文件类型（云端 API）：图 JPG/PNG/WEBP/HEIC；视频 H.264/H.265，片内音 AAC/MP3；音频 WAV/MP3。

## 4. 本机实际装了什么

部署目录：`D:\codex\local-ai\minimax-h3`。权重见该目录 `manifest.json`。

| 文件 | 来源 | 角色 |
|---|---|---|
| `minimax_h3_fl2va_pruned_w4a8_mixed.safetensors` | `Kijai/MiniMax-H3-experimental` | 主扩散，实验性量化，**不是官方等价物** |
| `minimax_h3_video_vae_int8_convrot.safetensors` | 同上 | 视频 VAE |
| `qwen3vl_32b_minimax_h3_nvfp4_awq.safetensors` | `Comfy-Org/MiniMax-H3` | 文本/视觉编码器（Qwen3-VL-32B） |
| `minimax_h3_audio_vae_fp32.safetensors` | 同上 | 音频 VAE |

ComfyUI 节点：

- `MiniMaxH3ImageToVideo`：文生、首帧、首尾帧（t2va / fl2va）
- `MiniMaxH3ReferenceToVideo`：参考图 / 参考视频 / 参考音频（ref2va）

节点硬上限与官方一致：参考图 9、参考视频 3、参考音频 3。参考视频按 24 fps 抽帧，建议 2–15 秒。

本机一次生成仍然是 **H.264 视频 + AAC 立体声音轨**，不是无声画面。已验证样例：864×480、24 fps、约 10.1 秒、AAC 32 kHz 立体声；20 步采样约 16 分钟，采样峰值约 5.6 GB 显存（见 `minimax-h3/evidence/H3-E2E-GENERATE.json`）。

## 5. Local AI 档案相对官方收了什么

当前档案：`runtime/video-model-profiles.json` 里的 `minimax-h3`。面向 RTX 4070 Laptop **8 GB**。

| 项 | 官方 / 节点 | 本机默认 | 设置里能调到 |
|---|---|---|---|
| 时长 | 官方 4–15 秒；节点训练约 5–15 秒（124–362 帧） | 10 秒 | 5–15 秒 |
| 分辨率 | 官方 768P / 2K | 864×480 | 边长 256–1024，步进 32，像素 ≤ 414720（约 864×480） |
| 帧率 | 24 | 24 | 档案锁定 24 |
| 参考图 | 9 | 4 | 0–9 |
| 参考视频 | 3 | 1 | 0–3 |
| 参考音频 | 3 | 1 | 0–3 |
| 步数 | — | 21 | 10–30 |
| 2K 重生 | 云端有 | 无 | 无 |
| Context-IR | 云端有 | 无 | 无 |
| 独立生图 / 独立音频 / 聊天 | 官方 H3 也不提供 | 无 | 无 |

客户端模式和节点对应：

| Local AI | 节点 | 说明 |
|---|---|---|
| 文生视频 | `MiniMaxH3ReferenceToVideo` 或 ImageToVideo 无关键帧 | 纯文本，成片仍带音轨 |
| 首帧图生视频 | `MiniMaxH3ImageToVideo` | 图是第 0 帧 |
| 首帧 + 尾帧 | `MiniMaxH3ImageToVideo` | 两张图钉开头和结尾 |
| 参考图 | `MiniMaxH3ReferenceToVideo` | 最多 9 张，标签 `<Picture N>` |
| 参考视频 | 同上 | 最多 3 段，标签 `<Video N>` |
| 参考音频 | 同上 | 最多 3 段，标签 `<Audio N>` |

参考图尺寸：`match` 按生成像素面积缩小（更省）；`max` 用 2048 短边，身份更稳，更慢更吃显存。

聊天页的 Qwen Local（llama.cpp）和视频页的 MiniMax H3 **互斥**：8 GB 上不能同时占显存。H3 里的 Qwen3-VL 只当视频编码器，没有聊天 API。

## 6. 「多模态」在本机意味着什么

能做：

- 文生视频，并且每次都带原生音轨
- 首帧 / 首尾帧驱动
- 多参考图、参考视频、参考音频（输入多模态）
- 提示词里用 `<Picture N>` / `<Video N>` / `<Audio N>` 指定每份素材的职责

不能做：

- 只出一张静图
- 只出一段独立音频/歌曲（音轨绑在视频上）
- 把 H3 当多模态聊天模型
- 官方 2K 重生、官方 Context-IR 自动扩写
- 用官方 Ref2VA 原精度权重（本机是 FL2VA 量化包兼跑参考节点）

量化砍掉的是精度、分辨率和云端配套，不是把模型改成「只会出无声视频」。

## 7. 本机建议工作点

8 GB 上已经跑通、比较稳的起点：

- 864×480、10 秒、24 fps、约 20 步
- 参考图默认 4 张；需要时在设置里加到 9
- 参考视频/音频默认各 1；短、清晰、2–15 秒
- 一次只开聊天或视频，不要两个模型一起驻留

更长、更大、更多参考会明显增加时间和显存风险；OOM 时先降分辨率或参考数量，不要先加步数。

## 来源

- [MiniMax H3 公告](https://www.minimax.io/blog/minimax-h3)
- [MiniMaxAI/MiniMax-H3 模型卡](https://huggingface.co/MiniMaxAI/MiniMax-H3)
- [官方视频生成指南](https://platform.minimax.io/docs/guides/video-generation)
- 本机清单：`D:\codex\local-ai\minimax-h3\manifest.json`
- 本机档案：`D:\codex\experiments\memos-local-codex\runtime\video-model-profiles.json`
- 本机节点：`ComfyUI/comfy_extras/nodes_minimax_h3.py`
- 本机出片证据：`D:\codex\local-ai\minimax-h3\evidence\H3-E2E-GENERATE.json`
