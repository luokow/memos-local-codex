# Spike wrapper: send split contents boxes or uncovered balloons through MIT
# Lama as crops, then paste back. Not a CPU overlay paintbrush.
from __future__ import annotations

import json
import re
import shutil
import sys
from pathlib import Path

import bubble_recall_local
import cjk_wrap
import local_qwen
import typeset_ocr_local

CROP_PAD = 16
CROP_DIR = "_crop_mit"
TINY_LEFTOVER = 0.25
TINY_MIN_HAN = 8
TINY_MIN_ASPECT = 1.4
# Partial MIT leftover: dest painted but JP still shows. Untouched SFX/logos
# sit at leftover≈1.0; already-covered balloons sit below 0.25.
BUBBLE_LEFTOVER_MIN = 0.25
BUBBLE_LEFTOVER_MAX = 0.90
BUBBLE_MAX_CORE = 40
BUBBLE_MAX_PAGE = 4
BUBBLE_MIN_WHITE = 0.60
_COLOPHON_RE = re.compile(r"^[★＊*]|[0-9０-９]+万?円|[0-9０-９]+月[0-9０-９]+日")
# MIT DefaultDetector pads with black when min(w,h) < 400
# (manga_translator/detection/common.py minimum_image_size).
MIT_MIN_SIDE = 400
CROP_DETECT_SIZE = 1024
CROP_OCR_PROB = 0.05
_EPISODE_RE = re.compile(r"〈(?:第[0-9０-９]+話|最終話)〉")
_PAGE_RE = re.compile(r"^[0-9０-９]+P")
_PAGE_PREFIX_RE = re.compile(r"^[0-9０-９]+[PpＰｐ](?:[ \t]+|\n|$)")


def padded_box(box: list[int], width: int, height: int, pad: int = CROP_PAD) -> list[int]:
    x1, y1, x2, y2 = (int(v) for v in box)
    return bubble_recall_local.clamp_box(
        [x1 - pad, y1 - pad, x2 + pad, y2 + pad], width, height
    )


def clip_box_away_from_keepouts(box: list[int], keepouts: list[list[int]]) -> list[int]:
    out = [int(v) for v in box[:4]]
    if out[3] <= out[1]:
        return out
    center = (out[1] + out[3]) / 2
    for other in keepouts:
        if len(other) < 4 or other == box:
            continue
        if other[2] <= out[0] or other[0] >= out[2]:
            continue
        other_center = (other[1] + other[3]) / 2
        if other_center < center and other[3] < out[3]:
            out[1] = max(out[1], int(other[3]))
        elif other_center > center and other[1] > out[1]:
            out[3] = min(out[3], int(other[1]))
    return out


def padded_box_away_from_neighbors(
    box: list[int],
    others: list[list[int]],
    width: int,
    height: int,
    pad: int = CROP_PAD,
) -> list[int]:
    padded = padded_box(box, width, height, pad)
    for other in others:
        if other == box or len(other) < 4:
            continue
        if other[1] >= box[3]:
            mid = box[3] + max(0, other[1] - box[3]) // 2
            padded[3] = min(padded[3], mid if mid > padded[1] else padded[3])
        if other[3] <= box[1]:
            mid = other[3] + max(0, box[1] - other[3]) // 2
            padded[1] = max(padded[1], mid if mid < padded[3] else padded[1])
        if other[0] >= box[2]:
            mid = box[2] + max(0, other[0] - box[2]) // 2
            padded[2] = min(padded[2], mid if mid > padded[0] else padded[2])
        if other[2] <= box[0]:
            mid = other[2] + max(0, box[0] - other[2]) // 2
            padded[0] = max(padded[0], mid if mid < padded[2] else padded[0])
    return bubble_recall_local.clamp_box(padded, width, height)


def vertical_ink_columns(
    image,
    box: list[int],
    *,
    dark: int = 80,
    min_density: float = 0.06,
    min_width: int = 8,
    merge_gap: int = 6,
) -> list[list[int]]:
    x1, y1, x2, y2 = (int(v) for v in box[:4])
    if x2 - x1 < 8 or y2 - y1 < 8:
        return []
    gray = image.convert("L").crop((x1, y1, x2, y2))
    width, height = gray.size
    pixels = gray.load()
    densities = []
    for x in range(width):
        dark_n = sum(1 for y in range(height) if pixels[x, y] < dark)
        densities.append(dark_n / max(1, height))
    bands: list[tuple[int, int]] = []
    start = None
    for x, density in enumerate(densities):
        if density >= min_density:
            if start is None:
                start = x
        elif start is not None:
            bands.append((start, x))
            start = None
    if start is not None:
        bands.append((start, width))
    merged: list[tuple[int, int]] = []
    for left, right in bands:
        if merged and left - merged[-1][1] <= merge_gap:
            merged[-1] = (merged[-1][0], right)
        else:
            merged.append((left, right))
    cols = []
    for left, right in merged:
        if right - left < min_width:
            continue
        cols.append([x1 + left, y1, x1 + right, y2])
    return cols


_DEST_TOKEN_RE = re.compile(r".+?(?:！！|!！|！+|？+|\?+|♥+|♡+|。|，|、|…+|$)")


def _dest_tokens(text: str) -> list[str]:
    compact = "".join(str(text or "").split())
    if not compact:
        return []
    tokens = [part for part in _DEST_TOKEN_RE.findall(compact) if part]
    return tokens or [compact]


def _split_long_token(token: str) -> list[str]:
    if len(token) < 4:
        return [token]
    mid = max(1, (len(token) + 1) // 2)
    cols = cjk_wrap.wrap_cjk_columns(token, mid)
    if len(cols) >= 2:
        return cols
    return [token[:mid], token[mid:]]


def _pack_dest_tokens(tokens: list[str], count: int) -> list[str]:
    if count <= 1:
        joined = "".join(tokens)
        return [joined] if joined else []
    if not tokens:
        return []
    target = max(1, (len("".join(tokens)) + count - 1) // count)
    limit = max(target, int(target * 1.25))
    parts: list[str] = []
    index = 0
    for col in range(count):
        remaining_cols = count - col
        remaining_toks = len(tokens) - index
        if remaining_cols <= 1 or remaining_toks <= 0:
            parts.append("".join(tokens[index:]))
            break
        take = 1
        while index + take < len(tokens) and remaining_toks - take >= remaining_cols - 1:
            trial = "".join(tokens[index : index + take + 1])
            if len(trial) <= limit:
                take += 1
                continue
            break
        take = min(take, remaining_toks - (remaining_cols - 1))
        take = max(1, take)
        parts.append("".join(tokens[index : index + take]))
        index += take
    return [part for part in parts if part]


def split_dest_for_columns(dst: str, count: int) -> list[str]:
    text = str(dst or "").strip()
    if count <= 1:
        return [text] if text else []
    tokens = _dest_tokens(text)
    if not tokens:
        return []
    guard = 0
    while len(tokens) < count and guard < 8:
        guard += 1
        longest = max(range(len(tokens)), key=lambda i: len(tokens[i]))
        bits = _split_long_token(tokens[longest])
        if len(bits) < 2:
            break
        tokens[longest : longest + 1] = bits
    parts = _pack_dest_tokens(tokens, count)
    if len(parts) != count or any(not part for part in parts):
        compact = "".join(tokens)
        cols = cjk_wrap.wrap_cjk_columns(
            compact, max(1, (len(compact) + count - 1) // count)
        )
        if len(cols) == count:
            return cols
        return []
    return parts


def tiny_font_balloons(work: Path, stem: str) -> list[tuple[str, list[int]]]:
    stored = bubble_recall_local.load_json(bubble_recall_local.recall_path(work), {})
    rec = stored.get(stem) if isinstance(stored, dict) else None
    regions = rec.get("regions") if isinstance(rec, dict) else None
    if not regions:
        return []
    mapping = bubble_recall_local.load_json(work / "translations.json", {})
    if not isinstance(mapping, dict):
        mapping = {}
    source_path = _find_out_image(work / "typeset_in", stem)
    dest_path = _find_out_image(work / "out", stem)
    if source_path is None or dest_path is None:
        return []
    from PIL import Image

    source = Image.open(source_path).convert("RGB")
    dest = Image.open(dest_path).convert("RGB")
    out: list[tuple[str, list[int]]] = []
    seen: set[tuple[int, int, int, int]] = set()
    for row in regions:
        if not isinstance(row, dict) or row.get("covered"):
            continue
        src = str(row.get("text") or "")
        box = list(row.get("xyxy") or [])
        if len(box) < 4 or bubble_recall_local.skip_as_art(src, box):
            continue
        dst = bubble_recall_local.lookup_translation(mapping, src)
        if not dst or not local_qwen.translation_ok(src, dst):
            continue
        xyxy = [int(v) for v in box[:4]]
        width = xyxy[2] - xyxy[0]
        height = xyxy[3] - xyxy[1]
        if width < 8 or height < width * TINY_MIN_ASPECT:
            continue
        dest_han = len(local_qwen._HAN_RE.findall(dst))
        if dest_han < TINY_MIN_HAN:
            continue
        key = (xyxy[0], xyxy[1], xyxy[2], xyxy[3])
        if key in seen:
            continue
        frac = bubble_recall_local.leftover_source_ink_fraction(source, dest, xyxy)
        if frac < TINY_LEFTOVER:
            continue
        seen.add(key)
        out.append((src, xyxy))
    return out


def _expand_columns_to_gaps(cols: list[list[int]], box: list[int]) -> list[list[int]]:
    if not cols:
        return []
    out: list[list[int]] = []
    for i, col in enumerate(cols):
        left = int(box[0]) if i == 0 else (cols[i - 1][2] + col[0]) // 2
        right = int(box[2]) if i == len(cols) - 1 else (col[2] + cols[i + 1][0]) // 2
        if right <= left:
            right = left + 1
        out.append([left, int(box[1]), right, int(box[3])])
    for i in range(len(out) - 1):
        if out[i][2] > out[i + 1][0]:
            out[i][2] = out[i + 1][0]
        if out[i][2] <= out[i][0]:
            out[i][2] = out[i][0] + 1
    return out


def tiny_font_column_jobs(work: Path, stem: str) -> list[tuple[str, str, list[int]]]:
    balloons = tiny_font_balloons(work, stem)
    if not balloons:
        return []
    source_path = _find_out_image(work / "typeset_in", stem)
    if source_path is None:
        return []
    from PIL import Image

    source = Image.open(source_path).convert("RGB")
    mapping = bubble_recall_local.load_json(work / "translations.json", {})
    if not isinstance(mapping, dict):
        mapping = {}
    jobs: list[tuple[str, str, list[int]]] = []
    for src, box in balloons:
        dst = bubble_recall_local.lookup_translation(mapping, src)
        if not dst:
            continue
        cols = _expand_columns_to_gaps(vertical_ink_columns(source, box), box)
        if len(cols) < 2:
            jobs.append((src, dst, [int(v) for v in box[:4]]))
            continue
        parts = split_dest_for_columns(dst, len(cols))
        if len(parts) != len(cols):
            jobs.append((src, dst, [int(v) for v in box[:4]]))
            continue
        for col, part in zip(reversed(cols), parts):
            jobs.append((src, part, [int(v) for v in col[:4]]))
    return jobs


def title_bind_dst(dst: str) -> str:
    """Keep page numbers on the first MIT pass; do not typeset them in the title crop."""
    text = str(dst or "")
    while True:
        stripped = _PAGE_PREFIX_RE.sub("", text, count=1)
        if stripped == text:
            break
        text = stripped
    return text.strip()


def page_number_box(boxes: list[list[int]]) -> list[int] | None:
    if len(boxes) < 2:
        return None
    ordered = sorted(boxes, key=lambda box: (box[1], box[0]))
    top = ordered[0]
    max_w = max(box[2] - box[0] for box in boxes)
    if max_w > 0 and (top[2] - top[0]) <= max_w * 0.45:
        return top
    return None


def _union_boxes(boxes: list[list[int]]) -> list[int]:
    return [
        min(box[0] for box in boxes),
        min(box[1] for box in boxes),
        max(box[2] for box in boxes),
        max(box[3] for box in boxes),
    ]


def _contents_clusters(
    src: str, group: list[list[int]]
) -> list[tuple[str, list[list[int]]]]:
    parts = local_qwen.split_merged_titles(src)
    clusters = bubble_recall_local.cluster_boxes_by_y_gap(group)
    if len(parts) >= 2 and len(clusters) == len(parts):
        return list(zip(parts, clusters))
    if bubble_recall_local.is_contents_entry(src) and group:
        return [(src, group)]
    return []


def contents_keepout_boxes(work: Path, stem: str) -> list[list[int]]:
    keepouts: list[list[int]] = []
    for src, group in bubble_recall_local.sidecar_labeled_groups(work, stem):
        for _text, cluster in _contents_clusters(src, group):
            page = page_number_box(cluster)
            if page:
                keepouts.append([int(v) for v in page[:4]])
    return keepouts


def contents_crop_boxes(work: Path, stem: str) -> list[tuple[str, list[int]]]:
    boxes: list[tuple[str, list[int]]] = []
    for src, group in bubble_recall_local.sidecar_labeled_groups(work, stem):
        parts: list[tuple[str, list[int]]] = []
        for text, cluster in _contents_clusters(src, group):
            page = page_number_box(cluster)
            titles = [box for box in cluster if box != page] if page else list(cluster)
            if not titles:
                titles = list(cluster)
            parts.append((text, _union_boxes(titles)))
        if parts:
            boxes.extend(bubble_recall_local.close_split_gaps(parts))
    return boxes


def crop_overlay_map(pairs: list[tuple[str, str]]) -> dict[str, str]:
    """Exact keys plus collision-safe episode/page aliases for crop MIT lookup."""
    overlay: dict[str, str] = {}
    for src, dst in pairs:
        if not src or not dst:
            continue
        overlay[src] = dst
        overlay[src.strip()] = dst
        cleaned = local_qwen.repair_ocr_bang(src)
        overlay[cleaned] = dst
    aliases: list[tuple[str, str]] = []
    for src, dst in pairs:
        if not src or not dst:
            continue
        match = _EPISODE_RE.search(src)
        if match:
            aliases.append((match.group(0), dst))
            aliases.append((match.group(0).strip("〈〉"), dst))
        stripped = _PAGE_RE.sub("", src.replace(" ", ""))
        if stripped and stripped != src and not _PAGE_RE.match(stripped):
            aliases.append((stripped, dst))
            cleaned_stripped = local_qwen.repair_ocr_bang(stripped)
            if cleaned_stripped and not _PAGE_RE.match(cleaned_stripped):
                aliases.append((cleaned_stripped, dst))
    buckets: dict[str, set[str]] = {}
    for key, dst in aliases:
        if key:
            buckets.setdefault(key, set()).add(dst)
    for key, dsts in buckets.items():
        if len(dsts) == 1 and key not in overlay:
            overlay[key] = next(iter(dsts))
    return overlay


def translated_crop_jobs(
    work: Path, boxes: list[tuple[str, list[int]]]
) -> list[tuple[str, str, list[int]]]:
    mapping = bubble_recall_local.load_json(work / "translations.json", {})
    if not isinstance(mapping, dict):
        mapping = {}
    jobs: list[tuple[str, str, list[int]]] = []
    for src, box in boxes:
        dst = bubble_recall_local.lookup_translation(mapping, src)
        if not dst or not local_qwen.translation_ok(src, dst):
            continue
        if bubble_recall_local.is_contents_entry(src):
            dst = title_bind_dst(dst)
            if not dst:
                continue
        jobs.append((src, dst, [int(v) for v in box[:4]]))
    return jobs


def leftover_src_ok(src: str, dst: str) -> bool:
    """Drop OCR garbage / colophon; keep dialogue with a Chinese dest."""
    if not local_qwen._HIRA_RE.search(src) and not local_qwen._HAN_RE.search(src):
        return False
    if len(bubble_recall_local._core(src)) >= BUBBLE_MAX_CORE:
        return False
    if _COLOPHON_RE.search(src):
        return False
    return len(local_qwen._HAN_RE.findall(dst or "")) >= 1


def leftover_box_ok(source, _dest, xyxy: list[int], frac: float) -> bool:
    """White balloon, partial leftover only — not SFX art or already-covered ink."""
    if frac < BUBBLE_LEFTOVER_MIN or frac >= BUBBLE_LEFTOVER_MAX:
        return False
    if not bubble_recall_local.balloon_is_white(source, xyxy):
        return False
    return bubble_recall_local.white_fraction(source, xyxy) >= BUBBLE_MIN_WHITE


def uncovered_translated_boxes(work: Path, stem: str) -> list[tuple[str, list[int]]]:
    stored = bubble_recall_local.load_json(bubble_recall_local.recall_path(work), {})
    rec = stored.get(stem) if isinstance(stored, dict) else None
    regions = rec.get("regions") if isinstance(rec, dict) else None
    if not regions:
        return []
    mapping = bubble_recall_local.load_json(work / "translations.json", {})
    if not isinstance(mapping, dict):
        mapping = {}
    src_img = dest_img = None
    source_path = _find_out_image(work / "typeset_in", stem)
    dest_path = _find_out_image(work / "out", stem)
    if source_path is not None and dest_path is not None:
        from PIL import Image

        src_img = Image.open(source_path).convert("RGB")
        dest_img = Image.open(dest_path).convert("RGB")
    raw: list[tuple[str, str, list[int], float]] = []
    seen: set[tuple[int, int, int, int]] = set()
    for row in regions:
        if not isinstance(row, dict) or row.get("covered"):
            continue
        src = str(row.get("text") or "")
        box = list(row.get("xyxy") or [])
        core = bubble_recall_local._core(src)
        if len(box) < 4 or len(core) < 8 or bubble_recall_local.skip_as_art(src, box):
            continue
        dst = bubble_recall_local.lookup_translation(mapping, src)
        if not dst or not local_qwen.translation_ok(src, dst):
            continue
        xyxy = [int(v) for v in box[:4]]
        key = (xyxy[0], xyxy[1], xyxy[2], xyxy[3])
        if key in seen:
            continue
        frac = 0.0
        if src_img is not None and dest_img is not None:
            frac = bubble_recall_local.leftover_source_ink_fraction(
                src_img, dest_img, xyxy
            )
            if frac < 0.12:
                continue
        seen.add(key)
        raw.append((src, dst, xyxy, frac))
    if src_img is not None and dest_img is not None and len(raw) > BUBBLE_MAX_PAGE:
        return []
    out: list[tuple[str, list[int]]] = []
    for src, dst, xyxy, frac in raw:
        if not leftover_src_ok(src, dst):
            continue
        if src_img is not None and dest_img is not None:
            if not leftover_box_ok(src_img, dest_img, xyxy, frac):
                continue
        out.append((src, xyxy))
    return out


def expand_box_to_cover_diff(
    box: list[int],
    source,
    dest,
    others: list[list[int]],
    pad: int = CROP_PAD,
    search_pad: int = 48,
) -> list[int]:
    from PIL import ImageChops

    width, height = source.size
    grown = [int(v) for v in box[:4]]
    reach = search_pad
    for _ in range(4):
        search = padded_box(grown, width, height, reach)
        crop_s = source.crop((search[0], search[1], search[2], search[3]))
        crop_d = dest.crop((search[0], search[1], search[2], search[3]))
        if crop_d.size != crop_s.size:
            crop_d = crop_d.resize(crop_s.size)
        diff = ImageChops.difference(crop_s, crop_d).convert("L")
        mask = diff.point(lambda pixel: 255 if pixel >= 28 else 0)
        ink = mask.getbbox()
        if not ink:
            break
        grown = [
            min(grown[0], search[0] + ink[0]),
            min(grown[1], search[1] + ink[1]),
            max(grown[2], search[0] + ink[2]),
            max(grown[3], search[1] + ink[3]),
        ]
        grown = clip_box_away_from_keepouts(grown, others)
        sw = search[2] - search[0]
        sh = search[3] - search[1]
        if ink[0] > 1 and ink[1] > 1 and ink[2] < sw - 1 and ink[3] < sh - 1:
            break
        reach = min(reach + search_pad, max(width, height))
    return padded_box_away_from_neighbors(grown, others, width, height, pad)


def prepare_crop_canvas(crop):
    """4x/2x then white-pad so MIT skips black _add_border on small crops."""
    from PIL import Image

    width, height = crop.size
    short = min(width, height)
    if short >= MIT_MIN_SIDE:
        return crop, (width, height), 1
    scale = 4 if short * 2 < MIT_MIN_SIDE else 2
    big = crop.resize((width * scale, height * scale), Image.Resampling.NEAREST)
    big_w, big_h = big.size
    if min(big_w, big_h) >= MIT_MIN_SIDE:
        return big, (big_w, big_h), scale
    side = max(MIT_MIN_SIDE, big_w, big_h)
    canvas = Image.new("RGB", (side, side), (255, 255, 255))
    canvas.paste(big, (0, 0))
    return canvas, (big_w, big_h), scale


def crop_content_from_canvas(image, content_size: tuple[int, int]):
    content_w, content_h = (int(content_size[0]), int(content_size[1]))
    width, height = image.size
    return image.crop((0, 0, min(content_w, width), min(content_h, height)))


def crop_mit_config() -> dict:
    """Official small-crop knobs: lower detection_size, lower ocr.prob."""
    base = next((path for path in typeset_ocr_local.CONFIG_CANDIDATES if path.is_file()), None)
    cfg = json.loads(base.read_text(encoding="utf-8")) if base is not None else {}
    detector = dict(cfg.get("detector") or {})
    detector["detection_size"] = CROP_DETECT_SIZE
    cfg["detector"] = detector
    ocr = dict(cfg.get("ocr") or {})
    ocr["prob"] = CROP_OCR_PROB
    cfg["ocr"] = ocr
    translator = dict(cfg.get("translator") or {})
    translator["translator"] = "none"
    cfg["translator"] = translator
    inpainter = dict(cfg.get("inpainter") or {})
    inpainter["inpainter"] = "lama_large"
    cfg["inpainter"] = inpainter
    return cfg


def crop_row_should_paste(row: dict) -> bool:
    queries = row.get("queries") or []
    return any(str(item or "").strip() for item in queries)


def save_pasted_page(page, dest_path: Path) -> None:
    """Match MIT JPEG --save-quality 100; PNG/WebP stay lossless."""
    suffix = dest_path.suffix.lower()
    if suffix in {".jpg", ".jpeg"}:
        page.save(dest_path, quality=100, subsampling=0, format="JPEG")
        return
    page.save(dest_path)


def _find_out_image(folder: Path, stem: str) -> Path | None:
    for suffix in local_qwen.IMAGE_SUFFIXES:
        path = folder / f"{stem}{suffix}"
        if path.is_file():
            return path
    return None


def crop_mit_page(
    work: Path,
    stem: str,
    boxes: list[tuple[str, list[int]]],
    *,
    pad: int = CROP_PAD,
    keepouts: list[list[int]] | None = None,
    cover_out: bool = False,
) -> int:
    from PIL import Image

    if boxes and len(boxes[0]) == 3:
        bound = []
        for src, dst, box in boxes:
            bound.append((str(src), str(dst or ""), [int(v) for v in box[:4]]))
    else:
        bound = translated_crop_jobs(work, boxes)
    if not bound:
        return 0
    source_path = _find_out_image(work / "typeset_in", stem)
    dest_path = _find_out_image(work / "out", stem)
    if source_path is None or dest_path is None:
        print(f"crop-mit skip missing page {stem}", flush=True)
        return 0
    source = Image.open(source_path).convert("RGB")
    page = Image.open(dest_path).convert("RGB")
    crops_in = work / CROP_DIR / "in"
    crops_out = work / CROP_DIR / "out"
    overlay_path = work / CROP_DIR / "translations.json"
    lookup_log = work / CROP_DIR / "lookup.jsonl"
    if crops_in.exists():
        shutil.rmtree(crops_in)
    if crops_out.exists():
        shutil.rmtree(crops_out)
    crops_in.mkdir(parents=True, exist_ok=True)
    jobs: list[tuple[str, str, str, list[int]]] = []
    raw_boxes = [box for _, _, box in bound]
    barriers = raw_boxes + [list(item[:4]) for item in (keepouts or [])]
    for i, (text, dst, box) in enumerate(bound):
        if cover_out:
            grown = clip_box_away_from_keepouts(
                expand_box_to_cover_diff(box, source, page, barriers, pad=0),
                barriers,
            )
        else:
            grown = box
        padded = padded_box_away_from_neighbors(
            grown, barriers, source.size[0], source.size[1], pad
        )
        x1, y1, x2, y2 = padded
        if x2 - x1 < 8 or y2 - y1 < 8:
            continue
        name = f"{stem}__{i:02d}.png"
        canvas, content_size, _scale = prepare_crop_canvas(source.crop((x1, y1, x2, y2)))
        canvas.save(crops_in / name)
        jobs.append((name, text, dst, padded, content_size))
        print(f"crop-mit {stem} {text[:24]} {padded} canvas={canvas.size}", flush=True)
    if not jobs:
        return 0
    overlay_path.parent.mkdir(parents=True, exist_ok=True)
    overlay = crop_overlay_map([(src, dst) for _, src, dst, _, _ in jobs])
    overlay_path.write_text(
        json.dumps(overlay, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    cfg_path = work / CROP_DIR / "mit_config.json"
    cfg_path.write_text(
        json.dumps(crop_mit_config(), ensure_ascii=False, indent=2), encoding="utf-8"
    )
    rc = typeset_ocr_local.run_mit_folder(
        work,
        crops_in,
        crops_out,
        trans=overlay_path,
        relaxed=True,
        crop_queue=[dst for _, _, dst, _, _ in jobs],
        lookup_log=lookup_log,
        config_file=cfg_path,
    )
    if rc != 0:
        print(f"crop-mit mit_exit={rc}", flush=True)
        return 0
    log_rows: list[dict] = []
    if lookup_log.is_file():
        for line in lookup_log.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(row, dict):
                log_rows.append(row)
    if len(log_rows) != len(jobs):
        print(
            f"crop-mit skip paste {stem}: lookup log {len(log_rows)} != crops {len(jobs)}",
            flush=True,
        )
        return 0
    pasted = 0
    for (name, _src, _dst, padded, content_size), row in zip(jobs, log_rows):
        if not crop_row_should_paste(row):
            print(f"crop-mit skip empty {name}", flush=True)
            continue
        crop_path = _find_out_image(crops_out, Path(name).stem)
        if crop_path is None:
            print(f"crop-mit missing output {name}", flush=True)
            continue
        crop = crop_content_from_canvas(Image.open(crop_path).convert("RGB"), content_size)
        x1, y1, x2, y2 = padded
        w, h = x2 - x1, y2 - y1
        if crop.size != (w, h):
            crop = crop.resize((w, h), Image.Resampling.LANCZOS)
        page.paste(crop, (x1, y1))
        pasted += 1
    if pasted:
        save_pasted_page(page, dest_path)
        print(f"crop-mit pasted={pasted} -> {dest_path.name}", flush=True)
    return pasted


def crop_mit_pages(
    work: Path,
    stems: list[str],
    *,
    contents: bool,
    bubbles: bool,
    tiny_font: bool = False,
) -> int:
    total = 0
    wanted = {stem.lower() for stem in stems}
    for img in local_qwen.list_image_files(work / "typeset_in"):
        stem = img.stem
        if wanted and stem.lower() not in wanted and img.name.lower() not in wanted:
            continue
        boxes: list = []
        keepouts: list[list[int]] = []
        if contents:
            boxes.extend(contents_crop_boxes(work, stem))
            keepouts.extend(contents_keepout_boxes(work, stem))
        if bubbles:
            boxes.extend(uncovered_translated_boxes(work, stem))
        if tiny_font:
            boxes.extend(tiny_font_column_jobs(work, stem))
        if not boxes:
            continue
        total += crop_mit_page(
            work, stem, boxes, keepouts=keepouts, cover_out=contents, pad=0 if tiny_font else CROP_PAD
        )
    return total


def main() -> int:
    argv = list(sys.argv[1:])
    argv, contents = local_qwen.take_flag(argv, "--contents")
    argv, bubbles = local_qwen.take_flag(argv, "--bubbles")
    argv, tiny_font = local_qwen.take_flag(argv, "--tiny-font")
    pages: list[str] | None = None
    if "--pages" in argv:
        idx = argv.index("--pages")
        if idx + 1 >= len(argv):
            print(
                "usage: crop_mit_local.py [--contents] [--bubbles] [--tiny-font] [--pages a,b] <work-folder>",
                file=sys.stderr,
            )
            return 2
        pages = [part.strip() for part in argv[idx + 1].split(",") if part.strip()]
        argv = argv[:idx] + argv[idx + 2 :]
    if len(argv) < 1 or not (contents or bubbles or tiny_font):
        print(
            "usage: crop_mit_local.py [--contents] [--bubbles] [--tiny-font] [--pages a,b] <work-folder>",
            file=sys.stderr,
        )
        return 2
    work = Path(argv[0]).resolve()
    stems = pages or [p.stem for p in local_qwen.list_image_files(work / "typeset_in")]
    n = crop_mit_pages(
        work, stems, contents=contents, bubbles=bubbles, tiny_font=tiny_font
    )
    print(f"crop-mit total={n}", flush=True)
    return 0 if n else 1


if __name__ == "__main__":
    raise SystemExit(main())
