# Typeset already-filled translations.json with manga-image-translator (translator=none).
# Does not call Qwen. Stop Local AI first so Lama can use the 8GB GPU.
from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import local_qwen

CONFIG_CANDIDATES = (
    Path(r"D:\grok\c108_work\config.json"),
    Path(r"D:\grok\hana_work\config.json"),
)


def mit_paths() -> tuple[Path, Path, Path]:
    root = local_qwen.mit_root()
    return root, root / ".venv" / "Scripts" / "python.exe", root / "fonts" / "msyh.ttc"


def find_input(root: Path) -> Path:
    for name in ("typeset_in", "in", "src_rgb"):
        d = root / name
        if d.is_dir() and any(d.glob("*.png")):
            return d
    raise RuntimeError(f"no typeset input png in {root}")


def main() -> int:
    argv = list(sys.argv[1:])
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    local_qwen.enable_progress(jsonl)
    if len(argv) < 1:
        print("usage: typeset_ocr_local.py [--progress-jsonl] <work-folder>", file=sys.stderr)
        return 2
    root = Path(argv[0]).resolve()
    trans = root / "translations.json"
    if not trans.is_file():
        print(f"missing {trans}", file=sys.stderr)
        return 1
    src = find_input(root)
    local_qwen.isolate_image_folder(src, root / "ocr_sidecars")
    dest = root / "out"
    dest.mkdir(parents=True, exist_ok=True)
    cfg = next((p for p in CONFIG_CANDIDATES if p.is_file()), None)
    if cfg is None:
        print("missing manga-image-translator config.json", file=sys.stderr)
        return 1
    ok, detail = local_qwen.probe()
    if ok:
        print("Local Qwen is still on 18135. Stop Local AI / llama-server first (8GB GPU).")
        print(detail)
        return 3
    mit_root, mit_py, font = mit_paths()
    if not mit_py.is_file():
        print(f"missing {mit_py}", file=sys.stderr)
        return 1
    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    env["MIT_TRANSLATIONS_JSON"] = str(trans)
    cmd = [
        str(mit_py),
        "-m",
        "manga_translator",
        "local",
        "--use-gpu",
        "--overwrite",
        "--ignore-errors",
        "--font-path",
        str(font),
        "--config-file",
        str(cfg),
        "-i",
        str(src),
        "-o",
        str(dest),
    ]
    print("typeset", src, "->", dest)
    print("lookup", trans)
    local_qwen.emit("phase", "typeset", message=f"{src} -> {dest}")
    rc = subprocess.call(cmd, cwd=str(mit_root), env=env)
    print(f"mit_exit={rc}")
    if rc == 0:
        local_qwen.emit("done", "typeset", output=str(dest), message="嵌字完成")
        return 0
    message = (
        "嵌字工具在保存后提前退出。"
        if rc in {-1, 4294967295}
        else f"嵌字工具异常退出（代码 {rc}）。"
    )
    local_qwen.log(message)
    local_qwen.emit("error", "typeset", message=message)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
