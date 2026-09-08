# Fill already-extracted OCR strings.
# Default: Local AI chat Qwen. --mt: Aliyun qwen-mt via existing MTool config.
# Does not run detection / inpaint / typeset. Never prints API keys.
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import local_qwen
import translate_direct

SKIP = {"", "trans", "text"}


def load_mapping(path: Path) -> dict[str, str]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if isinstance(data, dict):
        out: dict[str, str] = {}
        for key, val in data.items():
            if isinstance(val, dict):
                for src, dst in val.items():
                    if isinstance(src, str):
                        out[src] = "" if dst is None else str(dst)
            elif isinstance(key, str):
                out[key] = "" if val is None else str(val)
        return out
    if isinstance(data, list):
        out = {}
        for row in data:
            if isinstance(row, dict) and row.get("text"):
                src = str(row["text"])
                dst = row.get("trans") or ""
                out[src] = str(dst)
            elif isinstance(row, str):
                out[row] = ""
        return out
    raise RuntimeError("unsupported JSON shape")


def collect_from_dir(root: Path) -> tuple[dict[str, str], Path]:
    trans_path = root / "translations.json"
    mapping: dict[str, str] = {}
    if trans_path.is_file():
        mapping.update(load_mapping(trans_path))
    for ocr in sorted(root.glob("*_ocr.json")):
        parsed = load_mapping(ocr)
        for src, dst in parsed.items():
            if src not in mapping or (not mapping[src] and dst):
                mapping[src] = dst
    if not mapping:
        raise RuntimeError(f"no OCR strings in {root}")
    return mapping, trans_path


def _translate_batch(batch: list[str], *, engine: str) -> list[str]:
    if engine == "local":
        return local_qwen.translate_texts(batch, system=local_qwen.IMAGE_SYSTEM)
    url, key, model = translate_direct.load_upstream()
    return translate_direct.call(url, key, model, batch, engine="mt")


def pending_keys(data: dict[str, str], originals: list[str]) -> list[str]:
    return [
        key
        for key in originals
        if key not in SKIP and not local_qwen.translation_ok(key, data.get(key, ""))
    ]


def translate_mapping(
    data: dict[str, str],
    dest: Path,
    *,
    engine: str = "local",
    translate_fn=None,
) -> dict[str, str]:
    originals = [key for key in data if key not in SKIP]
    keys = pending_keys(data, originals)
    dest.parent.mkdir(parents=True, exist_ok=True)
    (dest.parent / "filled_keys.json").write_text(
        json.dumps(keys, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    if translate_fn is None:
        if engine == "local":
            ok, detail = local_qwen.probe()
            if not ok:
                raise RuntimeError(
                    "local Qwen not listening on 127.0.0.1:18135 ("
                    + detail
                    + "). Open Local AI first; do not start MiniMax H3."
                )
            chunk = local_qwen.CHUNK
            print(
                f"translate {len(keys)}/{len(data)} empty OCR strings engine=local model={local_qwen.LOCAL_MODEL}"
            )
        else:
            chunk = translate_direct.CHUNK
            print(f"translate {len(keys)}/{len(data)} empty OCR strings engine=mt")

        def translate_fn(batch: list[str]) -> list[str]:
            return _translate_batch(batch, engine=engine)
    else:
        chunk = local_qwen.CHUNK
        print(f"translate {len(keys)}/{len(data)} empty OCR strings engine={engine}")

    total = len(keys)
    local_qwen.emit("phase", "fill", done=0, total=total, message=f"填字 {total}")

    def run_batch(batch: list[str]) -> None:
        queries = [local_qwen.repair_ocr_bang(item) for item in batch]
        last_exc: Exception | None = None
        out: list[str] | None = None
        for attempt in range(4):
            try:
                out = translate_fn(queries)
                last_exc = None
                break
            except Exception as exc:  # noqa: BLE001
                last_exc = exc
                wait = 2 ** attempt
                print(f"retry {attempt+1} after {wait}s: {type(exc).__name__}: {exc}")
                time.sleep(wait)
        if out is None:
            print(f"batch failed, trying one by one: {type(last_exc).__name__}: {last_exc}")
            out = []
            for one in queries:
                try:
                    out.extend(translate_fn([one]))
                except Exception as one_exc:  # noqa: BLE001
                    preview = one.replace("\n", " ")[:40]
                    print(f"skip {type(one_exc).__name__}: {preview}")
                    out.append("")
        for src_text, dst_text in zip(batch, out):
            local_qwen.store_translation(data, src_text, dst_text)
        dest.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        accepted = total - len(pending_keys(data, originals))
        print(f"progress {accepted}/{total}")
        local_qwen.emit(
            "progress", "fill", done=max(0, accepted), total=total, message=f"{accepted}/{total}"
        )

    for i in range(0, len(keys), chunk):
        run_batch(keys[i : i + chunk])
    for round_i in range(2):
        leftover = pending_keys(data, originals)
        if not leftover:
            break
        print(f"retry untranslated round {round_i + 2}: {len(leftover)}")
        for one in leftover:
            run_batch([one])

    filled = sum(1 for key in originals if local_qwen.translation_ok(key, data.get(key, "")))
    leftover = pending_keys(data, originals)
    print(f"done filled={filled} leftover={len(leftover)} total={len(originals)} -> {dest}")
    local_qwen.emit(
        "done",
        "fill",
        done=filled,
        total=len(originals),
        empty=len(leftover),
        output=str(dest),
        message=(
            f"填了 {filled}/{len(originals)}"
            if not leftover
            else f"填了 {filled}/{len(originals)}，未译 {len(leftover)}"
        ),
    )
    return data


def main() -> int:
    argv = list(sys.argv[1:])
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    local_qwen.enable_progress(jsonl)
    engine = "local"
    if "--mt" in argv:
        engine = "mt"
        argv = [a for a in argv if a != "--mt"]
    if "--local" in argv:
        engine = "local"
        argv = [a for a in argv if a != "--local"]
    if len(argv) < 1:
        print("usage: fill_ocr_local.py [--mt] [--progress-jsonl] <translations.json | work-folder>", file=sys.stderr)
        return 2
    target = Path(argv[0])
    if target.is_dir():
        data, dest = collect_from_dir(target)
    elif target.is_file():
        data = load_mapping(target)
        dest = target
    else:
        print(f"not found: {target}", file=sys.stderr)
        return 1
    translate_mapping(data, dest, engine=engine)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
