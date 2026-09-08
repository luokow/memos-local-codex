# Local AI 本地客户端

双击桌面的 `Local AI.lnk` 即可打开 WinUI 3 客户端。顶部可切换“聊天”和“视频”：聊天模型来自 `runtime\model-profiles.json`，视频模型来自 `runtime\video-model-profiles.json`。Qwen Local 和 MiniMax H3 是当前默认档案，不是写死的产品名称；所有请求只访问本机回环地址。

## 界面

- 固定使用黑白深色主题，背景包含静态柔光、星点和星图连线。
- 应用图标为通用的黑白对话气泡，不包含模型或厂商品牌标记。
- 界面使用 WinUI 3/XAML，不加载联网图片、字体或第三方 UI 组件。

## 使用方式

- 首次启动时，“使用 MemOS 长期记忆”和“保存聊天日志”都关闭。
- 修改开关后会立即保存，下次启动继续使用你的选择。
- `Enter` 发送，`Shift+Enter` 换行。
- 视频宽高、时长、步数和种子可在“设置 > 视频模型”调整；可选范围和帧对齐规则来自当前视频档案。
- 8GB 显卡上聊天和视频模型互斥：开始视频前会安全停止当前聊天模型；返回聊天发送时会停止本窗口启动的视频服务，或释放复用服务占用的模型。
- 切换“聊天/视频”页面本身不会取消正在执行的任务；视频可在任务区显式取消。
- 视频页会从当前视频档案的输出目录恢复最近成片；预览按媒体比例适配，播放控件仅在悬浮或键盘聚焦时显示。
- 应用打开时不会启动任何模型；首次发送或开始生成才按需启动。设置中的“立即启动”和“释放模型”只用于主动检查或回收本机资源。
- 在“设置 > 文本模型”选择本地目录时，目录必须且只能包含一个 GGUF；客户端会复用当前服务模板、生成唯一档案 ID 并保存后重载。视频目录只会在能验证 ComfyUI 根与兼容工作流模板时导入，不能安全验证时不会保存猜测的节点映射。
- “清空会话”只清除窗口上下文，不删除 MemOS 和记忆日志。
- 日志开启后，可见问答保存到 `logs\YYYY-MM-DD`；隐藏召回内容不会写入日志。

## 模型生命周期

- 共享配置指定的回环端口健康且进程身份匹配时，程序直接复用服务。
- 服务未运行时，程序按共享配置启动模型，启动等待时间由 `startup_timeout_seconds` 控制。
- 当前文本适配器为 `llama.cpp-openai`：每个档案可以独立配置显示名称、GGUF 路径、API model、端口和启动参数。
- 当前视频适配器为 `comfyui-workflow`：每个档案可以独立配置显示名称、ComfyUI 地址与目录、工作流、必需节点、输入映射和能力边界。
- 视频输出目录由所选视频档案指定，完成后可在客户端预览或打开。
- 如果模型由本窗口启动，关闭窗口时可选择停止模型或保持后台运行。
- MemOS 的真实记忆调用可按设置启动共享模型；健康检查和列表查询保持只读。
- 程序不创建 Windows 服务、计划任务或开机启动项。

## 本地数据

- 设置：`data\settings.json`
- 文本模型档案：`runtime\model-profiles.json`
- 视频模型档案：`runtime\video-model-profiles.json`
- 视频工作流：`runtime\workflows\`
- 聊天日志：`logs\`
- 自测结果：`diagnostics\self-test.json`
- 模型诊断：`diagnostics\llama-local-chat.log`

## 添加其他本地模型

- 其他 llama.cpp 文本模型：可在“设置 > 文本模型”选择仅含一个 GGUF 的本地目录；也可在 `runtime\model-profiles.json` 的 `profiles` 中新增唯一 `id`，填写显示名称、GGUF 路径、API model 和服务参数，重启客户端后即可选择。
- 其他 ComfyUI 视频模型：把 API 工作流放入 `runtime\workflows\`，在 `runtime\video-model-profiles.json` 新增档案并声明节点输入映射、所需节点和尺寸/时长/步数能力，重启客户端后即可在视频模型设置中选择。
- 其他服务协议需要新增适配器；客户端不会把不兼容的后端伪装成上述两种协议。

WinUI 桌面版重新构建并注册时运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy-winui.ps1`。脚本会运行核心测试、更新 loose-layout AppX，并强制核对源码构建 DLL 与桌面 AppX DLL 的 SHA-256；不要只运行普通 `dotnet build` 后就从桌面入口验收。

WinUI 已确认的排版、交互与发布保留约束记录在 [`docs/winui-ui-baseline.md`](docs/winui-ui-baseline.md)，机器可读数值记录在 `docs/winui-ui-baseline.json`。`deploy-winui.ps1` 会在构建前强制验证该基线；滚动、恢复和首帧等时间过程仍需按文档执行真实窗口验收。

旧 WinForms 版本如需单独重新构建，运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1`。
需要同时执行本地模型、两轮对话和 MemOS 读写验收时，增加 `-RunSelfTest`。构建会把最终 EXE 的路径、大小、时间戳和 SHA-256 写入 `diagnostics\publish-artifact.json`。

## UI 回归检查

`build.ps1` 在发布前运行核心行为测试和桌面 UI 回归测试。UI 测试会执行以下检查：

- 比较 96、120、144 DPI 的组件图片基线，对应 100%、125%、150% 缩放，并覆盖普通、悬停、按下、禁用、焦点及开关状态。
- 单独比较 96、120、144 DPI 的聊天区背景基线，锁定右上柔光、斜向光带、节点和左侧阅读留白。
- 渲染程序实际使用的按钮和开关，检查圆角边缘与父背景的合成结果。
- 渲染实际聊天记录控件，确认背景绘图已接入最终控件；滚轮测试同时检查滚动不会主动刷新整个视口。
- 实例化退出确认窗口，检查按钮是否使用统一的自绘控件。
- 通过 Windows 键盘消息验证按钮和开关的空格键操作。
- 把本次候选图写入 `diagnostics\ui-regression\`，方便目视检查差异。

设计经过确认后，可用下面的命令显式更新图片基线：

```powershell
dotnet run --project .\tests\QwenLocalChat.UiTests\QwenLocalChat.UiTests.csproj -c Release -- --approve
```

普通构建只比较现有基线。图片变化会让构建停止，并保留候选图供检查。更新基线前需要同时检查原参考图、旧基线、候选图和完整窗口；已确认的背景或装饰变化需要用户批准。
