# Typeset already-filled translations.json with manga-image-translator (translator=none).
# Does not call Qwen. Stop Local AI first so Lama can use the 8GB GPU.
from __future__ import annotations

import json
import os
import shutil
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
        if d.is_dir() and local_qwen.list_image_files(d):
            return d
    raise RuntimeError(f"no typeset input image in {root}")


def ocr_texts(path: Path) -> set[str]:
    try:
        parsed = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return set()
    rows = parsed if isinstance(parsed, list) else [parsed]
    texts: set[str] = set()
    for row in rows:
        if isinstance(row, dict) and row.get("text"):
            texts.add(str(row["text"]))
        elif isinstance(row, str) and row:
            texts.add(row)
    return texts


def load_changed_keys(root: Path) -> set[str]:
    path = root / "filled_keys.json"
    if not path.is_file():
        return set()
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return set()
    if isinstance(data, list):
        return {str(item) for item in data}
    return set()


def select_changed_images(root: Path, keys: set[str]) -> list[Path]:
    src = find_input(root)
    out = root / "out"
    sidecar_dirs = (root / "ocr_sidecars", root / "ocr_dump", src)
    selected: list[Path] = []
    for img in local_qwen.list_image_files(src):
        if not local_qwen.output_image_exists(out, img.stem):
            selected.append(img)
            continue
        texts: set[str] = set()
        for folder in sidecar_dirs:
            ocr = folder / f"{img.stem}_ocr.json"
            if ocr.is_file():
                texts.update(ocr_texts(ocr))
        repaired = {local_qwen.repair_ocr_bang(text) for text in texts}
        if texts & keys or repaired & keys:
            selected.append(img)
    return selected


def stage_changed_input(root: Path, images: list[Path]) -> Path:
    staged = root / "typeset_changed"
    if staged.exists():
        shutil.rmtree(staged)
    staged.mkdir(parents=True, exist_ok=True)
    for img in images:
        shutil.copy2(img, staged / img.name)
    return staged


def main() -> int:
    argv = list(sys.argv[1:])
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    argv, changed_only = local_qwen.take_flag(argv, "--changed-only")
    local_qwen.enable_progress(jsonl)
    if len(argv) < 1:
        print(
            "usage: typeset_ocr_local.py [--progress-jsonl] [--changed-only] <work-folder>",
            file=sys.stderr,
        )
        return 2
    root = Path(argv[0]).resolve()
    trans = root / "translations.json"
    if not trans.is_file():
        print(f"missing {trans}", file=sys.stderr)
        return 1
    src = find_input(root)
    if changed_only:
        selected = select_changed_images(root, load_changed_keys(root))
        if not selected:
            dest = root / "out"
            dest.mkdir(parents=True, exist_ok=True)
            print("typeset changed-only: no changed pages")
            local_qwen.emit("done", "typeset", output=str(dest), message="没有改动页")
            return 0
        src = stage_changed_input(root, selected)
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
    images = local_qwen.list_image_files(src)
    local_qwen.emit(
        "phase", "typeset", done=0, total=len(images), message=f"{src} -> {dest}"
    )
    rc = local_qwen.run_mit_with_page_progress(
        cmd,
        cwd=str(mit_root),
        env=env,
        phase="typeset",
        total=len(images),
        count=lambda: local_qwen.completed_image_pages(images, output_dirs=[dest]),
    )
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
