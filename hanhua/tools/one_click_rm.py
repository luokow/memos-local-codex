# One-click RPG Maker MV/MZ embed: copy -> extract -> translate -> inject.
# Default translator is Aliyun qwen-mt-plus. Pass --local for Local AI chat Qwen.
# Does not open Translator++ / LinguaGacha / MTool. Never prints API keys.
from __future__ import annotations

import hashlib
import json
import os
import shutil
import sys
import traceback
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
PACK = TOOLS.parent
sys.path.insert(0, str(TOOLS))

import local_qwen  # noqa: E402
import rm_embed  # noqa: E402
import translate_direct  # noqa: E402

WIN_BAD = '<>:"/\\|?*'
MTOOL_MARKERS = (
    "ManualTransFile.json",
    "lastLoadedTrsFile",
    "与工具一同启动.bat",
    "StartWithTool.bat",
)
MTOOL_ALWAYS = (
    "ManualTransFile.json",
    "lastLoadedTrsFile",
    "与工具一同启动.bat",
    "StartWithTool.bat",
    "injectPath",
    "LocaleEmulator.dll",
    "LELoaderDll.dll",
    "MTool_Game.exe",
    "MTool_Config.exe",
)
MTOOL_DLL_IF_MARKED = ("winmm.dll", "version.dll")
COPY_IGNORE = {"Thumbs.db", "desktop.ini", "debug.log"}


def log(msg: str) -> None:
    local_qwen.log(msg)


def win_legal_name(name: str) -> str:
    cleaned = "".join("_" if c in WIN_BAD else c for c in name).strip(" .")
    return cleaned[:80] or "game"


def pick_folder() -> Path | None:
    try:
        import tkinter as tk
        from tkinter import filedialog
    except ImportError:
        return None
    root = tk.Tk()
    root.withdraw()
    try:
        root.attributes("-topmost", True)
    except tk.TclError:
        pass
    chosen = filedialog.askdirectory(title="选择要汉化的游戏文件夹（有 Game.exe 的那一层）")
    root.destroy()
    return Path(chosen) if chosen else None


def detect_other_engine(root: Path) -> str | None:
    if (root / "UnityPlayer.dll").exists() or any(root.glob("*_Data")):
        return "Unity"
    if (root / "renpy").is_dir() or ((root / "game").is_dir() and list(root.glob("*.py"))):
        return "RenPy"
    if (root / "Game.dat").exists() or (root / "Data.wolf").exists() or (root / "data.wolf").exists():
        return "WOLF RPG Editor"
    return None


def find_game_root(path: Path) -> Path | None:
    path = path.expanduser()
    if path.is_file():
        path = path.parent
    if not path.is_dir():
        return None
    path = path.resolve()
    if rm_embed.find_data_dir(path) is not None:
        return path
    hits: list[Path] = []
    try:
        children = list(path.iterdir())
    except OSError:
        return None
    for child in children:
        if child.is_dir() and rm_embed.find_data_dir(child) is not None:
            hits.append(child)
        if len(hits) > 2:
            break
    if len(hits) == 1:
        return hits[0]
    return None


def dest_folder(src_root: Path) -> Path:
    name = win_legal_name(src_root.name)
    if name.lower().endswith("-cn"):
        dest = PACK / name
    else:
        dest = PACK / (name + "-cn")
    if src_root.resolve() == dest.resolve():
        return dest
    if dest.exists():
        marker = dest / "来源.txt"
        if marker.is_file():
            prev = marker.read_text(encoding="utf-8", errors="replace").strip()
            if prev and Path(prev).exists() and Path(prev).resolve() != src_root.resolve():
                digest = hashlib.md5(str(src_root.resolve()).encode("utf-8")).hexdigest()[:6]
                dest = PACK / (name + "-cn-" + digest)
    return dest


def ignore_copy(_dir: str, names: list[str]) -> list[str]:
    return [n for n in names if n in COPY_IGNORE]


def copy_game(src: Path, dest: Path) -> None:
    if dest.exists():
        log(f"中文副本已存在，接着往下做（不覆盖）: {dest}")
        return
    log(f"正在复制游戏到: {dest}")
    log("文件多的话会等一会儿，不要关窗口。")
    shutil.copytree(src, dest, ignore=ignore_copy)
    log("复制完成。")


def strip_mtool(dest: Path) -> None:
    marked = any((dest / name).exists() for name in MTOOL_MARKERS)
    names = list(MTOOL_ALWAYS)
    if marked:
        names.extend(MTOOL_DLL_IF_MARKED)
    removed = []
    for name in names:
        path = dest / name
        if path.is_file():
            try:
                path.unlink()
                removed.append(name)
            except OSError as exc:
                log(f"没法删 {name}: {exc}")
    if removed:
        log("已去掉 MTool 浮窗残留: " + ", ".join(removed))


def write_open_bat(dest: Path) -> None:
    text = (
        "@echo off\r\n"
        'cd /d "%~dp0"\r\n'
        'if exist "Game.exe" (\r\n'
        '  start "" "%~dp0Game.exe"\r\n'
        "  goto :eof\r\n"
        ")\r\n"
        "echo Game.exe not found\r\n"
        "pause\r\n"
    )
    (dest / "点我打开中文版.bat").write_bytes(text.encode("ascii"))


def merge_old_trans(items: dict[str, str], trans_path: Path) -> int:
    if not trans_path.is_file():
        return 0
    try:
        old = json.loads(trans_path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError:
        return 0
    if not isinstance(old, dict):
        return 0
    reused = 0
    for key, val in old.items():
        if key in items and val and val != key and not items[key]:
            items[key] = val
            reused += 1
    return reused


def translate_one(src_arg: Path, *, dry_run: bool, engine: str = "mt") -> int:
    src_arg = src_arg.expanduser()
    if not src_arg.exists():
        log(f"找不到路径: {src_arg}")
        return 1

    other = detect_other_engine(src_arg if src_arg.is_dir() else src_arg.parent)
    root = find_game_root(src_arg)
    if root is None:
        if other:
            log(f"这是 {other} 游戏。现在一键只做 RPG Maker MV/MZ，这类以后再接。")
        else:
            log("没认出 RPG Maker MV/MZ（需要 Game.exe 旁边有 data\\System.json，或 www\\data\\System.json）。")
            log("把文件夹再往里进一层，选到真正有 Game.exe 的目录再试。")
        return 1

    data_dir = rm_embed.find_data_dir(root)
    assert data_dir is not None
    if rm_embed.data_looks_encrypted(data_dir):
        log("文本文件是加密的，一键抽不出来。这种要用 Translator++ 先解密。")
        return 1

    dest = dest_folder(root)
    work = PACK / "work" / win_legal_name(dest.name)
    trans_path = work / "ManualTransFile.json"

    extract_root = root
    require_kana = False
    listed_path = dest / "来源.txt"
    if dest.resolve() == root.resolve() and listed_path.is_file():
        listed = Path(listed_path.read_text(encoding="utf-8-sig", errors="replace").strip())
        if listed.exists() and rm_embed.find_data_dir(listed) is not None:
            extract_root = listed
    if dest.resolve() == root.resolve() and extract_root.resolve() == dest.resolve():
        require_kana = True

    extract_data = rm_embed.find_data_dir(extract_root)
    if extract_data is None:
        log("找不到用来抽文本的 data。")
        return 1

    log(f"原版: {extract_root}")
    log(f"中文副本: {dest}")
    log(f"data: {extract_data.relative_to(extract_root)}")
    if require_kana:
        log("在中文副本上接着做，只抽还带假名的日文。")

    if dry_run:
        items, _locations = rm_embed.extract_game(extract_data, require_kana=require_kana)
        log(f"预检：能抽出 {len(items)} 句日文。没有改任何文件。")
        return 0

    local_qwen.emit("phase", "copy", message="正在复制游戏")
    copy_game(root, dest)
    strip_mtool(dest)
    dest_data = rm_embed.find_data_dir(dest)
    if dest_data is None:
        log("复制之后找不到 data，停。")
        return 1

    work.mkdir(parents=True, exist_ok=True)
    (work / "source.txt").write_text(str(extract_root), encoding="utf-8")
    if dest.resolve() != extract_root.resolve():
        (dest / "来源.txt").write_text(str(extract_root), encoding="utf-8")

    log("正在抽文本…")
    local_qwen.emit("phase", "extract", message="正在抽文本")
    items, locations = rm_embed.extract_game(extract_data, require_kana=require_kana)
    reused = merge_old_trans(items, trans_path)
    if reused:
        log(f"沿用上次译文 {reused} 句。")
    trans_path.write_text(json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8")
    (work / "locations.json").write_text(
        json.dumps({"count": len(items), "locations": locations}, ensure_ascii=False),
        encoding="utf-8",
    )
    actors = dest_data / "Actors.json"
    if actors.exists():
        shutil.copy2(actors, work / "Actors.json")
    log(f"抽出 {len(items)} 句。")

    empty = sum(1 for k, v in items.items() if not v or v == k)
    if empty == 0:
        log("没有需要翻的句子（可能已经汉化过）。")
    else:
        if engine == "local":
            log("翻译走本地 Qwen 对话口 127.0.0.1:18135。先开 Local AI，不要同时开 MiniMax H3。")
        else:
            conf = translate_direct.CONF
            if not conf.is_file():
                log(f"找不到 API 配置: {conf}")
                log("一键翻译用的是现有那条阿里云线路，配置不在就没法翻。")
                return 1
        log(f"开始翻译 {empty} 句，可以去干别的，别关这个窗口。")
        local_qwen.emit("phase", "translate", done=0, total=empty, message=f"开始翻译 {empty} 句")
        translate_direct.translate_mapping(items, trans_path, engine=engine)

    log("正在写回游戏文件…")
    local_qwen.emit("phase", "inject", message="正在写回游戏文件")
    trans = json.loads(trans_path.read_text(encoding="utf-8-sig"))
    filled = rm_embed.inject_game(dest_data, trans)
    empty_after = sum(1 for k, v in trans.items() if not v or v == k)
    write_open_bat(dest)
    log(f"写回 {filled} 句。空着/被拦 {empty_after} 句。")
    local_qwen.emit(
        "done",
        "inject",
        done=filled,
        total=len(trans),
        empty=empty_after,
        output=str(dest),
        message=f"写回 {filled} 句",
    )
    log("")
    log("可以玩了。打开：")
    log(str(dest / "点我打开中文版.bat"))
    if empty_after:
        if engine == "local":
            log(f"还有 {empty_after} 句没翻。进游戏碰到日文再说。")
        else:
            log(f"还有 {empty_after} 句没翻（多半是阿里云内容审核拦了）。进游戏碰到日文再说。")
    return 0


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    dry_run = False
    engine = "mt"
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    local_qwen.enable_progress(jsonl)
    if "--dry-run" in argv:
        dry_run = True
        argv = [a for a in argv if a != "--dry-run"]
    if "--local" in argv:
        engine = "local"
        argv = [a for a in argv if a != "--local"]

    os.environ.setdefault("PYTHONUTF8", "1")

    if not argv:
        picked = pick_folder()
        if picked is None:
            log("用法: 把游戏文件夹拖到「点我翻译游戏.bat」或「点我用本地Qwen翻译游戏.bat」上，或双击后在窗口里选文件夹。")
            return 2
        argv = [str(picked)]

    rc = 0
    for raw in argv:
        log("=" * 60)
        try:
            one_rc = translate_one(Path(raw), dry_run=dry_run, engine=engine)
        except KeyboardInterrupt:
            log("手动停了。已经翻完写进 JSON 的部分还在，下次会接着翻。")
            return 130
        except Exception:
            traceback.print_exc()
            one_rc = 1
        rc = rc or one_rc
    return rc


if __name__ == "__main__":
    raise SystemExit(main())
