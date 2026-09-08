# 视频生成：OOM 假运行修复与进度检查点（2026-08-12）

## 背景

- **TODO-20260812-001**：CUDA OOM 后客户端仍显示“生成中”。
- **TODO-20260812-002**：长任务中间进度丢失，无法从失败前的采样步继续。

## 根因（001）

1. ComfyUI `execution.py` 在 OOM 分支调用 `unload_all_models()`；二次 CUDA 异常（如 `mem_get_info`）会逃出 `except`，导致本应返回的 `FAILURE` 丢失。
2. `main.py` `prompt_worker` 在 `e.execute(...)` 抛错时不会调用 `task_done`，任务永久留在 `currently_running`；`/queue` 仍显示 `queue_running`，`/history/<id>` 为空。
3. 客户端 `GetJobAsync` 只读 `error` 字段，而 Jobs API 实际返回 `execution_error`；也没有 history/queue 交叉核验与假运行判定。

## 修复（001）

| 层 | 变更 |
|----|------|
| ComfyUI | OOM 卸载包在 `try/except`；worker 用 `try/finally` 保证 `task_done` 写入历史 |
| Core | 解析 `execution_error`；轮询时补充 `/history`、`/queue`；多信号 liveness |
| UI | 失败展示完整错误、“查看详情”、“重试” |

假运行判定**禁止**仅用超时：需后端错误、服务不可达、queue+history 同时证明任务消失，或“长时间指纹不变 + 仍在队列 + 历史为空”等多项无前进证据。
**在队列中（queue_running）默认 90 分钟** 才判假运行——H3 low-VRAM 下 5 秒片采样常需 15–40+ 分钟且 jobs API 无 progress，旧版 12 分钟会误杀真实 KSampler（并 Cancel 打断后端）。

## 检查点可行性（002）

### MiniMax H3（当前本地默认）

H3 的 AV latent 是 **`NestedTensor`（视频 + 音频）**，不是普通 `torch.Tensor`。

- 标准 `SaveLatent` 会执行 `samples["samples"].contiguous()` → **`AttributeError: 'NestedTensor' object has no attribute 'contiguous'`**。
- 因此客户端对含 `MiniMaxH3ReferenceToVideo` / `MiniMaxH3ImageToVideo` 的图 **禁用** 分段 `SaveLatent` / 步级续跑，回退为 **整图 `KSampler`**。
- 失败后仍可保存提示词与种子做 **整任务重跑**，不能从中间采样步续跑。

### 其他普通 LATENT 模型（预留）

- **可用**：`KSamplerAdvanced` + `SaveLatent` / `LoadLatent` 分段。
- **策略**：默认 5 步一段；段间写盘；失败后从最新 `_stepN` 续跑。
- **不是**单节点内部半步恢复。

元数据：`local-chat/data/video-generation-checkpoint.json`。仅当步级检查点可用时 UI 显示 **继续上次**。

## 验证

- 核心测试：`minimax-h3-parses-execution-error-as-failed`、`video-job-liveness-*`、`video-checkpoint-store-roundtrip`、`video-segmented-workflow-builds-resume-chain` 等均 PASS。
- ComfyUI 修改需在下次启动 8188 后生效；若现场仍有僵尸 `queue_running`，需手动取消/重启服务一次以清空旧状态。

## 范围与限制

- 未提交 Git、未推送。
- 未在本轮真实低显存环境重跑 864×480 全片（耗时长、占 GPU）。
- latent 检查点会占用磁盘；失败任务的 latent 需用户或后续清理策略处理。
