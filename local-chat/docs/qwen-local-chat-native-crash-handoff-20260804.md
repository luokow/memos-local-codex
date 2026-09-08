# Qwen Local Chat WinUI 闪退问题交接（给 Grok）

更新时间：2026-08-04  
项目目录：`D:\codex\experiments\memos-local-codex\local-chat`

## 1. 结论与当前状态

- **已验证：闪退尚未修复。** 最新一次 Windows 错误报告（WER）生成于 2026-08-04 00:07:22，仍为 `Microsoft.UI.Xaml.dll` 的 `c0000005` 访问冲突，故障偏移 `0x2a10c2`。
- **已验证：设置页面不是必要触发条件。** 用户没有打开设置时也发生过闪退；自动连续打开/关闭设置 100 次没有稳定复现。
- **已验证：问题位于 WinUI 主界面的原生 XAML/输入消息路径。** 已捕获的完整转储显示主 UI 线程从 `user32 -> CoreMessagingXP -> Microsoft_UI_Input -> Microsoft_UI_Xaml` 进入崩溃点。
- **已验证：不是模型接口、MemOS 或设置保存代码直接抛出的托管异常。** 崩溃为 `Microsoft.UI.Xaml.dll` 内部访问无效地址的原生访问冲突。
- **已观察：存在两个重复出现的故障偏移。** `0x15fb26` 与 `0x2a10c2`；它们可能是同一生命周期问题的不同路径，也可能是两个问题，尚未证明。
- **待验证：更可能与输入/指针事件和 XAML 对象生命周期有关。** 当前证据支持这个排查方向，但尚未定位到具体控件或方法。
- 当前没有运行中的 `QwenLocalChat.WinUI`、`procdump64` 或 `cdb` 进程。中断遗留的符号下载 PowerShell/curl 循环已经停止。
- 本目录当前不是 Git 仓库，不能依靠提交记录回退；修改前应先复制相关源文件或建立本地版本控制快照。

## 2. 用户可见问题

应用会在不固定时间直接退出。最初看起来像是反复点击“设置”导致，但用户后来确认：不打开设置也会闪退，多点几下其他界面区域也可能出现。因此不能继续只围绕设置面板做延迟、防抖或显隐补丁。

### 预期行为

应用在以下操作及空闲状态下都应保持运行：

- 长时间空闲；
- 长文本流式输出和 Markdown 渲染；
- 鼠标快速移动、悬停、点击、切换输入焦点；
- 滚动长对话、跳转到回复、清空会话；
- 打开、修改、重置、关闭设置；
- 最小化与恢复窗口；
- 使用中文输入法输入。

### 实际行为

应用偶发直接退出，WER 持续记录 `Microsoft.UI.Xaml.dll` 中的 `c0000005`。

## 3. 已完成的改动

### 3.1 Windows App SDK 维护升级

文件：`src\QwenLocalChat.WinUI\QwenLocalChat.WinUI.csproj`

- `Microsoft.WindowsAppSDK` 从 `1.8.260317003` 升级到 `1.8.260508005`。
- 已离线还原、构建和部署。
- **结论：升级没有解决闪退。** 00:07 的新构建仍崩溃在 `Microsoft.UI.Xaml.dll + 0x2a10c2`。
- 新旧程序实际加载的系统 Runtime 仍是：
  `C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.1.8_8000.921.1539.0_x64__8wekyb3d8bbwe\Microsoft.UI.Xaml.dll`
- WER 显示故障模块版本 `3.1.8.0`；本机文件产品版本为 `3.1.8.2607`。

### 3.2 设置面板收窄

文件：`src\QwenLocalChat.WinUI\RuntimeSettingsPanel.xaml`

- 设置面板宽度由 `720` 改为 `620`。
- `NumberBox` 使用宽度 `164` 的统一样式。
- `TextBox` 使用宽度 `460` 的统一样式。

这项属于已完成的独立 UI 要求，不是闪退修复。

### 3.3 UI 基线约束

文件：`tests\verify-winui-ui-baseline.ps1`

- 固定 Windows App SDK 版本 `1.8.260508005`。
- 固定设置面板宽度 `620`。
- 检查设置中的全部 `NumberBox`、`TextBox` 使用紧凑样式。

### 3.4 最近一次已通过的构建验证

- 构建：0 warning，0 error。
- 核心测试：31 项通过。
- WinUI UI 基线：通过。
- 部署：成功。
- 当前 x64 Debug/AppX 程序集 SHA-256：
  `475529C4AC7107B4EB3D00C365CBC2F02DDB732A5D6C9D5F37DB7F37CE6D5FF4`

注意：这些结果仅说明构建和既有测试通过，不能证明闪退已修复。

## 4. 已做过但未复现闪退的压力测试

- 设置面板自动打开/关闭 100 次：未崩溃。
- 新构建中快速调用“清空会话”约 300 次：未崩溃。
- 约 3000 个中文字符的 Markdown 长回复完整输出：当次未立即崩溃。

这些是反证：不要把“设置打开次数”“清空会话”或“单次长输出”单独当成充分复现条件。

## 5. 崩溃记录

最近的 WER 记录如下：

| 时间 | 模块 | 异常 | 偏移 |
|---|---|---|---|
| 2026-08-04 00:07:22 | Microsoft.UI.Xaml.dll 3.1.8.0 | c0000005 | 0x2a10c2 |
| 2026-08-03 23:57:43 | Microsoft.UI.Xaml.dll 3.1.8.0 | c0000005 | 0x15fb26 |
| 2026-08-03 23:02:58 | Microsoft.UI.Xaml.dll 3.1.8.0 | c0000005 | 0x15fb26 |
| 2026-08-03 23:01:49 及更早多次 | Microsoft.UI.Xaml.dll 3.1.8.0 | c0000005 | 0x2a10c2 |

WER 目录：

`C:\ProgramData\Microsoft\Windows\WER\ReportArchive`

完整转储对应 2026-08-03 23:57:31/23:57:43 的 `0x15fb26` 崩溃。进程当时已运行 51 分 11 秒，说明它不只发生在启动或设置打开瞬间。

## 6. 已保存的关键证据

### 6.1 完整进程转储

路径：

`diagnostics\crash-dumps\QwenLocalChat.WinUI.exe_260803_235731.dmp`

- 大小：879,814,635 bytes
- SHA-256：`D46298B8FADCA9AD7FA40D1FA28AA02541197A99F756CC525E418F8DD5246024`
- 由官方 Sysinternals ProcDump 捕获，参数为未处理异常 + full dump。
- **不要删除或覆盖这个文件。**

### 6.2 无完整 PDB 时的原始调用栈

路径：

`diagnostics\crash-raw-stack-260803-235731.txt`

- SHA-256：`A47A730A0DBFE8A35788C1135E817EB120477890C24CFE8B85299979B63922EE`
- 崩溃指令：

```text
Microsoft_UI_Xaml+0x15fb26
mov rsi,qword ptr [rax]
rax=00000000086f4116
```

`rax` 指向不可读地址，属于原生无效指针访问。

调用链的模块层级：

```text
user32
  -> CoreMessagingXP
  -> Microsoft_UI_Input
  -> Microsoft_UI_Xaml
  -> Microsoft_UI_Xaml+0x15fb26 (c0000005)
```

崩溃发生在主 UI 线程的 Windows 消息/输入处理过程中。

### 6.3 已安装调试工具

- WinDbg：`1.2606.22001.0`
- CDB：
  `C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2606.22001.0_x64__8wekyb3d8bbwe\amd64\cdb.exe`
- ProcDump：
  `diagnostics\procdump\procdump64.exe`
- ProcDump 来源压缩包 SHA-256：
  `68E057587B0FD654EFA095F76D80D633C0E5C60EA26FD3E7C0011C076BB2D00C`
- ProcDump 数字签名已验证为 Microsoft 有效签名。

官方资料：

- ProcDump：<https://learn.microsoft.com/sysinternals/downloads/procdump>
- WinDbg/CDB：<https://learn.microsoft.com/windows-hardware/drivers/debugger/command-line-options>
- WER 本地转储：<https://learn.microsoft.com/windows/win32/wer/collecting-user-mode-dumps>
- Windows App SDK releases：<https://github.com/microsoft/WindowsAppSDK/releases>

## 7. 符号下载进度

### 7.1 Microsoft.UI.Input.pdb：已完成

路径：

`diagnostics\symbols\Microsoft.UI.Input.pdb\5523FFD9312FE7FC34A4A5D9E180EECB1\Microsoft.UI.Input.pdb`

- 大小：4,313,088 bytes
- SHA-256：`2846330EA474FDDB58BA65A1B82A0F4AAAE83FA6EE23589C03161BCA21CAFEE5`

### 7.2 Microsoft.UI.Xaml.pdb：未完成，可续传

路径：

`diagnostics\symbols\Microsoft.ui.xaml.pdb\A33929855FBC46B645E314ECEB4BA0BF2\Microsoft.ui.xaml.pdb`

- 当前大小：40,665,088 bytes（停止残留下载进程时核对）
- 完整目标大小：301,985,792 bytes
- PDB key：`A33929855FBC46B645E314ECEB4BA0BF2`
- 下载地址：
  `https://msdl.microsoft.com/download/symbols/Microsoft.ui.xaml.pdb/A33929855FBC46B645E314ECEB4BA0BF2/Microsoft.ui.xaml.pdb`

在 PowerShell 中执行下面的命令可以从现有文件续传。网络不稳定时可重复执行，每次最多 120 秒：

```powershell
$file = 'D:\codex\experiments\memos-local-codex\local-chat\diagnostics\symbols\Microsoft.ui.xaml.pdb\A33929855FBC46B645E314ECEB4BA0BF2\Microsoft.ui.xaml.pdb'
$url = 'https://msdl.microsoft.com/download/symbols/Microsoft.ui.xaml.pdb/A33929855FBC46B645E314ECEB4BA0BF2/Microsoft.ui.xaml.pdb'
curl.exe -4 --noproxy "*" --fail --location --continue-at - --max-time 120 --output $file $url
(Get-Item -LiteralPath $file).Length
```

只有大小严格等于 `301985792` 才能视为下载完成。不要对不完整文件运行最终符号分析。

## 8. Grok 下一步应按此顺序处理

### 第一步：完成 XAML PDB，解析现有完整转储

不要使用全模块 `.reload /f`，之前它会因大量符号加载而长时间卡住。只加载 XAML 和 Input 两个模块：

```powershell
$cdb = 'C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2606.22001.0_x64__8wekyb3d8bbwe\amd64\cdb.exe'
$root = 'D:\codex\experiments\memos-local-codex\local-chat'
$dump = "$root\diagnostics\crash-dumps\QwenLocalChat.WinUI.exe_260803_235731.dmp"
$symbols = "$root\diagnostics\symbols"
$log = "$root\diagnostics\crash-symbolized-260803-235731.txt"

& $cdb -sins -y $symbols -z $dump -logo $log -c ".reload /f Microsoft.UI.Xaml.dll; .reload /f Microsoft.UI.Input.dll; !analyze -v; .ecxr; kv 80; q"
```

输出必须回答：

1. `Microsoft_UI_Xaml+0x15fb26` 的实际方法名；
2. 它上游的 Input 方法名；
3. 调用链涉及哪类 XAML 对象、事件或控件；
4. 是否能从寄存器、对象地址或 shadow stack 看出对象已释放/损坏。

### 第二步：为 `0x2a10c2` 捕获新的 full dump

现有 full dump 是 `0x15fb26`，最新 `0x2a10c2` 只有 WER。先启动应用，再运行：

```powershell
$root = 'D:\codex\experiments\memos-local-codex\local-chat'
& "$root\diagnostics\procdump\procdump64.exe" -accepteula -ma -e -w QwenLocalChat.WinUI.exe "$root\diagnostics\crash-dumps"
```

保持监控，直至捕获下一次自然崩溃。不要用强制结束应用制造假转储。

### 第三步：根据符号栈映射到应用代码

优先检查与栈中具体 XAML/Input 方法相连的事件和对象，不要再泛化修改整个设置面板。重点候选仅作为待验证线索：

- 指针进入/退出、悬停 VisualState、焦点切换、文本选择与中文输入法事件；
- `ItemsRepeater`/列表项被替换或移除后仍排队执行的滚动/聚焦回调；
- `DispatcherQueue.TryEnqueue`、异步 continuation 或延迟回调捕获已经移除的 `TranscriptEntry`/控件；
- 设置面板、Markdown 内容控件或复制按钮在 Unload/Visibility 切换后仍被输入系统引用；
- 清空会话、替换 ItemsSource 与 `ScrollIntoView` 同时发生时的生命周期竞争。

代码中已注意到一个需要审计的方向：排队执行的 `ScrollIntoView` 回调可能捕获 `TranscriptEntry`，而条目可能已被替换/删除。但 300 次快速清空会话没有复现，所以这不是已确认根因。

### 第四步：做输入法 A/B 验证，但不要先归罪输入法

转储中加载了：

- `SogouTSF.ime`
- `SogouPY.ime`
- `PicFace64.dll`

由于调用栈经过 `Microsoft_UI_Input`，可分别使用微软拼音和搜狗输入法执行同一套指针/输入/焦点压力测试。只有当复现率、转储栈或模块调用能稳定区分两组时，才能判断输入法是否参与。当前只属于**待验证线索**。

### 第五步：只做一个有证据的根因修复

修复应针对符号栈确认的对象生命周期或事件订阅问题，例如：

- 在对象 Unloaded/移除时取消事件、取消排队操作或使 operation 失效；
- 排队回调执行前验证页面仍 loaded、条目仍在集合中、控件仍属于当前 visual tree；
- 用稳定 ID 重新查找当前对象，不长期捕获易失效的 UI 元素；
- 把集合修改、滚动和焦点操作串行化到 UI Dispatcher，并增加 generation/cancellation token；
- 若证据指向特定 WinUI 控件缺陷，先做最小替换或规避并保留对照测试。

不要继续用延迟、点击防抖、隐藏异常或 catch 托管异常来掩盖原生 `c0000005`。

## 9. 修复后的回归与验收标准

### 构建基线

- [ ] 离线 restore 成功。
- [ ] build 0 warning、0 error。
- [ ] 31 项核心测试全部通过。
- [ ] `tests\verify-winui-ui-baseline.ps1` 通过。
- [ ] 设置面板仍为 620 宽，紧凑输入控件没有回退。
- [ ] 部署成功，并记录已部署 DLL SHA-256。

### 行为基线

在 ProcDump 监控下执行：

- [ ] 空闲至少 60 分钟；
- [ ] 至少一次长篇 Markdown 流式回复，期间滚动、选择和复制；
- [ ] 鼠标快速悬停/点击所有按钮、开关、聊天内容、输入框和设置项；
- [ ] 中文输入、候选框、焦点切换；
- [ ] 设置打开、修改、重置、关闭循环；
- [ ] 长对话滚动、跳转、清空会话；
- [ ] 最小化/恢复多次；
- [ ] 不产生新的 WER `c0000005`，不产生新的 ProcDump 崩溃转储。

只有以上当前证据都通过，才能声明“已修复”。一次未崩溃、测试通过或构建成功都不够。

如果仍崩溃：

1. 保留新 dump 和 WER；
2. 用相同符号命令解析；
3. 对比新栈是否仍落在 `0x15fb26` 或 `0x2a10c2`；
4. 记录触发前 30 秒的输入、焦点、集合修改和窗口状态；
5. 不重复同一类无证据补丁。

## 10. 已排除或不可直接下结论的方向

- “设置保存导致闪退”：设置不是必要条件，未成立。
- “多点几下设置导致”：没有稳定复现，未成立。
- “长文本超过 1024 token 导致原生闪退”：输出长度问题已单独处理，不能解释主 UI 线程 XAML 访问冲突。
- “升级 Windows App SDK 就会修好”：新构建已再次崩溃，已证伪。
- “搜狗输入法就是根因”：只有模块加载和 Input 调用链线索，证据不足。
- “300 次清空未崩溃说明滚动安全”：只能说明该脚本没有命中竞态，不能排除生命周期竞争。

## 11. 给接手者的约束

- 先解析 dump，再改代码。
- 每个结论注明“已验证 / 已观察 / 待验证”。
- 保留 880 MB full dump、WER 和原始栈。
- 不要删除已完成的设置面板收窄和 UI 基线。
- 不要把包升级、构建成功或短期运行正常写成闪退修复。
- 若要做破坏性清理、改系统输入法、卸载 Runtime 或调整系统权限，先征得用户明确授权。

