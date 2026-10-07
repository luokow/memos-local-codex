# 汉化工具包

Local AI「汉化」页调度的 Python 脚本和一键 bat。WinUI 客户端只拼命令，不自己翻句。

本机正在用的目录仍是 `D:\grok\内嵌汉化`。这里入库是为了记下脚本改动；游戏资源、Translator++、LinguaGacha、manga-image-translator 不进仓库。

## 会做什么

- RPG Maker MV/MZ：复制一份 `*-cn`，抽日文，翻成简体，写回副本。
- 本机翻句：RPG 的本地 bat 仍打 `127.0.0.1:18135` 的 chat Qwen。图片和 Unity 填字走 Sakura（`sakura-galtransl-7b-v3-7`）。Local AI 会把 `HANHUA_LOCAL_PORT` 设成填字档；没设时客户端仍落到 18135。一批 8 句。
- 阿里云翻句：读本机 MTool 配置，不把密钥写进这个仓库。
- 图片：检测和嵌字走 manga-image-translator 的 Lama。填字只填已经抽出的字。
- Unity：XUAT 显示，Addressable 抽字后用 Sakura 预填。插件 zip 缺了再下载，不进这个仓库。游戏词库不进这个仓库。

## 一键入口

| 文件 | 用途 |
| --- | --- |
| `点我翻译游戏.bat` | 阿里云 qwen-mt |
| `点我用本地Qwen翻译游戏.bat` | 本机 18135，先开 Local AI |
| `点我测试本地Qwen.bat` | `こんにちは` 探活 |
| `点我用本地Qwen翻已抽出的图字.bat` | 只填 OCR 字 |
| `点我把已填图字嵌回去.bat` | 嵌字，先关 Local AI |
| `点我把自配切到本地Qwen.bat` / `点我把自配切回阿里云.bat` | 只改 MTool 自配两字段 |
| `点我启动Qwen适配.bat` | LinguaGacha 走阿里云 MT 时才需要的 18765 适配 |
| `点我汉化Unity游戏.bat` | 装 XUAT、抽字、Sakura 预填。先开 Local AI |
| `点我填Unity已抽出的词库.bat` | 只补填已抽出的 Unity 词库 |

把游戏文件夹拖到对应 bat 上。原目录不动。

## 离线测试

```powershell
cd hanhua\tools
python test_local_qwen.py
python -m unittest test_unity_xuat.py
```
