# Extract OCR strings with manga-image-translator. Does not call Qwen.
# Writes typeset_in/ and translations.json. Stop Local AI first (8GB GPU).
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import local_qwen
import typeset_ocr_local

SKIP = {"", "trans", "text"}
# manga-image-translator --save-text calls exit(-1) after the first page.
SAVE_TEXT_QUIT_CODES = {-1, 4294967295}


def collect_mapping(root: Path) -> dict[str, str]:
    mapping: dict[str, str] = {}
    for ocr in sorted(root.rglob("*_ocr.json")):
        try:
            parsed = json.loads(ocr.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            continue
        rows = parsed if isinstance(parsed, list) else []
        if isinstance(parsed, dict):
            rows = [parsed]
        for row in rows:
            if isinstance(row, dict) and row.get("text"):
                src = str(row["text"])
                if src not in SKIP:
                    mapping.setdefault(src, "")
            elif isinstance(row, str) and row not in SKIP:
                mapping.setdefault(row, "")
    return mapping


def copy_pngs(src: Path, dest: Path) -> list[Path]:
    dest.mkdir(parents=True, exist_ok=True)
    copied: list[Path] = []
    for png in sorted(p for p in src.iterdir() if p.is_file() and p.suffix.lower() == ".png"):
        target = dest / png.name
        shutil.copy2(png, target)
        copied.append(target)
    return copied


def mit_ocr_command(
    mit_py: Path, font: Path, cfg: Path, typeset_in: Path, dump: Path
) -> list[str]:
    return [
        str(mit_py),
        "-m",
        "manga_translator",
        "local",
        "--use-gpu",
        "--overwrite",
        "--ignore-errors",
        "--prep-manual",
        "--font-path",
        str(font),
        "--config-file",
        str(cfg),
        "-i",
        str(typeset_in),
        "-o",
        str(dump),
    ]


def is_save_text_quit(rc: int) -> bool:
    return rc in SAVE_TEXT_QUIT_CODES


def ocr_failed_message(rc: int, n_strings: int) -> str:
    if n_strings > 0:
        return ""
    if is_save_text_quit(rc):
        return "抽字工具在保存文字后提前退出，没有处理完全部页。"
    if rc != 0:
        return f"抽字工具异常退出（代码 {rc}）。"
    return "抽字完成，但没有识别到文字。请换含对白的 png 目录。"


def main() -> int:
    argv = list(sys.argv[1:])
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    local_qwen.enable_progress(jsonl)
    if len(argv) < 2:
        print(
            "usage: ocr_extract_local.py [--progress-jsonl] <png-dir> <work-dir>",
            file=sys.stderr,
        )
        return 2
    src = Path(argv[0])
    work = Path(argv[1])
    if not src.is_dir():
        print(f"not a directory: {src}", file=sys.stderr)
        return 1
    pngs = [p for p in src.iterdir() if p.is_file() and p.suffix.lower() == ".png"]
    if not pngs:
        msg = "这个目录里没有 png。请选含图片的文件夹。"
        print(msg, file=sys.stderr)
        local_qwen.emit("error", "ocr", message=msg)
        return 1

    typeset_in = work / "typeset_in"
    copied = copy_pngs(src, typeset_in)
    trans = work / "translations.json"
    local_qwen.emit("phase", "ocr", done=0, total=len(copied), message=f"抽字 {len(copied)} 张")

    ok, detail = local_qwen.probe()
    if ok:
        msg = "抽字需要显卡，本机 Qwen 还没停干净。"
        print(msg, file=sys.stderr)
        print(detail, file=sys.stderr)
        local_qwen.emit("error", "ocr", message=msg)
        return 3

    cfg = next((p for p in typeset_ocr_local.CONFIG_CANDIDATES if p.is_file()), None)
    if cfg is None:
        print("missing manga-image-translator config.json", file=sys.stderr)
        return 1
    mit_root, mit_py, font = typeset_ocr_local.mit_paths()
    if not mit_py.is_file():
        print(f"missing {mit_py}", file=sys.stderr)
        return 1

    dump = work / "ocr_dump"
    dump.mkdir(parents=True, exist_ok=True)
    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    cmd = mit_ocr_command(mit_py, font, cfg, typeset_in, dump)
    print("ocr", typeset_in, "->", dump)
    rc = subprocess.call(cmd, cwd=str(mit_root), env=env)
    print(f"mit_exit={rc}")

    mapping = collect_mapping(typeset_in)
    mapping.update({k: v for k, v in collect_mapping(dump).items() if k not in mapping})
    local_qwen.isolate_image_folder(typeset_in, work / "ocr_sidecars")
    failed = ocr_failed_message(rc, len(mapping))
    if failed:
        local_qwen.log(failed)
        local_qwen.emit("error", "ocr", message=failed)
        return 1

    work.mkdir(parents=True, exist_ok=True)
    trans.write_text(json.dumps(mapping, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"ocr strings={len(mapping)} -> {trans}")
    local_qwen.emit(
        "done",
        "ocr",
        done=len(mapping),
        total=len(mapping),
        output=str(work),
        empty=len(mapping),
        message=f"抽出 {len(mapping)} 句",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
