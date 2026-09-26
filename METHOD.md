# Local AI 验收

## 当前方法

界面改动分三层，沿用现有测试，不换框架。

1. 库行为：`local-chat/tests/QwenLocalChat.Tests`。
2. 结构合同：`local-chat/tests/verify-winui-ui-baseline.ps1`。
3. 正在运行的窗口：本机 `winapp ui`，选择器用 `AutomationId`。目标窗口用标题 `Local AI` 且类名 `WinUIDesktopWin32WindowClass`。进程名还会匹配一个很小的状态窗，不能拿它当主窗口。设置抽屉关掉后控件仍留在树上，断言用 `CloseSettingsButton` 的 `IsEnabled=False`。点左侧空白用鼠标 `click` 打在左侧 `AppIconMark` 上，让点击落在遮罩上。

窗口层脚本：`local-chat/tests/verify-settings-scrim.ps1`。应用须已用当前构建打开。

## 已否决

| 方法 | 否决原因 | 何时可以重开 |
| --- | --- | --- |
| 换成 MSTest WinUI 测试宿主或 Appium | 设置规则已在 Core。再引入一套宿主不增加这扇抽屉的证据。 | 出现必须在 UI 线程构造 XAML、并且 `winapp ui` 打不到的交互 |
| `wait-for --gone` 判断设置已关闭 | 2026-09-26 真机：关闭后 `CloseSettingsButton` 仍在树里，`IsEnabled=False`，`IsOffscreen=False`，截图已回到聊天页 | 抽屉改为从可视树移除 |
| 录制回放 | 界面测试只留少量按标识操作的检查 | 不重开 |

## 轮次

当前方法已失败 0 轮；本轮证据：2026-09-26 对重新部署后的 Local AI，`verify-settings-scrim.ps1` 打开设置后点左侧图标位置，`CloseSettingsButton` 从 `IsEnabled=True` 回到 `False`，脚本退出码 0。更早一次用进程名等待时，命令附到了 135×45 的状态窗，主窗口要按标题和类名选。

## Spike

一手源：Microsoft `winapp ui`（windows-dev-docs `hub/apps/dev-tools/winapp-cli/ui-automation.md`，2026-05-05；`hub/apps/develop/ai-assisted/testing.md`，2026-05-13）。界面层保持少量，库测试承担主证据（Martin Fowler，Test Pyramid，2012-05-01）。

真物证是正在运行的 Local AI 窗口，不是替身夹具。命中：`winapp ui` 能打开设置，并用左侧点击把它收回。打偏两处：用元素消失判断关闭；用进程名选窗口会打到状态窗。生产脚本已避开这两处。
