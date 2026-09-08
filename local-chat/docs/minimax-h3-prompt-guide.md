# MiniMax H3 提示词使用手册

面向 Local AI 视频页。规则来自官方开源提示词指南，不是社区口口相传的格式。

本地没有官方的 **H3-Context-IR**（把口语扩写成标准结构的云端预处理）。云端 API 会先改写提示词；本机要把结构自己写全。

配套能力说明见 [`minimax-h3-capabilities.md`](minimax-h3-capabilities.md)。

## 1. 先选对模式

| Local AI 模式 | 官方任务 | 图/音视频在提示词里的角色 | 提示词主干字段 |
|---|---|---|---|
| 文生视频 | T2VA | 没有参考素材 | `integrated_multimodal_description` |
| 首帧图生视频 | I2VA | `<Picture 1>` = 第 0 秒的真实首帧 | 先写首帧对齐句，再写上面三个字段 |
| 首帧 + 尾帧 | FL2VA | Comfy 固定图 1 = 开头，图 2 = 结尾 | 先写首尾对齐句，正文写中间过程 |
| 尾帧图生视频 | L2VA | Comfy 把唯一尾帧标成 `<Picture 1>` | 先写尾帧对齐句，正文写如何落到这张图 |
| 参考图 | Ref2VA | `<Picture N>` 只提供长相/场景/姿势等，**默认不是首帧** | `subject_definitions` + `detailed_description` |
| 参考视频 | Ref2VA | `<Video N>` 提供动作、运镜、剪辑节奏 | 同上 |
| 参考音频 | Ref2VA | `<Audio N>` 提供音色、节奏或环境声 | 同上 |

不要把「参考图」当成「首帧」。首帧模式会把图拉伸成开场画面；参考图模式是抽特征，画面可以是全新构图。

## 2. 语言

官方要求：

- **画面、镜头、参考关系、环境声、配乐：用英文写。**
- **对白、歌词**：保留原文，放进 `<d>[中文] ……</d>` 或 `<d>[English] ……</d>`。
- **画面里真实出现的字**（招牌、字幕、霓虹）：原文加英文双引号，例如 `"营业中"`。

中文提示词本地能出片，但参考身份、站位、动作更容易糊。先中文打草稿、再改成英文结构再提交。

## 3. 标签怎么对号

客户端 **图 1 / 视频 1 / 音频 1** 对应提示词里的 1-based 标签：

| 你上传的顺序 | 提示词标签 |
|---|---|
| 图 1、图 2、图 3 | `<Picture 1>` `<Picture 2>` `<Picture 3>` |
| 参考视频 1 | `<Video 1>` |
| 参考音频 1 | `<Audio 1>` |

ComfyUI 节点的呈现顺序是：先全部参考图，再参考视频（若带原声会先出现对应 `<Audio>`），最后是独立参考音频。标签按类型分别从 1 编号，不要把「第 2 个文件」写成 `<Picture 2>` 如果它其实是视频。

给每张参考图**只分配一个任务**，并写清忽略什么：

```text
<Picture 1> is the character appearance reference. Keep this face, body, and clothes.
<Picture 2> is the pose and position reference only. Copy blocking and action, not identity.
```

若图 2 里也有另一张脸，不写 “not the identity”，模型很容易换脸。

## 4. 文生 / 首帧 / 首尾帧：三个核心字段

除了首帧、首尾帧开头那句对齐指令，正文固定三段：

```text
integrated_multimodal_description: [Shot 1] ...

overall_soundscape: ...

non_diegetic_music: ...
```

| 字段 | 写什么 | 不要写什么 |
|---|---|---|
| `integrated_multimodal_description` | 风格、构图、人物、动作、分镜、对白、画内声 | 抽象情绪词堆砌 |
| `overall_soundscape` | 环境声、动作声、呼吸笑声；1–4 句英文 | 对白、歌声、画内音乐（那些放第一段） |
| `non_diegetic_music` | 观众才听得到的配乐：乐器、速度、强弱 | 抽象心情；角色能听见的歌要放第一段。没有就写 `N/A` |

### 4.1 首帧对齐句（I2VA，Local AI「首帧图生视频」）

必须放在提示词第一行，后面空一行：

```text
For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.
```

`<Picture 1>` 就是视频第 0 秒的真实首帧。先钉住图里的人、构图、场景，再写接下来怎么动。推荐节奏：**首帧锚点 → 动作开始 → 连续发展 → 结果**。

### 4.2 首尾帧对齐句（FL2VA）

```text
How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with the 0.00-second mark of the target video; Picture 2 (from Shot 1) aligns with the 8.00-second mark of the target video.
```

`8.00` 必须是这次的片长，两位小数。Local AI 提交时会按「本次时长」写入这句，不要手写“end of the target video”。正文写**中间过程**，不要把两张静帧再描述一遍。官方更建议单镜头连续过渡。推荐节奏：**首帧状态 → 可见的中间变化 → 差距收窄 → 尾帧状态**。

ComfyUI `MiniMaxH3ImageToVideo` 会在提示词前注入 `<Picture 1>:`（首帧）和 `<Picture 2>:`（尾帧）。模板里的图号必须跟这个顺序一致，不要按素材条上的「图 2 当首帧」去改标签。

### 4.2.1 尾帧对齐句（L2VA，Local AI「尾帧图生视频」）

```text
How the reference pictures align with the target video — <Picture 1> (from [Shot 1]) aligns with the 6.00-second mark of the target video.
```

只接尾帧时，Comfy 把这张图标成 `<Picture 1>`。正文从合理前态写到落点，不要把尾帧当开场。

### 4.3 分镜

- `[Shot 1]` **不要**加时间码。
- 后续镜头必须写递增时间，且落在片长内：

```text
[Shot 2] At 00:03.500, the camera cuts to...
```

切镜用语：`the camera cuts to` / `the shot cuts to` / `the shot transitions to`。只是稍微推近或微调角度，优先写运镜，不要硬切。

### 4.4 运镜

写进句子里，不要在句尾堆标签。完整表达是 **类型 + 幅度 + 速度**；中等幅度和常速可省略。

| 类型 | 英文 |
|---|---|
| 变焦推/拉 | `Zoom In` / `Zoom Out` |
| 机位推进/拉远 | `Push In` / `Pull Out` |
| 原地左右摇 | `Pan Left` / `Pan Right` |
| 机位左右横移 | `Truck Left` / `Truck Right` |
| 原地上下摇 | `Tilt Up` / `Tilt Down` |
| 机位升降 | `Pedestal Up` / `Pedestal Down` |
| 环绕 | `Arc Shot` |
| 跟随 | `Tracking Shot` |
| 固定 | `Static Shot` |
| 轻/重晃 | `Shake Slightly` / `Shake Strongly` |
| 主观 | `POV` |
| 滚转 | `Roll Clockwise` / `Roll Counterclockwise` |

幅度：`with small amplitude` / `with large amplitude`。速度：`at slow speed` / `at fast speed`。

```text
The camera pushes in with small amplitude at slow speed toward the folded letter in her hands.
The camera holds a static shot as the runner exits the frame.
```

### 4.5 说话和唱歌

- 会出声的人给稳定编号 `(S1)` `(S2)`，全程不要换。
- 从不出声的角色不要编号。
- 身份、音色、语速写在 `<d>` **外面**；`<d>` 里面只有语言标签和原词，不翻译。

```text
The young woman with a quiet, breathy voice (S1) says: <d>[English] I get off at the next station.</d>
The two children (S1,S2) shout together, <d>[English] Wait for us!</d>
```

画外音必须用这句：`says in an off-screen voiceover`，并且马上写嘴唇闭合：

```text
The man (S1) says in an off-screen voiceover: <d>[English] I still remember that road.</d> while his lips remain completely closed.
```

对白跨镜头：两头用 `<scenetrans>`，并写明声音连续。被片长截断用 `<cutoff>`。

## 5. 参考图 / 参考视频 / 参考音频（Ref2VA）

Local AI 的「参考图」「参考视频」「参考音频」都走这一套。官方完整改写有六段，按这个顺序：

| 字段 | 作用 |
|---|---|
| `subject_definitions` | 定义每个标签是什么、从哪张素材来、跟什么 |
| `summary` | 一句任务类型 + 主要参考关系 |
| `retention_analysis` | 每个标签是完整保留、部分保留，还是只弱参考 |
| `detailed_description` | 按播放顺序写画面和声音；生成任务大约 350–500 英文词 |
| `overall_soundscape` | 环境声、动作声 |
| `non_diegetic_music` | 观众配乐；没有写 `N/A` |

`summary` 开头的任务类型（可组合，用 ` + `）：

| 标记 | 何时用 |
|---|---|
| `[reference generation]` | 图/视频/音频只提供长相、动作、风格、运镜，不当真实首帧 |
| `[keyframe completion]` | 某张图就是目标视频的首帧、关键帧或尾帧 |
| `[video editing]` | 直接改已有参考视频 |
| `[video continuation]` | 从参考视频结尾接着拍 |
| `[audio reuse]` | 原音频信号整段或局部拷贝 |
| `[audio reference]` | 只借音色、节奏、风格，不拷贝波形 |

### 5.1 四种标签

| 标签 | 用在 |
|---|---|
| `<Subject N>` | 真正要复用的可见内容：人、场景、服装、姿势、风格 |
| `<Picture N>` | 图本身当首帧/分镜锚点时才单独占一行；若只用来定义人物，写进 `<Subject>` 即可 |
| `<Video N>` | 整段视频关系：剪辑、续拍、运镜/剪辑节奏 |
| `<Audio N>` | 独立音频，或明确启用的参考视频声轨 |

一个人可以来自多张素材：

```text
<Subject 1> is the woman whose appearance comes from <Picture 1> and whose walking motion comes from <Video 1>.
```

`retention_analysis` 画面侧标记：`fully_preserved` / `partially_preserved` / `attribute_transfer` / `weak_reference`。  
音频侧：`fully_copy` / `partially_copy` / `reference` / `weak_reference`。

`detailed_description` 在 `[Shot 1]` 之前先用一两句英文定风格，再开第一镜。标签在第一次出场处写清外貌和位置，后面只复用标签，不要重定义。

### 5.2 本机可直接粘贴的短结构

没有 Context-IR 时，至少保留定义 + 正文两段。下面是「图 1 管长相、图 2 管站位和动作」：

```text
subject_definitions:
<Subject 1> is the person whose face, body, hair, and clothing come only from <Picture 1>.
<Subject 1> performs the pose and action shown in <Picture 2>.

summary:
[reference generation] Keep <Subject 1>'s appearance locked to <Picture 1>. Transfer only the pose, blocking, relative positions, and camera from <Picture 2>. Do not copy the face, body, or clothes from <Picture 2>. Do not animate <Picture 2> as-is.

retention_analysis:
<Subject 1> (appears in [Shot 1]): fully_preserved - face, body, hair, and outfit stay locked to <Picture 1>; pose, blocking, and camera are attribute_transfer from <Picture 2>.

detailed_description:
The target video uses a cinematic live-action style.
[Shot 1] <Subject 1> looks exactly like <Picture 1>.
The camera framing, character placement, and pose follow <Picture 2>.
<Subject 1> occupies the corresponding role in that layout. Keep <Picture 2>'s camera and relative positions. Do not keep the face or body identity of the person <Subject 1> is replacing.
Then the person continues that action naturally for the rest of the clip.

overall_soundscape: Quiet room tone and soft fabric movement.
non_diegetic_music: N/A
```

姿势、动作、运镜不要只挂在 `<Picture 2>` 上无人认领。外观用 `<Subject>` 锁定参考图；表演用另一句绑到同一个 `<Subject>`，或改由 `<Video N>` 提供：

```text
subject_definitions:
<Subject 1> is the person whose face, body, hair, and clothing come only from <Picture 1>.
<Video 1> provides the reference for the pose, action, and camera movement of <Subject 1>.
```

参考视频、参考音频同理：`<Video N>` 管动作和运镜，`<Audio N>` 管音色或节奏，不要把视频定义成一个新的人物 Subject。

## 6. 官方范例（可对照）

### 6.1 文生视频（T2VA）

```text
integrated_multimodal_description: [Shot 1] Live-action, cinematic, a medium-wide shot frames a baker opening the shutters of a small street bakery before sunrise. The camera pushes in with small amplitude at slow speed as the middle-aged baker with a calm, slightly raspy voice (S1) places a fresh loaf on the wooden counter and says: <d>[English] First batch of the morning.</d> [Shot 2] At 00:05.000, the camera cuts to a close-up of steam rising from the sliced bread while the baker's final words carry over from the previous shot.

overall_soundscape: Wooden shutters scrape open over a quiet street as trays clink softly inside the bakery. The doorbell rings once, followed by light footsteps and the crisp sound of bread being sliced.

non_diegetic_music: A soft acoustic-guitar pattern at a moderate tempo, joined by sparse upright-bass notes and a gentle fade at the end.
```

### 6.2 首帧（I2VA）

```text
For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.

integrated_multimodal_description: [Shot 1] Live-action, cinematic, the young woman shown in <Picture 1> remains beside the rain-covered train window, preserving her appearance, clothing, seat position, and the carriage layout. The camera trucks right with small amplitude at slow speed as she lifts her gaze from the folded letter toward the passing city lights. Her reflection moves across the glass while the quiet, breathy young woman (S1) says: <d>[English] I get off at the next station.</d> She folds the letter along its existing crease.

overall_soundscape: The train wheels produce a steady metallic rhythm beneath a low ventilation hum. Rain ticks against the window while paper rustles softly in her hands.

non_diegetic_music: Sustained cello notes at a slow tempo with widely spaced piano tones, gradually decreasing in volume.
```

### 6.3 首尾帧（FL2VA，8 秒单镜头）

```text
How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with the 0.00-second mark of the target video; Picture 2 (from Shot 1) aligns with the 8.00-second mark of the target video.

integrated_multimodal_description: [Shot 1] Live-action, cinematic, a rain-soaked cyclist begins in the position and framing established by Picture 1, holding a closed black umbrella beside a silver bicycle. The camera pulls out with small amplitude at slow speed as she releases the bicycle handle, raises the umbrella above her shoulder, and presses the runner upward until the canopy opens. Water rolls from the expanding fabric while she steps beneath it, rotates the handle into the final angle, and settles into the pose, spacing, and composition established by Picture 2 at the end of the shot.

overall_soundscape: Rain falls steadily on the pavement, followed by the metallic click of the umbrella runner and the soft snap of the canopy opening. Water drips from the bicycle frame as distant traffic passes.

non_diegetic_music: N/A
```

### 6.4 尾帧（L2VA，6 秒单镜头）

```text
How the reference pictures align with the target video — <Picture 1> (from [Shot 1]) aligns with the 6.00-second mark of the target video.

integrated_multimodal_description: [Shot 1] Live-action, cinematic, a close shot begins with an intact drinking glass near the edge of a dark wooden table, while the same hand and sleeve visible in <Picture 1> approach from the right. The camera pushes in with small amplitude at slow speed as the fingertips strike the rim. The glass tips, falls, and hits the floor with a sharp impact; cracks spread through it as fragments slide outward. Toward the end, the moving pieces lose momentum and settle into the exact broken arrangement, hand position, camera angle, lighting, and final composition established by <Picture 1>.

overall_soundscape: Fingertips tap the glass before it scrapes across the tabletop, falls, and breaks with a sharp crash. Small fragments scatter and gradually stop sliding across the floor.

non_diegetic_music: A low electronic pulse at a slow tempo, ending immediately after the glass breaks.
```

## 7. 本机提交前核对

1. 模式和标签一致：首帧 / 单独尾帧都是 `<Picture 1>`；首尾帧固定图 1 = 首帧、图 2 = 尾帧；参考图按上传顺序用 `<Picture N>`。
2. 每张参考图只承担一种职责，并写了「不要用它的什么」。
3. 主体用英文；对白在 `<d>` 里且未翻译。
4. `[Shot 1]` 无时间码；后续镜头时间递增且不超过本次时长。
5. 有环境声就写 `overall_soundscape`；没配乐写 `non_diegetic_music: N/A`。
6. 参考视频建议 2–15 秒、至少约 5 帧（约 0.2 秒）；更短会被节点拒绝。

## 来源

- [MiniMax-AI/MiniMax-H3 `h3-prompt-writing`](https://github.com/MiniMax-AI/MiniMax-H3/tree/main/skills/h3-prompt-writing)：`references/base-en.txt`、`references/ref-en.txt`
- [VIDEO_PROMPT_WRITING_GUIDE_base_en.md](https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/docs/VIDEO_PROMPT_WRITING_GUIDE_base_en.md)（T2VA / I2VA / FL2VA / L2VA）
- [VIDEO_PROMPT_WRITING_GUIDE_ref_en.md](https://huggingface.co/MiniMaxAI/MiniMax-H3/blob/main/docs/VIDEO_PROMPT_WRITING_GUIDE_ref_en.md)（Ref2VA）
- [Comfy-Org/ComfyUI `nodes_minimax_h3.py`](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy_extras/nodes_minimax_h3.py) 与 [`comfy/text_encoders/minimax.py`](https://github.com/Comfy-Org/ComfyUI/blob/master/comfy/text_encoders/minimax.py)（关键帧 / 参考标签注入顺序）
