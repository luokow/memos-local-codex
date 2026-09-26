# Local AI 验收卡

通用顺序在技能 `verification-before-completion`。这里只填这一次界面改动的五项。

| 项 | 这次 |
| --- | --- |
| 库测试 | 设置记录卡走 `QwenLocalChat.Tests`。点空白收回没有库规则。 |
| 结构标识 | `verify-winui-ui-baseline.ps1` 守 `SettingsScrim`，关闭时不接收点击。 |
| 打开动作 | 当前页可见的设置按钮：`SettingsButton`、`VideoSettingsButton` 或 `HanhuaSettingsButton`。 |
| 点击目标 | 鼠标点击左侧 `AppIconMark`，让点击落在遮罩上。 |
| 完成后的属性 | `CloseSettingsButton` 的 `IsEnabled=False`。 |

主窗口标题是 `Local AI`，类名是 `WinUIDesktopWin32WindowClass`。同进程还有一个小状态窗。脚本：`local-chat/tests/verify-settings-scrim.ps1`，应用须已用当前构建打开。

2026-09-26：该脚本退出码 0。同一天，用元素消失判断关闭会超时，因为按钮还在树上；用进程名选窗口会打到 135×45 的状态窗。
