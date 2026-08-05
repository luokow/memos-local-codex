# Qwen 本地聊天

双击桌面的 `Qwen Local.lnk` 即可打开 WinUI 3 本地聊天窗口。首次注册或重新构建后，可运行 `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\create-desktop-shortcut.ps1` 重建入口。程序使用 D 盘现有的 Qwen3.5-9B、llama.cpp 和 MemOS，不调用云端 API。

## 界面

- 固定使用黑白深色主题，背景包含静态柔光、星点和星图连线。
- 应用图标为通用的黑白对话气泡，不包含模型或厂商品牌标记。
- 界面使用 WinUI 3/XAML，不加载联网图片、字体或第三方 UI 组件。

## 使用方式

- 首次启动时，“使用 MemOS 长期记忆”和“保存聊天日志”都关闭。
- 修改开关后会立即保存，下次启动继续使用你的选择。
- `Enter` 发送，`Shift+Enter` 换行。
- “清空会话”只清除窗口上下文，不删除 MemOS 和记忆日志。
- 日志开启后，可见问答保存到 `logs\YYYY-MM-DD`；隐藏召回内容不会写入日志。

## 模型生命周期

- `127.0.0.1:18135` 健康时直接复用，不重复加载模型。
- 服务未运行时，程序从父目录启动 Qwen3.5-9B，最多等待 120 秒。
- 如果模型由本窗口启动，关闭窗口时可选择停止模型或保持后台运行。
- 程序不创建 Windows 服务、计划任务或开机启动项。

## 本地数据

- 设置：`data\settings.json`
- 聊天日志：`logs\`
- 自测结果：`diagnostics\self-test.json`
- 模型诊断：`diagnostics\llama-local-chat.log`

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
