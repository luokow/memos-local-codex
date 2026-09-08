# Local AI 汉化工作区

日期：2026-09-08  
状态：已与用户确认方向，并批准本机 Qwen / 阿里云引擎开关；实现前以本文为准。

## 目标

在 Local AI 顶部增加第三个工作区「汉化」，调度已经跑通的游戏文本汉化和漫画图片填字/嵌字。汉化页默认走本机 Qwen 对话模型，也可在页内切到阿里云 qwen-mt。Local AI 负责选目录、选引擎、显存互斥、进度和打开结果。抽字、OCR、涂白、嵌字仍由现有 Python 流水线执行。

硬约束：**聊天、视频、阿里云一键汉化、MTool 自配与必应、18765 适配器的原有行为不得改变。** 汉化是新增工作区，不是改写现有两条模式。

## 明确不做

- 不把汉化任务丢进聊天气泡或聊天附件。
- 不在 C# 里重写 RPG Maker 抽写回、manga-image-translator、OCR、涂白。
- 不把阿里云 qwen-mt、18765 适配进程、LinguaGacha 默认引擎、MTool 自配的默认配置切到本地。汉化页的阿里云选项只调用现有 `translate_direct.py` / `one_click_rm.py` 无 `--local` 路径，不改那些默认。
- 不自动启动、退出或提交 MTool；不清理自配通道已经落盘的译文。
- 不在回复、日志、设置里打印接口密钥。
- 不把 Unity / Ren'Py / WOLF / 加密 RPG Maker 纳入这一期。
- 不把未批准的图线工作目录纳入任务或测试。
- 不把「汉化」加入「打开应用后启动」选项；启动模型仍只有文本 / 视频 / 不自动启动。
- 不给设置抽屉增加第三栏；设置仍是「文本模型 / 视频模型」。

## 原有功能保护

实现时只允许新增汉化相关文件，以及下面列出的接缝。未列出的聊天、视频、设置、退出路径保持原样。

| 必须保持 | 验收 |
| --- | --- |
| 云端一键汉化 bat 仍走阿里云 qwen-mt，命令行不含 `--local` | 内嵌汉化现有离线测试继续绿 |
| 本地一键汉化 bat 仍调用 `one_click_rm.py --local` | 核对 bat 正文 |
| `translate_direct.py` 无 `--local` 时仍读云端配置 | 现有离线测试 |
| MTool 自配的模型名、地址、密钥文件不被本功能改写 | 实现 diff 不含那些配置；测试不启动 MTool |
| 聊天发送、会话、附件、MemOS、日志、继续生成 | 现有 `QwenLocalChat.Tests` 全绿 |
| 视频生成、会话、显存互斥键 `enforce_text_video_model_exclusivity` 仍默认开、仍可关 | 现有 settings 往返测试；键名不改 |
| 切「聊天 / 视频」本身不取消正在跑的任务 | 汉化加入后同样成立 |
| 默认启动仍是当前文本模型 | `startup-model-selection-persists-and-normalizes` |
| 视频中断恢复优先于汉化中断提示 | 见「启动」 |
| 聊天输入卡、视频输入卡的字体、圆角、64 高底栏、模式工具栏左对齐 SelectorBar | 更新后的 WinUI 基线 + `verify-winui-ui-baseline.ps1` |
| 退出三选择（仅退出 / 退出并关闭模型服务 / 取消） | 现有协调器测试 |

若某项为了汉化必须改接缝，先写测试锁住旧行为，再加新分支。禁止顺手改聊天 Markdown、视频提示词编译、采样参数默认值。

允许改动的接缝：

1. `MainPage.xaml` 的 `ModeSelector` 增加第三项，三路显示切换。
2. `ModeSelector_SelectionChanged` 从「是否视频」改成三态；选中「聊天」时的可见性必须与改前「非视频」一致。
3. `PrepareForVideoAsync` / 聊天发送入口增加「汉化任务占用中则拒绝」。
4. `LocalChatSettings` 增加三个可空路径字段和 `hanhua_engine`；路径缺省为空，引擎缺省 `local`，旧设置文件缺键时其它字段与现在相同。
5. 设置「本地服务」卡末尾追加汉化路径三字段；互斥开关文案改为说明聊天、视频、汉化三者互斥，JSON 键不变。引擎下拉在汉化页，不进设置抽屉。
6. `docs/winui-ui-baseline.md` 与 `verify-winui-ui-baseline.ps1` 把模式工具栏从两项更新为三项。
7. 内嵌汉化 Python 增加可选 `--progress-jsonl`、图片填字 `--mt`、和新的抽字入口；默认不传这些旗标时 stdout 仍可被人读，bat 行为不变。现有脚本的默认路径保持原样，仅在设置了 `HANHUA_MIT_ROOT` 时改用设置值。不把密钥写入 Local AI 设置、命令行或环境变量。

## 架构

Local AI 顶部为 `聊天 | 视频 | 汉化`。汉化页只调度，不自己翻句。

- WinUI：`HanhuaPanel` 负责选目录、进度、打开结果。
- Core：`HanhuaJobStore` 写 `data/hanhua-jobs.json`；`HanhuaCommand` 只拼 Python 命令；`HanhuaProcessHost` 启进程、读进度、取消。
- 显存：聊天生成、视频生成、汉化任务三选一。本机翻字需要 Qwen；阿里云翻字不启停 Qwen；OCR/嵌字仍要停 Qwen 与视频。
- Python（路径来自设置，不写死盘符）：游戏 `one_click_rm.py`，本机加 `--local`，阿里云不加；图片 `ocr_extract_local.py` → `fill_ocr_local.py`（本机默认，阿里云加 `--mt`）→ `typeset_ocr_local.py`。
- 本机翻字打 `127.0.0.1:18135`。阿里云翻字由 Python 读取现有 MTool 配置，C# 不经手密钥。OCR/嵌字只打 manga-image-translator。

C# 不调用聊天补全接口做翻译，也不调用阿里云。翻句仍由 Python 执行：本机走 `local_qwen.py`（system 加 user、思考关闭、保护游戏控制码）；阿里云走现有 qwen-mt payload。Local AI 只在本机翻字阶段保证该服务健康，并在 OCR/嵌字阶段把它停掉。

## 组件

### `HanhuaPanel`（WinUI UserControl）

对标 `VideoGenerationPanel`：上主区、中输入卡、下 64 高命令栏。不使用聊天气泡列表。

主区：只读任务日志。游戏完成后可打开中文副本。图片完成后可预览输出目录最后一张 png。没有成品时显示「选择游戏或图片目录后开始」。

输入卡：

- 标题「汉化任务」
- 右侧两个下拉：种类（游戏文本 / 漫画图片）、引擎（本机 Qwen / 阿里云）。默认引擎是本机 Qwen。位置对标视频生成模式下拉。
- 只读路径框加「选择目录」
- 主按钮在正文行最右：空闲为「开始汉化」，运行中为「取消」

底栏对标视频生成：一行状态摘要、进度条、条右侧百分比。运行中摘要为「正在{阶段}  已用时 hh:mm:ss  剩余约 hh:mm:ss  done/total 句或页」；百分比只出现在条右侧。剩余时间用当前阶段 done/total 做线性外推，规则与视频相同。没有总数时只走已用时。结束行仍带已用时。每秒刷新时钟。右侧：设置 / 打开结果；运行中额外显示「取消」。失败时「查看详情」。不在底栏再放「开始汉化」。

汉化页不提供多窗口会话。一次只跑一个任务。历史留在任务文件里供续跑，界面只展示当前任务。

顶栏选中汉化时：

- 隐藏聊天会话工具、聊天记录、聊天输入、聊天底栏、聊天专属提示。
- 隐藏视频会话工具、视频页、视频模型状态芯片。
- 显示文本模型摘要和「启动」。
- 显示 Qwen 状态芯片；OCR/嵌字阶段文案改为「汉化占用 GPU」，不新增第四枚芯片。
- 点设置打开文本模型分区，不打开视频分区。

切换模式不取消汉化任务。任务状态只在汉化页展示，不写进聊天通知条。

### Core 类型

`HanhuaKind`: `Game` | `Image`

`HanhuaEngine`: `LocalQwen` | `Aliyun`。默认 `LocalQwen`。JSON 值为 `local` / `aliyun`。

`HanhuaPhase`：游戏为 `Copy` → `Extract` → `Translate` → `Inject`；图片为 `Ocr` → `Fill` → `Typeset`。

`HanhuaJobStatus`: `Queued` | `Running` | `Cancelling` | `Succeeded` | `Failed` | `Interrupted`

`HanhuaJob` 字段：`Id`、`Kind`、`Engine`、`Phase`、`Status`、`SourcePath`、`WorkPath`、`OutputPath`、`Done`、`Total`、`Message`、`Error`、`StartedUtc`、`UpdatedUtc`

`HanhuaJobStore`：原子写 `data/hanhua-jobs.json`。最多保留最近 20 条；正在跑或可续跑的一条始终保留。续跑使用该任务记录的引擎，不改用户当前下拉，除非用户重新开始。

`HanhuaCommand`：根据设置和引擎拼进程参数，不启动进程。本机游戏带 `--local --progress-jsonl`；阿里云游戏只带 `--progress-jsonl`，不含 `--local`。本机填字不带 `--mt`；阿里云填字带 `--mt`。不把云端密钥放进参数或环境变量。

`HanhuaProcessHost`：`python -u` 跑脚本，UTF-8，工作目录为汉化工具包根目录。取消即结束该进程树。不在 Python 里自己启停 llama-server。Local AI 把设置里的 MIT 目录写入环境变量 `HANHUA_MIT_ROOT`；脚本有该变量就用它，没有则沿用现有默认路径，保证双击 bat 的行为不变。

### 设置

`LocalChatSettings` 新增：

| JSON 键 | 含义 | 默认 |
| --- | --- | --- |
| `hanhua_pack_root` | 内嵌汉化工具包根目录 | 空 |
| `hanhua_python_exe` | Python 可执行文件 | 空 |
| `hanhua_mit_root` | manga-image-translator 根目录 | 空 |
| `hanhua_engine` | 汉化页引擎：`local` 或 `aliyun` | `local` |

缺键或路径为空字符串时：汉化页拒绝开始，状态行提示去设置里填路径。`hanhua_engine` 缺键、空值或未知值一律当作 `local`。Core 不得写死固定盘符或当前用户主目录。本机可在设置里填现网路径。

路径和引擎变更即时生效，不重启模型。引擎下拉写回 `hanhua_engine`，不进设置抽屉。

互斥开关键名仍为 `enforce_text_video_model_exclusivity`，默认开启，旧测试继续有效。开启时聊天生成、视频生成、汉化任务不能重叠。关闭时不自动卸另一模型，但仍不允许第二个任务在已有生成或汉化任务时启动。

GPU 细则：

- 本机游戏翻字、本机图片填字：先释放视频（互斥开时），再启动或复用 Qwen。
- 阿里云游戏翻字、阿里云图片填字：不启停 Qwen；Qwen 正在聊天生成时仍拒绝开始汉化。
- 图片 OCR / 嵌字：无论引擎，先停本窗口可停的 Qwen 与视频；停完后若文本口仍健康则拒绝。

## 数据流

### 游戏文本

1. 用户选中含 `Game.exe` 的目录（可向上查找游戏根，规则与现有 `one_click_rm.find_game_root` 一致；其它引擎直接拒绝并说明）。
2. 聊天正在生成则拒绝开始，不静默杀掉聊天。视频正在生成则拒绝开始。
3. 本机 Qwen：互斥开启则先释放视频，再启动或复用 Qwen。阿里云：不启停 Qwen。
4. 本机运行 `one_click_rm.py --local --progress-jsonl`；阿里云运行 `one_click_rm.py --progress-jsonl`（无 `--local`）。
5. 成功后输出为带 `-cn` 后缀的副本；「打开结果」选中中文版启动脚本或游戏主程序。
6. 失败或取消：已经写进译文表的句子保留；下次对同一源目录接着翻空行。

游戏脚本仍只改副本，不改原版。

### 漫画图片

源目录是含 png 的文件夹。工作目录为工具包下 `work/_local_ai/<job-id>/`，内有 `typeset_in`、译文表、`out`。

阶段必须串行，由 C# 在阶段之间切换 GPU：

1. 抽字：停 Qwen 与视频，跑 `ocr_extract_local.py --progress-jsonl`，写出空译文表和 `typeset_in`。
2. 填字：本机启动 Qwen 并做健康检查（loopback 禁用系统代理），跑 `fill_ocr_local.py --progress-jsonl`。阿里云不启停 Qwen，跑 `fill_ocr_local.py --mt --progress-jsonl`。已填且不等于原文的键跳过。
3. 嵌字：再停 Qwen，跑 `typeset_ocr_local.py --progress-jsonl`。MIT 保持 translator=none，用环境变量指向已填译文表，不让 MIT 自己调模型。

游戏任务与图片任务不能并行。图片三阶段是同一个任务，取消停在当前阶段；续跑从该阶段重来（填字跳过已填）。

### 进度协议

脚本在收到 `--progress-jsonl` 时，每条进度一行 JSON，写 stdout。字段为 `type`（`phase` / `progress` / `done` / `error`）、`phase`、`done`、`total`、`message`、`output`、`empty`。

`phase` 取值：`copy` `extract` `translate` `inject` `ocr` `fill` `typeset`。

未传 `--progress-jsonl` 时不得改变现有 bat 的可读输出。人类日志可继续写 stderr。解析失败的 stdout 行记入任务日志，不把任务标失败，除非进程退出码非 0。

### 启动

保持现有顺序：若视频有中断任务，仍切到视频页并走现有恢复。仅当视频没有中断任务、且汉化任务文件里有 Running 或 Interrupted 时，才切到汉化页并提示有未完成任务。不自动开始，用户点开始或续跑。

应用打开仍不因为汉化页而启动任何模型。

## 错误处理

| 情况 | 行为 |
| --- | --- |
| 路径未配置，或 Python / MIT / 脚本缺失 | 不启动进程；一行中文原因 |
| 本机翻字时 Qwen 健康检查失败（含系统代理把回环打成 502） | 提示先启动文本模型；探测必须禁用代理。阿里云翻字不走这条 |
| 本机翻字时视频仍占 GPU | 互斥开启则先释放视频；释放失败则拒绝开始。阿里云翻字不要求释放 Qwen |
| OCR/嵌字时本机文本口仍在听 | 先停本窗口可停的 Qwen；仍健康则拒绝 |
| 聊天正在生成 | 拒绝开始汉化，不取消聊天 |
| 汉化正在跑 | 拒绝发送聊天、拒绝开始视频 |
| Python 非 0 退出 | 状态 Failed，详情为尾部日志 |
| 用户取消 | 结束进程树；游戏已填句子保留；图片停在当前阶段 |
| 脚本把日文原样写回或无效刷屏 | 仍由现有 Python 拒绝逻辑处理，C# 不重复实现 |

## 测试

不跑真实 GPU、不启动 MTool、不访问云端。

Core（加入 `tests/QwenLocalChat.Tests/Program.cs` 的具名用例）：

- 旧设置文件无汉化键时三个路径为空、引擎为 `local`，其它字段与现在一致。
- 填写汉化路径和 `aliyun` 引擎后往返磁盘，不影响互斥开关默认开启。
- 本机游戏命令含 `--local` 与 `--progress-jsonl`；阿里云游戏命令含 `--progress-jsonl` 且不含 `--local`；环境都不含密钥。
- 本机填字命令不含 `--mt`；阿里云填字命令含 `--mt`。图片三阶段命令顺序与工作目录正确。
- JSONL 解析覆盖 progress / done / error / 非 JSON 行。
- 汉化底栏进度对标视频：填字 3/9 在 10 分钟时应显示已用时、剩余约 20 分钟、3/9 句；百分比只在条旁；抽字用「页」；阶段尚无总数时不估剩余；结束行保留已用时。
- 汉化 Running 时不能开始视频、不能开始第二份汉化、不能发送聊天；视频 Running 时不能开始汉化。
- 本机填字需要 Qwen；阿里云填字与游戏翻字不把 Qwen 列入启动条件。OCR/嵌字无论引擎都要求停 Qwen。
- 启动计划：汉化中断不得改变 text / video / none。

Python（内嵌汉化现有离线测试增补，原有项保持）：

- 带 `--progress-jsonl` 时输出可解析；缺省时云端 bat 仍无该旗标、仍无 `--local`。本地游戏 bat 仍有 `--local`，仍无 `--progress-jsonl`。
- `fill_ocr_local.py` 无旗标时仍是本机；`--mt` 走云端分流。点我用本地Qwen翻已抽出的图字.bat 不含 `--mt`。
- 新抽字入口离线：缺目录返回非 0，不调用网络。
- 云端与本地 payload 分流原断言不变。

WinUI：

- 基线脚本：ModeSelector 仍左对齐、仍在工具栏第一列；第三项为「汉化」；聊天和视频宿主结构不降级。
- 部署仍走 `deploy-winui.ps1`。源码 DLL 与 AppX DLL 哈希必须一致。真实窗口：三种模式切换外框不跳；未开汉化时聊天发送与视频生成与改前一致。

## WinUI 基线变更

旧关系：模式工具栏为「聊天 / 视频」两项。

新关系：同一左对齐 SelectorBar 为「聊天 / 视频 / 汉化」。模式专属工具仍在同一行剩余区域。汉化页复用「主区 / 输入卡 / 64 高底栏」节奏，输入卡同样是左侧标题、右侧精简元数据、主按钮在正文行最右。颜色、柔光、圆角不在本需求授权范围内。

设置抽屉仍是文本/视频两栏。汉化路径放在文本分区「本地服务」卡内，短标题加一行小灰说明（即时生效）。

## 发布

改 WinUI 后必须运行 `deploy-winui.ps1`，不能只用 `dotnet build` 从桌面快捷方式验收。正在运行的 Local AI 不会加载新 DLL，验收前要退出客户端。
