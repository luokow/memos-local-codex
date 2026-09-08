# Extract OCR strings with manga-image-translator. Does not call Qwen.
# Writes typeset_in/ and translations.json. Stop Local AI first (8GB GPU).
from __future__ import annotations

import json
import os
import shutil
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
    return local_qwen.copy_images(src, dest)


def load_translations(path: Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return {}
    if not isinstance(data, dict):
        return {}
    out: dict[str, str] = {}
    for key, val in data.items():
        if isinstance(key, str):
            out[key] = "" if val is None else str(val)
    return out


def merge_mapping(existing: dict[str, str], incoming: dict[str, str]) -> dict[str, str]:
    merged = dict(existing)
    for key, val in incoming.items():
        current = merged.get(key, "")
        if key not in merged:
            merged[key] = val
        elif (not current or current == key) and val and val != key:
            merged[key] = val
    return merged


def mark_empty_ocr(pages: list[Path], dest: Path) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    for img in pages:
        path = dest / f"{img.stem}_ocr.json"
        if not path.is_file():
            path.write_text("[]", encoding="utf-8")


def retry_missing_ocr(
    missing: list[Path],
    *,
    work: Path,
    mit_py: Path,
    font: Path,
    cfg: Path,
    mit_root: Path,
    dump: Path,
    env: dict[str, str],
) -> None:
    if not missing:
        return
    retry_in = work / "ocr_retry_in"
    retry_out = work / "ocr_retry_out"
    if retry_in.exists():
        shutil.rmtree(retry_in)
    if retry_out.exists():
        shutil.rmtree(retry_out)
    retry_in.mkdir(parents=True, exist_ok=True)
    retry_out.mkdir(parents=True, exist_ok=True)
    for img in missing:
        shutil.copy2(img, retry_in / img.name)
    cmd = mit_ocr_command(mit_py, font, cfg, retry_in, retry_out)
    print("ocr-retry", retry_in, "->", retry_out, "pages", len(missing))
    local_qwen.run_mit_with_page_progress(
        cmd,
        cwd=str(mit_root),
        env=env,
        phase="ocr",
        total=len(missing),
        count=lambda: local_qwen.completed_image_pages(
            missing, ocr_dirs=[retry_in, retry_out], output_dirs=[retry_out]
        ),
    )
    for folder in (retry_in, retry_out):
        for ocr in folder.glob("*_ocr.json"):
            shutil.copy2(ocr, dump / ocr.name)


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
    return "抽字完成，但没有识别到文字。请换含对白的图片目录。"


def main() -> int:
    argv = list(sys.argv[1:])
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    argv, missing_only = local_qwen.take_flag(argv, "--missing-only")
    local_qwen.enable_progress(jsonl)
    if len(argv) < 2:
        print(
            "usage: ocr_extract_local.py [--progress-jsonl] [--missing-only] <image-dir> <work-dir>",
            file=sys.stderr,
        )
        return 2
    src = Path(argv[0])
    work = Path(argv[1])
    if not src.is_dir():
        print(f"not a directory: {src}", file=sys.stderr)
        return 1
    images = local_qwen.list_image_files(src)
    if not images:
        msg = "这个目录里没有图片（png/jpg/webp）。请选含图片的文件夹。"
        print(msg, file=sys.stderr)
        local_qwen.emit("error", "ocr", message=msg)
        return 1

    typeset_in = work / "typeset_in"
    copied = copy_pngs(src, typeset_in)
    trans = work / "translations.json"
    dump = work / "ocr_dump"
    dump.mkdir(parents=True, exist_ok=True)
    sidecar = work / "ocr_sidecars"
    ocr_dirs = [typeset_in, dump, sidecar]
    missing = local_qwen.missing_ocr_pages(copied, ocr_dirs)
    local_qwen.emit(
        "phase", "ocr", done=0, total=len(missing) if missing_only else len(copied),
        message=f"抽字 {len(missing) if missing_only else len(copied)} 张",
    )
    if missing_only and not missing:
        print("ocr missing-only: no missing pages")
        local_qwen.emit(
            "done", "ocr", done=0, total=0, output=str(work), message="没有缺页"
        )
        return 0

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

    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    rc = 0
    if missing_only:
        retry_missing_ocr(
            missing,
            work=work,
            mit_py=mit_py,
            font=font,
            cfg=cfg,
            mit_root=mit_root,
            dump=dump,
            env=env,
        )
    else:
        cmd = mit_ocr_command(mit_py, font, cfg, typeset_in, dump)
        print("ocr", typeset_in, "->", dump)
        rc = local_qwen.run_mit_with_page_progress(
            cmd,
            cwd=str(mit_root),
            env=env,
            phase="ocr",
            total=len(copied),
            count=lambda: local_qwen.completed_image_pages(
                copied, ocr_dirs=[typeset_in, dump], output_dirs=[dump]
            ),
        )
        print(f"mit_exit={rc}")
        missing = local_qwen.missing_ocr_pages(copied, [typeset_in, dump])
        if missing:
            retry_missing_ocr(
                missing,
                work=work,
                mit_py=mit_py,
                font=font,
                cfg=cfg,
                mit_root=mit_root,
                dump=dump,
                env=env,
            )

    still = local_qwen.missing_ocr_pages(copied, [typeset_in, dump, sidecar])
    if still:
        preview = ", ".join(p.stem for p in still[:8])
        extra = f" 等 {len(still)} 页" if len(still) > 8 else ""
        local_qwen.log(f"抽字后仍无文字 {len(still)} 页: {preview}{extra}")
        mark_empty_ocr(still, dump)

    mapping = collect_mapping(typeset_in)
    mapping.update({k: v for k, v in collect_mapping(dump).items() if k not in mapping})
    mapping.update({k: v for k, v in collect_mapping(sidecar).items() if k not in mapping})
    local_qwen.isolate_image_folder(typeset_in, sidecar)
    if missing_only:
        mapping = merge_mapping(load_translations(trans), mapping)
    failed = ocr_failed_message(rc, len(mapping))
    if failed:
        local_qwen.log(failed)
        local_qwen.emit("error", "ocr", message=failed)
        return 1

    work.mkdir(parents=True, exist_ok=True)
    trans.write_text(json.dumps(mapping, ensure_ascii=False, indent=2), encoding="utf-8")
    still_missing = local_qwen.missing_ocr_pages(copied, [typeset_in, dump, sidecar])
    print(f"ocr strings={len(mapping)} missing_pages={len(still_missing)} -> {trans}")
    local_qwen.emit(
        "done",
        "ocr",
        done=len(copied),
        total=len(copied),
        output=str(work),
        empty=len(mapping),
        message=(
            f"抽出 {len(mapping)} 句"
            if not still_missing
            else f"抽出 {len(mapping)} 句，{len(still_missing)} 页无字"
        ),
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
