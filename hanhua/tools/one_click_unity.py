# One-click Unity display-layer 汉化: install XUAT, dump Addressable MB/TextAsset,
# hygiene, GalTransl fill. Drag a game folder. Does not copy the whole game.
# Local AI 汉化页调度本脚本；C# 不自己翻句。
# Default = install + fill (like image 开始汉化). --install-only skips fill.
from __future__ import annotations

import os
import sys
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOLS))

import local_qwen  # noqa: E402
import unity_xuat  # noqa: E402


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
    chosen = filedialog.askdirectory(title="选择 Unity 游戏文件夹（有 exe 和 UnityPlayer.dll 的那一层）")
    root.destroy()
    return Path(chosen) if chosen else None


def _translate_once(
    batch: list[str], model: str, system: str, glossary: str = ""
) -> list[str]:
    return local_qwen.translate_texts(
        batch,
        system=system,
        model=model,
        glossary=glossary,
    )


def make_translate(root: Path):
    folder = unity_xuat.text_dir(root)

    def translate_batch(batch: list[str]) -> list[str]:
        glossary = unity_xuat.fill_glossary_text(folder, srcs=batch)
        model = os.environ.get("HANHUA_LOCAL_MODEL") or local_qwen.HANHUA_FILL_MODEL
        try:
            return _translate_once(batch, model, local_qwen.GALTRANSL_SYSTEM, glossary)
        except Exception as exc:  # noqa: BLE001
            unity_xuat.log(
                f"GalTransl batch fail, retry one-by-one: {type(exc).__name__}: {exc}"
            )
            out: list[str] = []
            for src in batch:
                try:
                    got = _translate_once(
                        [src],
                        model,
                        local_qwen.GALTRANSL_SYSTEM,
                        unity_xuat.fill_glossary_text(folder, srcs=[src]),
                    )
                    out.append(got[0] if got else "")
                except Exception as inner:  # noqa: BLE001
                    unity_xuat.log(
                        f"GalTransl one fail, skip: {type(inner).__name__}: {inner}"
                    )
                    out.append("")
            return out

    return translate_batch


def run_fill(root: Path) -> int:
    dumped = unity_xuat.dump_addressable_jp(root)
    unity_xuat.log(f"预抽新增 {dumped} 句")
    ingested = unity_xuat.ingest_unmatched_log(root)
    unity_xuat.log(f"unmatched 收进 {ingested} 句")
    hooks = unity_xuat.diagnose_hooks(root)
    if not hooks.ok:
        unity_xuat.log(hooks.message)
        local_qwen.emit("error", "translate", message=hooks.message)
        return 1
    pending = unity_xuat.pending_count(root)
    if pending == 0:
        unity_xuat.log("没有新句子可填。预抽已跑过；漏句再进游戏走一遍。")
        local_qwen.emit(
            "done",
            "translate",
            done=0,
            total=0,
            output=str(root),
            message="没有新句子可填。预抽已跑过；漏句再进游戏走一遍。",
        )
        return 0
    ok, detail = local_qwen.probe()
    if not ok:
        unity_xuat.log("本机模型没开: " + detail)
        local_qwen.emit("error", "translate", message="本机模型没开，填字无法继续。不用点右上角启动。")
        return 2
    unity_xuat.log(detail)
    local_qwen.emit("phase", "translate", done=0, total=pending, message=f"开始预填 {pending} 句")

    def on_progress(done: int, total: int) -> None:
        local_qwen.emit("progress", "translate", done=done, total=total, message=f"{done}/{total}")

    filled, total = unity_xuat.fill_dump(
        root, make_translate(root), on_progress=on_progress
    )
    unity_xuat.log(f"预填完成 filled={filled} pending={total}")
    local_qwen.emit(
        "done",
        "translate",
        done=filled,
        total=total,
        output=str(root),
        message=f"已填 {filled} 句。关掉模型后用点我启动汉化版.bat 重开。漏句再进游戏走一遍。",
    )
    return 0


def run_install(root: Path, *, emit_done: bool = True) -> int:
    local_qwen.emit("phase", "copy", message="正在安装翻译插件")
    try:
        info = unity_xuat.setup_game(root)
    except (RuntimeError, FileNotFoundError) as exc:
        unity_xuat.log(str(exc))
        local_qwen.emit("error", "copy", message=str(exc))
        return 1
    unity_xuat.log(
        f"backend={info['backend']} exe={info['exe']} "
        f"identities_removed={info['identities_removed']}"
    )
    unity_xuat.log("启动: 游戏目录里的 点我启动汉化版.bat")
    unity_xuat.log("词库: BepInEx\\Translation\\zh-CN\\Text\\")
    hooks = unity_xuat.diagnose_hooks(root)
    if hooks.exists and not hooks.ok:
        unity_xuat.log(hooks.message)
        if emit_done:
            local_qwen.emit(
                "done",
                "copy",
                output=str(root),
                message="插件已装，但上次启动钩子没挂上。" + hooks.message,
            )
        return 0
    if emit_done:
        local_qwen.emit(
            "done",
            "copy",
            output=str(root),
            message="插件已装，抽字已写入词库。",
        )
    else:
        local_qwen.emit(
            "phase",
            "copy",
            output=str(root),
            message="插件已装，接着预填抽出的句子。",
        )
    return 0


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    local_qwen.enable_progress(jsonl)
    argv, _fill_flag = local_qwen.take_flag(argv, "--fill")
    argv, install_only = local_qwen.take_flag(argv, "--install-only")
    argv = [a for a in argv if a != "--local"]
    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("NO_PROXY", "127.0.0.1,localhost")
    do_fill = not install_only

    src = Path(argv[0]) if argv else pick_folder()
    if src is None:
        unity_xuat.log("没有选择文件夹。")
        return 1
    root = unity_xuat.find_unity_root(src)
    if root is None:
        unity_xuat.log(f"没认出 Unity 游戏: {src}")
        unity_xuat.log("选到有 UnityPlayer.dll 或 GameAssembly.dll、旁边有 *_Data 的那一层。")
        return 1
    unity_xuat.log(f"游戏目录: {root}")
    install_rc = run_install(root, emit_done=not do_fill)
    if install_rc != 0:
        return install_rc
    if not do_fill:
        return 0
    return run_fill(root)


if __name__ == "__main__":
    raise SystemExit(main())
