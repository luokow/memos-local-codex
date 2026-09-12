# RT-DETR text_bubble recall (ogkalu/comic-text-and-bubble-detector).
# Detects dialogue MIT's line detector missed, OCRs the crop, overlays translation.
# CPU only. Does not replace manga-image-translator.
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import ssl
import subprocess
import sys
import urllib.request
from collections import deque
from pathlib import Path

import cjk_wrap
import local_qwen

MODEL_NAME = "detector-v4-s_int8.onnx"
MODEL_SHA256 = "5fe9e4f576e49d4e7e8b0e029d6d3cdc252abd4694113e1cae120e62c931ea79"
MODEL_URLS = (
    "https://huggingface.co/ogkalu/comic-text-and-bubble-detector/resolve/main/detector-v4-s_int8.onnx",
    "https://hf-mirror.com/ogkalu/comic-text-and-bubble-detector/resolve/main/detector-v4-s_int8.onnx",
)
CLASSES = {0: "bubble", 1: "text_bubble", 2: "text_free"}
CONF = 0.3
MIN_SIDE = 12
FRAGMENT_RATIO = 0.6
COVER_IOU = 0.2
COVER_OVERLAP = 0.5
HUGE_BOX_AREA = 180_000
_PUNCT_ONLY_RE = re.compile(r"^[\s。．.、，,！!？?…・♥♡~〜～「」『』（）()：:／/]+$")
_SMALL_TSU_SFX_RE = re.compile(r"^[\u3040-\u309f]{1,2}っ+[！!？?]*$")
_STUTTER_ONLY_RE = re.compile(
    r"^([ぁ-んァ-ン]{1,2})([、，,っッ]+)\1(?:\2|\1|[、，,っッ])*$"
)
_CONNECTIVE_SFX_RE = re.compile(
    r"^(そして|それで|それから|とりあえず)[、，,．.…♥♡]*$"
)
_REPEAT_KANA_SFX_RE = re.compile(
    r"^([ぁ-んァ-ン]{2,3})\1[！!？?っッ]*$"
)
_IKI_OCR_RE = re.compile(r"^[1１][2２][．.・\s]*[1１][2２]$")
RECALL_NAME = "bubble_recall.json"
UNCOVERED_NAME = "uncovered_dialogue.json"

SKIP = {"", "trans", "text"}
_VERTICAL_COL_HEIGHT = 1.08
# CJK glyphs occupy ~1em; 1.12em stacked 边边/上上, 1.38em still packed 溅 onto 对.
_VERTICAL_COL_STRIDE = 1.55
_ELLIPSIS_RE = re.compile(r"[.．]{2,}|…+")
_WAVE_RE = re.compile(r"[〜～~]")
_COLON_RE = re.compile(r"[：:]")
_QUOTE_RE = re.compile(r"[\"'「」『』〝〟［］\[\]\u201c\u201d\u2018\u2019\uff02\uff07]")
_CORE_RE = re.compile(r"[^\u3040-\u30ff\u4e00-\u9fffA-Za-z0-9♥♡]")
_COORDS_RE = re.compile(r"coords:\s*\[([^\]]+)\]")


def pack_root() -> Path:
    return Path(__file__).resolve().parent.parent


def default_model_path() -> Path:
    env = str(os.environ.get("HANHUA_RTDETR_ONNX") or "").strip()
    if env:
        return Path(env)
    return pack_root() / "models" / "detection" / MODEL_NAME


def recall_path(work: Path) -> Path:
    return work / RECALL_NAME


def uncovered_path(work: Path) -> Path:
    return work / UNCOVERED_NAME


def _norm(text: str) -> str:
    out = local_qwen.repair_ocr_bang("".join((text or "").split()))
    out = _ELLIPSIS_RE.sub("…", out)
    out = _COLON_RE.sub("…", out)
    out = _QUOTE_RE.sub("", out)
    out = _WAVE_RE.sub("", out).replace("ー", "")
    out = out.replace("♡", "♥").replace("❤", "♥").replace("❥", "♥")
    return (
        out.replace("⁉", "！？")
        .replace("⁈", "？！")
        .replace("！?", "！？")
        .replace("!？", "！？")
    )


def _core(text: str) -> str:
    return _CORE_RE.sub("", _norm(text))


_LOOKUP_EDGE_RE = re.compile(
    r"^[ー－—―~〜～!！?？♡♥っッ‼・]+|[ー－—―~〜～!！?？♡♥っッ‼・]+$"
)
_LOOKUP_AFFIX_RE = re.compile(r"^[ー－—―~〜～!！?？♡♥っッ‼・ぁ-んァ-ン]{1,4}$")


def _lookup_stem(text: str) -> str:
    """Same balloon, extra ！/ー/♡ on the OCR key must still hit the filled line."""
    out = _norm(text).replace("？", "…").replace("?", "…")
    while True:
        nxt = _LOOKUP_EDGE_RE.sub("", out)
        if nxt == out:
            break
        out = nxt
    return out


def _lookup_affix(long: str, short: str) -> bool:
    if len(short) < 6 or len(long) <= len(short):
        return False
    if long.endswith(short):
        extra = long[: len(long) - len(short)]
    elif long.startswith(short):
        extra = long[len(short) :]
    else:
        return False
    return bool(_LOOKUP_AFFIX_RE.match(extra)) or not re.search(
        r"[\u3040-\u30ff\u4e00-\u9fffA-Za-z]", extra
    )


def lookup_translation(mapping: dict, query: str) -> str:
    hit = local_qwen.mapped_translation(mapping, query)
    if hit and local_qwen.translation_ok(query, hit):
        return hit
    if hit:
        return hit
    target = _lookup_stem(query)
    if not target:
        return ""
    exact = _norm(query)
    best = ""
    best_n = 0
    for key, val in mapping.items():
        if not val:
            continue
        if not local_qwen.translation_ok(str(key), str(val)):
            continue
        if exact and _norm(str(key)) == exact:
            return str(val)
        stem = _lookup_stem(str(key))
        if not stem:
            continue
        if target == stem:
            n = len(stem)
        elif _lookup_affix(target, stem) or _lookup_affix(stem, target):
            n = min(len(stem), len(target))
        else:
            continue
        if n > best_n:
            best, best_n = str(val), n
    return best


def _soft_norm(text: str) -> str:
    """Normalize quotes/ellipsis but keep ー so プライベート stays intact."""
    out = local_qwen.repair_ocr_bang("".join((text or "").split()))
    out = _ELLIPSIS_RE.sub("…", out)
    out = _COLON_RE.sub("…", out)
    out = _QUOTE_RE.sub("", out)
    out = _WAVE_RE.sub("", out)
    out = out.replace("♡", "♥").replace("❤", "♥").replace("❥", "♥")
    return (
        out.replace("⁉", "！？")
        .replace("⁈", "？！")
        .replace("！?", "！？")
        .replace("!？", "！？")
    )


def uncovered_src(ocr_text: str, sidecar_texts: list[str] | None) -> str:
    """Column MIT missed inside a merged balloon, e.g. プライベートでは."""
    from difflib import SequenceMatcher

    leftover = _soft_norm(ocr_text)
    for item in sorted(sidecar_texts or [], key=lambda s: len(_soft_norm(s)), reverse=True):
        piece = _soft_norm(item)
        if not piece or len(piece) < 4:
            continue
        if piece in leftover:
            leftover = leftover.replace(piece, "", 1)
            continue
        match = SequenceMatcher(None, leftover, piece, autojunk=False).find_longest_match(
            0, len(leftover), 0, len(piece)
        )
        if match.size >= max(4, int(len(piece) * 0.72)):
            leftover = leftover[: match.a] + leftover[match.a + match.size :]
    leftover = leftover.strip(" …・")
    return leftover if leftover else str(ocr_text or "")


def sidecar_covers(ocr_text: str, sidecar_texts: list[str]) -> bool:
    """True when MIT already captured this bubble (not a short fragment of it)."""
    a = _norm(ocr_text)
    ac = _core(ocr_text)
    if not a:
        return True
    min_keep = max(4, int(len(a) * FRAGMENT_RATIO))
    for raw in sidecar_texts:
        b = _norm(raw)
        bc = _core(raw)
        if not b:
            continue
        if a == b or a in b or (ac and ac == bc):
            return True
        if b in a and len(b) >= min_keep:
            leftover = a.replace(b, "", 1)
            leftover_c = _core(leftover)
            if leftover_c and _noise_tail(leftover):
                return True
            if leftover_c and (
                len(leftover_c) >= 4 or re.search(r"[\u4e00-\u9fff]", leftover)
            ):
                continue
            return True
    return False


def _noise_tail(text: str) -> bool:
    compact = _core(text)
    if re.fullmatch(r"[0-9０-９万千円円]+", compact or ""):
        return True
    return local_qwen.suspicious_ocr(text)


def _is_short_fragment(full: str, piece: str) -> bool:
    """True when MIT only kept a ruby/prefix of this balloon, e.g. ちょー ⊂ ちょーデカいんだけど♥."""
    a = _norm(full)
    b = _norm(piece)
    if not a or not b or a == b or a in b:
        return False
    min_keep = max(4, int(len(a) * FRAGMENT_RATIO))
    if b in a and len(b) < min_keep:
        return True
    a2, b2 = a.replace("…", ""), b.replace("…", "")
    if not b2 or b2 == a2 or a2 in b2:
        return False
    if b2 in a2 and len(b2) < min_keep:
        return True
    leftover = a2.replace(b2, "", 1) if b2 in a2 else ""
    leftover_c = _core(leftover)
    return bool(
        leftover_c
        and (len(leftover_c) >= 4 or re.search(r"[\u4e00-\u9fff]", leftover))
    )


def _coords_to_box(nums: list[int]) -> list[int] | None:
    if len(nums) >= 8:
        xs, ys = nums[0::2], nums[1::2]
        return [min(xs), min(ys), max(xs), max(ys)]
    if len(nums) == 4:
        return [
            min(nums[0], nums[2]),
            min(nums[1], nums[3]),
            max(nums[0], nums[2]),
            max(nums[1], nums[3]),
        ]
    return None


def _parse_coord_nums(raw: str) -> list[int]:
    nums: list[int] = []
    for part in raw.split(","):
        part = part.strip()
        if not part:
            continue
        try:
            nums.append(int(float(part)))
        except ValueError:
            return []
    return nums


def sidecar_boxes(work: Path, stem: str) -> list[list[int]]:
    boxes: list[list[int]] = []
    for folder in (work / "ocr_sidecars", work / "ocr_dump", work / "typeset_in"):
        path = folder / f"{stem}_translations.txt"
        if not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8-sig")
        except OSError:
            continue
        for match in _COORDS_RE.finditer(text):
            box = _coords_to_box(_parse_coord_nums(match.group(1)))
            if box:
                boxes.append(box)
    return boxes


def sidecar_labeled_groups(
    work: Path, stem: str
) -> list[tuple[str, list[list[int]]]]:
    groups: list[tuple[str, list[list[int]]]] = []
    for folder in (work / "ocr_sidecars", work / "ocr_dump", work / "typeset_in"):
        path = folder / f"{stem}_translations.txt"
        if not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8-sig")
        except OSError:
            continue
        current_text = ""
        current_boxes: list[list[int]] = []

        def flush() -> None:
            if not current_text or not current_boxes:
                current_boxes.clear()
                return
            groups.append((current_text, list(current_boxes)))
            current_boxes.clear()

        for line in text.splitlines():
            stripped = line.strip()
            if stripped.startswith("text:"):
                flush()
                current_text = stripped.split(":", 1)[1].strip()
                continue
            match = _COORDS_RE.search(line)
            if not match:
                continue
            box = _coords_to_box(_parse_coord_nums(match.group(1)))
            if box:
                current_boxes.append(box)
        flush()
        if groups:
            break
    return groups


def sidecar_labeled_boxes(work: Path, stem: str) -> list[tuple[str, list[int]]]:
    """(sidecar text, union box) for each MIT region, so ruby fragments can be told apart."""
    labeled: list[tuple[str, list[int]]] = []
    for text, boxes in sidecar_labeled_groups(work, stem):
        labeled.append(
            (
                text,
                [
                    min(b[0] for b in boxes),
                    min(b[1] for b in boxes),
                    max(b[2] for b in boxes),
                    max(b[3] for b in boxes),
                ],
            )
        )
    return labeled


def _boxes_overlap(box: list[int], other: list[int]) -> bool:
    return iou(box, other) >= COVER_IOU or overlap_frac(box, other) >= COVER_OVERLAP


def skip_as_art(
    ocr_text: str, box: list[int] | None = None, *, overlay: bool = True
) -> bool:
    """SFX / stutter / そして drawn as art. Dialogue like ね♥ / あとで / えっ must not match."""
    compact = "".join((ocr_text or "").split())
    if not compact:
        return True
    if _PUNCT_ONLY_RE.match(compact):
        return True
    if local_qwen.keep_source_art(ocr_text):
        return True
    if overlay and local_qwen.suspicious_ocr(ocr_text):
        return True
    core_n = len(_core(ocr_text))
    if overlay and local_qwen._latin_or_digit(ocr_text) and core_n <= 20:
        return True
    if overlay and box and len(box) >= 4:
        w = max(0, int(box[2]) - int(box[0]))
        h = max(0, int(box[3]) - int(box[1]))
        area = w * h
        # Essay-length OCR over a page-sized detector is garbage. Long
        # dialogue inside a normal balloon is still dialogue.
        if area >= HUGE_BOX_AREA and (core_n >= 40 or core_n <= 4):
            return True
    elif overlay and core_n >= 40:
        return True
    if _IKI_OCR_RE.match(compact):
        return True
    if "♥" in compact or "♡" in compact:
        return False
    if overlay and re.search(r"[0-9０-９]", compact) and re.search(r"[\u4e00-\u9fff]", compact):
        if 3 <= core_n <= 8 and not re.search(
            r"第[0-9０-９]+[話话]|[0-9０-９]+[話话Pｐp頁页人円]", compact
        ):
            return True
    if overlay:
        for match in re.finditer(r"(.{2,})\1", compact):
            if re.search(r"[\u4e00-\u9fff]", match.group(1)):
                return True
    if overlay and _SMALL_TSU_SFX_RE.match(compact):
        return True
    if _STUTTER_ONLY_RE.match(compact):
        return True
    if _CONNECTIVE_SFX_RE.match(compact):
        return True
    if local_qwen._short_sfx(ocr_text):
        return True
    letters = re.sub(r"[♥♡❤❥]", "", _core(compact))
    return bool(_REPEAT_KANA_SFX_RE.match(letters))


def already_typeset(
    ocr_text: str,
    box: list[int],
    known_texts: list[str],
    known_boxes: list[list[int]],
    labeled_boxes: list[tuple[str, list[int]]] | None = None,
    image=None,
    source=None,
) -> bool:
    if skip_as_art(ocr_text, box):
        return True
    if image is not None:
        return not needs_overlay(ocr_text, box, image, source, score=1.0)
    if labeled_boxes is not None:
        hits = [(text, other) for text, other in labeled_boxes if _boxes_overlap(box, other)]
        if hits:
            if all(_is_short_fragment(ocr_text, text) for text, _ in hits):
                return False
            return True
    if sidecar_covers(ocr_text, known_texts):
        return True
    if labeled_boxes is not None:
        return False
    return any(_boxes_overlap(box, other) for other in known_boxes)


def leftover_reason(
    ocr_text: str,
    box: list[int],
    known_texts: list[str],
    known_boxes: list[list[int]],
    labeled_boxes: list[tuple[str, list[int]]] | None,
    painted_boxes: list[list[int]],
    mapping: dict,
    image=None,
    source=None,
) -> str | None:
    """Why this balloon is still Japanese after MIT+overlay, or None if done/art."""
    if skip_as_art(ocr_text, box):
        return None
    if already_typeset(
        ocr_text,
        box,
        known_texts,
        known_boxes,
        labeled_boxes,
        image,
        source if source is not None else image,
    ):
        return None
    if any(_boxes_overlap(box, other) for other in painted_boxes):
        return None
    dst = lookup_translation(mapping, ocr_text)
    if not local_qwen.translation_ok(ocr_text, dst) or local_qwen.leftover_japanese(dst):
        return "untranslated"
    return "not_painted"


def _inter_area(a: list[int], b: list[int]) -> int:
    ax1, ay1, ax2, ay2 = a
    bx1, by1, bx2, by2 = b
    iw = max(0, min(ax2, bx2) - max(ax1, bx1))
    ih = max(0, min(ay2, by2) - max(ay1, by1))
    return iw * ih


def iou(a: list[int], b: list[int]) -> float:
    ax1, ay1, ax2, ay2 = a
    bx1, by1, bx2, by2 = b
    inter = _inter_area(a, b)
    if inter <= 0:
        return 0.0
    area_a = max(0, ax2 - ax1) * max(0, ay2 - ay1)
    area_b = max(0, bx2 - bx1) * max(0, by2 - by1)
    union = area_a + area_b - inter
    return inter / union if union else 0.0


def overlap_frac(a: list[int], b: list[int]) -> float:
    """How much of the smaller box sits inside the larger one."""
    ax1, ay1, ax2, ay2 = a
    bx1, by1, bx2, by2 = b
    inter = _inter_area(a, b)
    if inter <= 0:
        return 0.0
    smaller = min(max(0, ax2 - ax1) * max(0, ay2 - ay1), max(0, bx2 - bx1) * max(0, by2 - by1))
    return inter / smaller if smaller else 0.0


def clamp_box(box: list[int], width: int, height: int) -> list[int]:
    x1, y1, x2, y2 = (int(v) for v in box)
    x1, x2 = sorted((max(0, x1), max(0, x2)))
    y1, y2 = sorted((max(0, y1), max(0, y2)))
    x2 = min(width, max(x1 + 1, x2))
    y2 = min(height, max(y1 + 1, y2))
    return [x1, y1, x2, y2]


def dialogue_boxes(hits: list[dict]) -> list[dict]:
    texts = [h for h in hits if h.get("cls_id") == 1]
    bubbles = [h for h in hits if h.get("cls_id") == 0]
    frees = [h for h in hits if h.get("cls_id") == 2]
    out = list(texts)
    for bubble in bubbles:
        if not any(iou(bubble["xyxy"], text["xyxy"]) > 0.05 for text in texts):
            extra = dict(bubble)
            extra["cls"] = "text_bubble"
            extra["from_empty_bubble"] = True
            out.append(extra)
    for free in frees:
        extra = dict(free)
        extra["from_text_free"] = True
        out.append(extra)
    return out


def sidecar_texts(work: Path, stem: str) -> list[str]:
    texts: list[str] = []
    for folder in (work / "ocr_sidecars", work / "ocr_dump", work / "typeset_in"):
        path = folder / f"{stem}_ocr.json"
        if not path.is_file():
            continue
        try:
            parsed = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError):
            continue
        rows = parsed if isinstance(parsed, list) else [parsed]
        for row in rows:
            if isinstance(row, dict):
                if row.get("skip"):
                    continue
                if row.get("text"):
                    texts.append(str(row["text"]))
            elif isinstance(row, str) and row not in SKIP:
                texts.append(row)
    return texts


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ensure_model(path: Path | None = None) -> Path:
    dest = path or default_model_path()
    if dest.is_file() and _sha256(dest) == MODEL_SHA256:
        return dest
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(".part")
    ctx = ssl.create_default_context()
    last: Exception | None = None
    for url in MODEL_URLS:
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
            print(f"download {url}", flush=True)
            with urllib.request.urlopen(req, context=ctx, timeout=120) as resp, tmp.open(
                "wb"
            ) as out:
                while True:
                    chunk = resp.read(1024 * 1024)
                    if not chunk:
                        break
                    out.write(chunk)
            if _sha256(tmp) != MODEL_SHA256:
                raise RuntimeError("detector onnx sha256 mismatch")
            tmp.replace(dest)
            return dest
        except Exception as exc:  # noqa: BLE001
            last = exc
            print(f"download fail {url}: {exc}", flush=True)
    raise RuntimeError(f"cannot download {MODEL_NAME}: {last}")


def _session(model: Path):
    import onnxruntime as ort

    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    options.inter_op_num_threads = 1
    return ort.InferenceSession(
        str(model), sess_options=options, providers=["CPUExecutionProvider"]
    )


def detect_image(session, image) -> list[dict]:
    import numpy as np

    resized = image.resize((640, 640))
    arr = np.asarray(resized, dtype=np.float32) / 255.0
    arr = np.transpose(arr, (2, 0, 1))[np.newaxis, ...]
    width, height = image.size
    orig = np.array([[width, height]], dtype=np.int64)
    labels, boxes, scores = session.run(
        None, {"images": arr, "orig_target_sizes": orig}
    )[:3]
    if getattr(labels, "ndim", 0) == 2:
        labels = labels[0]
    if getattr(scores, "ndim", 0) == 2:
        scores = scores[0]
    if getattr(boxes, "ndim", 0) == 3:
        boxes = boxes[0]
    hits: list[dict] = []
    for lab, box, scr in zip(labels, boxes, scores):
        score = float(scr)
        if score < CONF:
            continue
        cls_id = int(lab)
        xyxy = clamp_box([int(v) for v in box], width, height)
        if xyxy[2] - xyxy[0] < MIN_SIDE or xyxy[3] - xyxy[1] < MIN_SIDE:
            continue
        hits.append(
            {
                "cls": CLASSES.get(cls_id, str(cls_id)),
                "cls_id": cls_id,
                "score": round(score, 3),
                "xyxy": xyxy,
            }
        )
    return hits


def _ocr_engine():
    from manga_ocr import MangaOcr

    return MangaOcr(force_cpu=True)


def _crop(image, box: list[int]):
    x1, y1, x2, y2 = box
    return image.crop((x1, y1, x2, y2))


def load_json(path: Path, default):
    if not path.is_file():
        return default
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return default
    return data if isinstance(data, type(default)) else default


_HEART_CHARS = set("♥♡❤❥")
_SYMBOL_FONT_CANDIDATES = (
    Path(r"C:\Windows\Fonts\seguisym.ttf"),
    Path(r"C:\Windows\Fonts\seguiemj.ttf"),
)


def _font(size: int, char: str = "汉"):
    from PIL import ImageFont

    if char in _HEART_CHARS:
        for path in _SYMBOL_FONT_CANDIDATES:
            if not path.is_file():
                continue
            try:
                return ImageFont.truetype(str(path), size)
            except OSError:
                continue
    cjk = Path(r"D:\grok\tools\manga-image-translator\fonts\msyh.ttc")
    mit_root = local_qwen.mit_root()
    candidate = mit_root / "fonts" / "msyh.ttc"
    cjk = candidate if candidate.is_file() else cjk
    try:
        return ImageFont.truetype(str(cjk), size)
    except OSError:
        return ImageFont.load_default()


def _line_width(draw, line: str, size: int) -> float:
    return float(
        sum(draw.textlength(char, font=_font(size, char)) or 0 for char in line)
    )


def _draw_line(
    draw,
    x: float,
    y: float,
    line: str,
    size: int,
    fill=(20, 20, 20),
    stroke_width: int = 0,
    stroke_fill=(20, 20, 20),
) -> None:
    cursor = float(x)
    for char in line:
        if char in "\r\n":
            continue
        font = _font(size, char)
        draw.text(
            (cursor, y),
            char,
            font=font,
            fill=fill,
            stroke_width=stroke_width,
            stroke_fill=stroke_fill,
        )
        cursor += draw.textlength(char, font=font) or 0


def _wrap(text: str, font, max_width: int) -> list[str]:
    from PIL import ImageDraw, Image

    probe = Image.new("RGB", (max(1, max_width), 8), "white")
    draw = ImageDraw.Draw(probe)
    char_w = max(1, int(draw.textlength("汉", font=font) or 1))
    max_chars = max(1, int(max_width / char_w))
    lines: list[str] = []
    for para in (text or "").splitlines() or [""]:
        lines.extend(cjk_wrap.wrap_cjk_columns(para, max_chars) or [""])
    return lines or [""]


def _merge_adjacent_phrases(paras: list[str], max_cols: int) -> list[str]:
    """Too many newline columns: glue neighboring phrases, do not reflow 还 onto 对不起."""
    merged = [part for part in paras if part]
    while len(merged) > max(1, max_cols) and len(merged) >= 2:
        best = 0
        best_len = len(merged[0]) + len(merged[1])
        for i in range(1, len(merged) - 1):
            n = len(merged[i]) + len(merged[i + 1])
            if n < best_len:
                best, best_len = i, n
        merged[best] = merged[best] + merged[best + 1]
        del merged[best + 1]
    return merged


def _fit_vertical_columns(
    paras: list[str], compact: str, rows: int, max_cols: int
) -> list[str]:
    """Honor translation newlines only when those columns still fit the balloon."""
    columns: list[str] = []
    for para in paras:
        columns.extend(cjk_wrap.wrap_cjk_columns(para, rows) or [])
    if 1 <= len(columns) <= max_cols:
        return columns
    columns = []
    for para in _merge_adjacent_phrases(paras, max_cols):
        columns.extend(cjk_wrap.wrap_cjk_columns(para, rows) or [])
    if 1 <= len(columns) <= max_cols:
        return columns
    per = max(rows, (len(compact) + max_cols - 1) // max_cols)
    return cjk_wrap.wrap_cjk_columns(compact, per) or ([compact] if compact else [])


def _short_dest_in_wide_balloon(n: int, inner_w: int, inner_h: int) -> bool:
    """True when dest fits in one column and leaves unused balloon width.

    Wide 3-column JP balloons with a 6-char translation must not be stretched
    flush to the detector. Narrow one-column balloons still use the full height.
    """
    if n <= 0 or inner_w <= 0 or inner_h <= 0:
        return False
    comfort = max(16, min(32, int(inner_w * 0.35)))
    one_w = max(comfort, int(comfort * _VERTICAL_COL_STRIDE))
    one_h = n * max(1, int(comfort * _VERTICAL_COL_HEIGHT))
    return one_w <= int(inner_w * 0.55) and one_h <= int(inner_h * 0.9)


def choose_vertical_overlay_layout(
    inner_w: int, inner_h: int, body: str
) -> tuple[int, list[str]]:
    """Largest vertical CJK size that still fits. Start from column width, not
    inner_h/len*2 — that floor made long balloons look tiny next to short ones."""
    paras = [
        "".join(part.split())
        for part in str(body or "").split("\n")
        if "".join(str(part or "").split())
    ]
    compact = "".join(paras)
    if not compact:
        return 14, []
    n = len(compact)
    start = max(16, min(int(inner_w), int(inner_h)))
    start = min(start, int(inner_w * 0.95), int(inner_h * 0.9))
    short = _short_dest_in_wide_balloon(n, inner_w, inner_h)
    if short:
        start = min(
            start,
            max(16, int(inner_h * 0.72 / max(n, 1) / _VERTICAL_COL_HEIGHT)),
            max(16, int(inner_w * 0.42)),
        )
    for size in range(start, 13, -1):
        col_h = max(1, int(size * _VERTICAL_COL_HEIGHT))
        col_w = max(size, int(size * _VERTICAL_COL_STRIDE))
        rows = max(1, inner_h // col_h)
        max_cols = max(1, inner_w // col_w)
        columns = _fit_vertical_columns(paras, compact, rows, max_cols)
        if not columns or len(columns) > max_cols:
            continue
        if short and len(columns) > 1:
            continue
        packed = len(columns) * col_w
        if packed > inner_w:
            continue
        if max(len(col) * col_h for col in columns) > inner_h:
            continue
        if len(columns) >= 2 and packed >= int(inner_w * 0.9):
            continue
        return size, columns
    return 14, [compact]


def choose_vertical_overlay_size(inner_w: int, inner_h: int, body: str) -> int:
    size, _ = choose_vertical_overlay_layout(inner_w, inner_h, body)
    return size


_INK_MAX = 110
_INK_CONTRAST = 40


def overlay_ink_bbox(image, box: list[int]) -> list[int] | None:
    """Glyph bbox inside `box`. Uses border contrast so white-on-red titles count."""
    x1, y1, x2, y2 = (int(v) for v in box)
    if x2 - x1 < 4 or y2 - y1 < 4:
        return None
    crop = image.crop((x1, y1, x2, y2)).convert("L")
    width, height = crop.size
    border: list[int] = []
    for x in range(width):
        border.append(crop.getpixel((x, 0)))
        border.append(crop.getpixel((x, height - 1)))
    for y in range(1, height - 1):
        border.append(crop.getpixel((0, y)))
        border.append(crop.getpixel((width - 1, y)))
    border.sort()
    bg = border[len(border) // 2]
    mask = crop.point(lambda p, base=bg: 255 if abs(p - base) >= _INK_CONTRAST else 0)
    found = mask.getbbox()
    if not found:
        return None
    return [x1 + found[0], y1 + found[1], x1 + found[2], y1 + found[3]]


def overlay_light_ink_bbox(image, box: list[int]) -> list[int] | None:
    """White glyphs on a dark title. Ignores mid-tone illustration in the same box."""
    x1, y1, x2, y2 = (int(v) for v in box)
    if x2 - x1 < 4 or y2 - y1 < 4:
        return None
    crop = image.crop((x1, y1, x2, y2)).convert("L")
    width, height = crop.size
    border: list[int] = []
    for x in range(width):
        border.append(crop.getpixel((x, 0)))
        border.append(crop.getpixel((x, height - 1)))
    for y in range(1, height - 1):
        border.append(crop.getpixel((0, y)))
        border.append(crop.getpixel((width - 1, y)))
    border.sort()
    bg = border[len(border) // 2]
    floor = max(bg + _INK_CONTRAST, 180)
    mask = crop.point(lambda p, limit=floor: 255 if p >= limit else 0)
    found = mask.getbbox()
    if not found:
        return None
    return [x1 + found[0], y1 + found[1], x1 + found[2], y1 + found[3]]


def _box_area(box: list[int]) -> int:
    return max(0, int(box[2]) - int(box[0])) * max(0, int(box[3]) - int(box[1]))


def _median_luma(gray, box: list[int]) -> int:
    x1, y1, x2, y2 = (int(v) for v in box)
    if x2 <= x1 or y2 <= y1:
        return 0
    crop = gray.crop((x1, y1, x2, y2))
    hist = crop.histogram()
    total = sum(hist[:256])
    if total <= 0:
        return 0
    acc = 0
    for i, count in enumerate(hist[:256]):
        acc += count
        if acc * 2 >= total:
            return i
    return 0


def _median_rgb(image, box: list[int]) -> tuple[int, int, int]:
    x1, y1, x2, y2 = (int(v) for v in box)
    crop = image.convert("RGB").crop((x1, y1, max(x1 + 1, x2), max(y1 + 1, y2)))
    out: list[int] = []
    for channel in crop.split():
        hist = channel.histogram()
        total = sum(hist)
        acc = 0
        value = 255
        for i, count in enumerate(hist):
            acc += count
            if total and acc * 2 >= total:
                value = i
                break
        out.append(value)
    return (out[0], out[1], out[2]) if len(out) == 3 else (255, 255, 255)


def overlay_is_dark(image, box: list[int]) -> bool:
    gray = image.convert("L")
    return _median_luma(gray, box) < 120


def _grow_white_balloon_box(
    image, box: list[int], max_extra: int = 28, step: int = 4
) -> list[int]:
    """Expand a detector so outlined rim glyphs still sit inside the wipe box."""
    x1, y1, x2, y2 = (int(v) for v in box)
    gray = image.convert("L")
    width, height = gray.size
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(width, max(x1 + 1, x2)), min(height, max(y1 + 1, y2))

    def strip_is_balloon(rect: list[int]) -> bool:
        if rect[2] <= rect[0] or rect[3] <= rect[1]:
            return False
        return white_fraction(image, rect) >= 0.28 or _median_luma(gray, rect) >= 200

    extra_l = extra_r = extra_t = extra_b = 0
    while extra_l < max_extra:
        nxt = x1 - extra_l - step
        if nxt < 0 or not strip_is_balloon([nxt, y1, x1 - extra_l, y2]):
            break
        extra_l += step
    while extra_r < max_extra:
        nxt = x2 + extra_r + step
        if nxt > width or not strip_is_balloon([x2 + extra_r, y1, nxt, y2]):
            break
        extra_r += step
    while extra_t < max_extra:
        nxt = y1 - extra_t - step
        if nxt < 0 or not strip_is_balloon([x1, nxt, x2, y1 - extra_t]):
            break
        extra_t += step
    while extra_b < max_extra:
        nxt = y2 + extra_b + step
        if nxt > height or not strip_is_balloon([x1, y2 + extra_b, x2, nxt]):
            break
        extra_b += step
    return [x1 - extra_l, y1 - extra_t, x2 + extra_r, y2 + extra_b]


def grow_overlay_box(
    image, box: list[int], max_extra: int = 80, step: int = 4, light: int = 190
) -> list[int]:
    """Grow a thin glyph column into the surrounding white balloon."""
    x1, y1, x2, y2 = (int(v) for v in box)
    w, h = max(1, x2 - x1), max(1, y2 - y1)
    ink = overlay_ink_bbox(image, [x1, y1, x2, y2])
    if (
        balloon_is_white(image, [x1, y1, x2, y2])
        and ink
        and not (h >= int(2.2 * w) and w <= 90)
    ):
        touch = 0
        if ink[0] <= x1 + 8:
            touch += 1
        if ink[1] <= y1 + 8:
            touch += 1
        if ink[2] >= x2 - 8:
            touch += 1
        if ink[3] >= y2 - 8:
            touch += 1
        if touch >= 3:
            return _grow_white_balloon_box(image, [x1, y1, x2, y2])
    if not (h >= int(2.2 * w) and w <= 90):
        return [x1, y1, x2, y2]
    gray = image.convert("L")
    width, height = gray.size
    x1, y1 = max(0, x1), max(0, y1)
    x2, y2 = min(width, max(x1 + 1, x2)), min(height, max(y1 + 1, y2))

    def strip_ok(rect: list[int]) -> bool:
        if rect[2] <= rect[0] or rect[3] <= rect[1]:
            return False
        return _median_luma(gray, rect) >= light

    extra_l = extra_r = extra_t = extra_b = 0
    while extra_l < max_extra:
        nxt = x1 - extra_l - step
        if nxt < 0 or not strip_ok([nxt, y1, x1 - extra_l, y2]):
            break
        extra_l += step
    while extra_r < max_extra:
        nxt = x2 + extra_r + step
        if nxt > width or not strip_ok([x2 + extra_r, y1, nxt, y2]):
            break
        extra_r += step
    while extra_t < max_extra:
        nxt = y1 - extra_t - step
        if nxt < 0 or not strip_ok([x1, nxt, x2, y1 - extra_t]):
            break
        extra_t += step
    while extra_b < max_extra:
        nxt = y2 + extra_b + step
        if nxt > height or not strip_ok([x1, y2 + extra_b, x2, nxt]):
            break
        extra_b += step
    return [x1 - extra_l, y1 - extra_t, x2 + extra_r, y2 + extra_b]


def _grow_to_outside_ink(
    image, box: list[int], max_extra: int = 48
) -> list[int]:
    """Include leftover letters just outside the detector. Skip balloon strokes."""
    x1, y1, x2, y2 = (int(v) for v in box)
    gray = image.convert("L")
    width, height = gray.size
    padded = [
        max(0, x1 - max_extra),
        max(0, y1 - max_extra),
        min(width, x2 + max_extra),
        min(height, y2 + max_extra),
    ]

    def rim_letters(rect: list[int], horizontal: bool) -> bool:
        if rect[2] <= rect[0] or rect[3] <= rect[1]:
            return False
        if white_fraction(image, rect) < 0.12 and _median_luma(gray, rect) < 160:
            return False
        ink = overlay_ink_bbox(image, rect)
        if not ink:
            return False
        if horizontal:
            span = max(1, rect[2] - rect[0])
            return (ink[2] - ink[0]) <= int(span * 0.7)
        span = max(1, rect[3] - rect[1])
        return (ink[3] - ink[1]) <= int(span * 0.7)

    grown = [x1, y1, x2, y2]
    left = [padded[0], y1, x1, y2]
    right = [x2, y1, padded[2], y2]
    top = [x1, padded[1], x2, y1]
    bottom = [x1, y2, x2, padded[3]]
    if padded[0] < x1 and rim_letters(left, False):
        ink = overlay_ink_bbox(image, left)
        if ink:
            grown[0] = min(grown[0], ink[0])
    if padded[2] > x2 and rim_letters(right, False):
        ink = overlay_ink_bbox(image, right)
        if ink:
            grown[2] = max(grown[2], ink[2])
    if padded[1] < y1 and rim_letters(top, True):
        ink = overlay_ink_bbox(image, top)
        if ink:
            grown[1] = min(grown[1], ink[1])
    if padded[3] > y2 and rim_letters(bottom, True):
        ink = overlay_ink_bbox(image, bottom)
        if ink:
            grown[3] = max(grown[3], ink[3])
    return grown


def typeset_cramped(image, box: list[int]) -> bool:
    """True when MIT/OCR used a thin glyph column inside a much larger balloon."""
    grown = grow_overlay_box(image, box)
    return _box_area(grown) >= int(_box_area(box) * 1.8)


def typeset_undersized(image, box: list[int], dest: str) -> bool:
    """True when overlay can paint dest much larger than MIT's current ink column."""
    body = "".join(str(dest or "").split())
    if len(body) < 4 or not box or len(box) < 4 or image is None:
        return False
    x1, y1, x2, y2 = (int(v) for v in box)
    width, height = max(1, x2 - x1), max(1, y2 - y1)
    if height < width:
        return False
    pad = max(4, min(width, height) // 18)
    inner_w, inner_h = max(8, width - pad * 2), max(8, height - pad * 2)
    size, _ = choose_vertical_overlay_layout(inner_w, inner_h, dest)
    ink = overlay_ink_bbox(image, box)
    if not ink:
        return False
    mit_w = max(1, ink[2] - ink[0])
    return size >= 22 and size >= int(mit_w * 1.5)


def typeset_white_plate(source, out, box: list[int], dest: str) -> bool:
    """True when a white balloon was replaced by a rectangular plate around short dest."""
    body = "".join(str(dest or "").split())
    if source is None or out is None or len(body) < 2 or not box or len(box) < 4:
        return False
    if not balloon_is_white(source, box):
        return False
    x1, y1, x2, y2 = (int(v) for v in box)
    width, height = max(1, x2 - x1), max(1, y2 - y1)
    if height < width:
        return False
    jump = white_fraction(out, box) - white_fraction(source, box)
    if jump < 0.12:
        return False
    needed_h = len(body) * max(1, int(24 * _VERTICAL_COL_HEIGHT))
    return needed_h <= int(height * 0.85)


def _near_median_fraction(image, box: list[int], tol: int = 12) -> float:
    if image is None or not box or len(box) < 4:
        return 0.0
    crop = image.convert("L").crop(
        (int(box[0]), int(box[1]), max(int(box[0]) + 1, int(box[2])), max(int(box[1]) + 1, int(box[3])))
    )
    data = crop.tobytes()
    if not data:
        return 0.0
    med = sorted(data)[len(data) // 2]
    return sum(1 for value in data if abs(int(value) - med) <= tol) / len(data)


def typeset_art_plate(source, out, box: list[int]) -> bool:
    """True when MIT laid a flat card on screentone or SFX instead of glyphs."""
    if source is None or out is None or not box or len(box) < 4:
        return False
    if balloon_is_white(source, box):
        return False
    if region_mean_abs_diff(source, out, box) < 18:
        return False
    if white_fraction(out, box) - white_fraction(source, box) >= 0.12:
        return True
    src_gray = source.convert("L")
    out_gray = out.convert("L")
    return (
        _near_median_fraction(out, box) >= 0.55
        and _median_luma(out_gray, box) >= _median_luma(src_gray, box) + 40
    )


def leftover_source_ink_fraction(
    source, out, box: list[int], dark: int = 70, similar: int = 18
) -> float:
    """Among source glyphs, how many are still original ink in `out`."""
    if source is None or out is None or not box or len(box) < 4:
        return 0.0
    x1, y1, x2, y2 = (int(v) for v in box)
    if x2 <= x1 or y2 <= y1:
        return 0.0
    src = source.convert("L").crop((x1, y1, x2, y2))
    dst = out.convert("L").crop((x1, y1, x2, y2))
    if dst.size != src.size:
        dst = dst.resize(src.size)
    sa, sb = src.tobytes(), dst.tobytes()
    med = _median_luma(src, [0, 0, src.size[0], src.size[1]])
    ink = left = 0
    if med < 160:
        for pa, pb in zip(sa, sb):
            if pa < 180:
                continue
            ink += 1
            if pb >= 180 and abs(int(pa) - int(pb)) <= similar:
                left += 1
    else:
        for pa, pb in zip(sa, sb):
            if pa > dark:
                continue
            ink += 1
            if pb <= dark and abs(int(pa) - int(pb)) <= similar:
                left += 1
    if ink < 40:
        return 0.0
    return left / ink


def leftover_ink_bbox(
    source, out, box: list[int], dark: int = 70, similar: int = 18
) -> list[int] | None:
    """Glyph bbox of original dark-on-light ink MIT did not replace."""
    if leftover_source_ink_fraction(source, out, box, dark, similar) < 0.12:
        return None
    x1, y1, x2, y2 = (int(v) for v in box)
    src = source.convert("L").crop((x1, y1, x2, y2))
    dst = out.convert("L").crop((x1, y1, x2, y2))
    if dst.size != src.size:
        dst = dst.resize(src.size)
    sa, sb = src.tobytes(), dst.tobytes()
    width = src.size[0]
    min_x, min_y, max_x, max_y = src.size[0], src.size[1], -1, -1
    for i, (pa, pb) in enumerate(zip(sa, sb)):
        if pa > dark or pb > dark or abs(int(pa) - int(pb)) > similar:
            continue
        x, y = i % width, i // width
        if x < min_x:
            min_x = x
        if y < min_y:
            min_y = y
        if x > max_x:
            max_x = x
        if y > max_y:
            max_y = y
    if max_x < min_x:
        return None
    return [x1 + min_x, y1 + min_y, x1 + max_x + 1, y1 + max_y + 1]


def region_mean_abs_diff(source, out, box: list[int]) -> float:
    from PIL import ImageChops, ImageStat

    a = source.convert("L").crop((box[0], box[1], max(box[0] + 1, box[2]), max(box[1] + 1, box[3])))
    b = out.convert("L").crop((box[0], box[1], max(box[0] + 1, box[2]), max(box[1] + 1, box[3])))
    if b.size != a.size:
        b = b.resize(a.size)
    return float(ImageStat.Stat(ImageChops.difference(a, b)).mean[0])


def white_fraction(image, box: list[int]) -> float:
    crop = image.convert("L").crop(
        (box[0], box[1], max(box[0] + 1, box[2]), max(box[1] + 1, box[3]))
    )
    hist = crop.histogram()[:256]
    total = sum(hist) or 1
    return sum(hist[240:]) / total


def _background_probe_points(image, box: list[int]) -> list[tuple[int, int]]:
    ink = overlay_ink_bbox(image, box)
    if not ink:
        cx = (int(box[0]) + int(box[2])) // 2
        cy = (int(box[1]) + int(box[3])) // 2
        return [(cx, cy)]
    x1, y1, x2, y2 = ink
    return [
        (x1 - 3, (y1 + y2) // 2),
        (x2 + 2, (y1 + y2) // 2),
        ((x1 + x2) // 2, y1 - 3),
        ((x1 + x2) // 2, y2 + 2),
    ]


def text_background_luma(image, box: list[int]) -> int:
    """Luma next to the glyphs. White balloons ~250; screentone ~160; red TOC ~40."""
    gray = image.convert("L")
    samples: list[int] = []
    for x, y in _background_probe_points(image, box):
        if box[0] <= x < box[2] and box[1] <= y < box[3]:
            samples.append(gray.getpixel((int(x), int(y))))
    if not samples:
        return _median_luma(gray, box)
    samples.sort()
    return samples[len(samples) // 2]


def text_background_is_dark(image, box: list[int]) -> bool:
    """Luma next to the glyphs. White balloons stay light; red TOC titles count as dark."""
    return text_background_luma(image, box) < 120


def balloon_is_white(image, box: list[int]) -> bool:
    """True only for near-white speech balloons, not gray screentone."""
    return text_background_luma(image, box) >= 230


def newline_columns_collide(box: list[int], dest: str) -> bool:
    """True when translation newlines would pack vertical columns with no gutter."""
    parts = [
        "".join(part.split())
        for part in str(dest or "").split("\n")
        if "".join(str(part or "").split())
    ]
    if len(parts) < 2 or not box or len(box) < 4:
        return False
    x1, y1, x2, y2 = (int(v) for v in box)
    inner_w = max(8, x2 - x1 - 8)
    inner_h = max(8, y2 - y1 - 8)
    if inner_h < inner_w:
        return False
    max_len = max(len(part) for part in parts)
    size = min(int(inner_w * 0.95), int(inner_h / max(1, max_len) / _VERTICAL_COL_HEIGHT))
    if size < 14:
        return True
    return len(parts) * int(size * _VERTICAL_COL_STRIDE) >= inner_w - 4


def _overlay_probe_box(image, box: list[int]) -> list[int]:
    """Tight ink detectors sit inside a balloon; grow so leftover uses balloon luma."""
    if image is None or not box or len(box) < 4:
        return box
    if balloon_is_white(image, box):
        return box
    grown = grow_overlay_box(image, box)
    if balloon_is_white(image, grown):
        return grown
    return box


def needs_overlay(
    ocr_text: str,
    box: list[int],
    out=None,
    source=None,
    score: float = 1.0,
    dest: str | None = None,
) -> bool:
    """Overlay leftover Japanese, TOC, or MIT plates. Do not restyle a clean Lama balloon."""
    if skip_as_art(ocr_text, box) or skip_low_score_overlay(score, ocr_text):
        return False
    if out is None:
        return True
    src_img = source if source is not None else out
    if is_contents_entry(ocr_text):
        return True
    probe = _overlay_probe_box(src_img, box)
    leftover = leftover_source_ink_fraction(src_img, out, probe)
    if text_background_luma(src_img, probe) < 120:
        if leftover >= 0.12:
            return True
        return white_fraction(out, probe) - white_fraction(src_img, probe) >= 0.12
    if balloon_is_white(src_img, probe):
        if leftover >= 0.12:
            return True
        if dest and typeset_white_plate(src_img, out, probe, dest):
            return True
        return False
    if typeset_art_plate(src_img, out, box):
        return True
    # Original chalkboard / SFX glyphs still showing: overlay would stamp Chinese on art.
    return False


def restore_untranslated_art(page, source, box: list[int], src: str, known: list[str], mapping: dict) -> bool:
    """Undo overlay paint on logos/OCR garbage MIT never typeset."""
    if source is None or page is None or not box or len(box) < 4:
        return False
    dst = lookup_translation(mapping, src)
    if local_qwen.translation_ok(src, dst) and not skip_as_art(src, box):
        return False
    if sidecar_covers(src, known):
        return False
    if _box_area(box) >= HUGE_BOX_AREA:
        return False
    if region_mean_abs_diff(source, page, box) < 18:
        return False
    x1, y1, x2, y2 = (int(v) for v in box)
    page.paste(source.crop((x1, y1, x2, y2)), (x1, y1))
    return True


def restore_overlay_on_art(page, source, box: list[int], src: str) -> bool:
    """Undo stacked Chinese on chalkboard/SFX MIT left as original art."""
    if source is None or page is None or not box or len(box) < 4:
        return False
    if is_contents_entry(src) or balloon_is_white(source, box):
        return False
    if text_background_luma(source, box) < 120:
        return False
    if _box_area(box) >= HUGE_BOX_AREA:
        return False
    if leftover_source_ink_fraction(source, page, box) < 0.12:
        return False
    x1, y1, x2, y2 = _clip_box(
        [int(v) for v in box],
        [0, 0, page.size[0], page.size[1]],
    )
    if x2 <= x1 or y2 <= y1:
        return False
    page.paste(source.crop((x1, y1, x2, y2)), (x1, y1))
    return True


def skip_low_score_overlay(score: float, src: str) -> bool:
    """Drop false balloons; keep short real dialogue like 痛くていう."""
    if not score or score >= 0.5:
        return False
    return local_qwen.suspicious_ocr(src) or len(_core(src)) <= 3


def fit_overlay_box_to_text(box: list[int], text: str) -> list[int]:
    """Short overlays must not be sized to an empty balloon; long lines keep the box."""
    body = "".join(str(text or "").split())
    n = max(1, len(body))
    x1, y1, x2, y2 = (int(v) for v in box)
    w, h = max(1, x2 - x1), max(1, y2 - y1)
    vertical = h >= w
    if n >= 8 and not _short_dest_in_wide_balloon(n, w, h):
        return [x1, y1, x2, y2]
    if vertical:
        max_ch = int(h * 0.72) if _short_dest_in_wide_balloon(n, w, h) else h
        em = min(int(w * 0.45), 32, max(16, w - 8))
        ch = int(em * n * _VERTICAL_COL_HEIGHT) + 8
        if ch > max_ch:
            em = max(16, int((max_ch - 8) / max(n, 1) / _VERTICAL_COL_HEIGHT))
            ch = min(h, int(em * n * _VERTICAL_COL_HEIGHT) + 8)
        cw = min(w, max(16, int(em * 1.45)))
        ch = min(h, max(16, ch))
        cx = x1 + (w - cw) // 2
        cy = y1 + (h - ch) // 2
        return [cx, cy, cx + cw, cy + ch]
    em = min(h, max(1, w // max(n, 1)), 48)
    cw = min(w, max(16, int(em * n * 1.15)))
    ch = min(h, max(16, int(em * 1.4)))
    cx = x1 + (w - cw) // 2
    cy = y1 + (h - ch) // 2
    return [cx, cy, cx + cw, cy + ch]


def tighten_overlay_box(image, box: list[int]) -> list[int]:
    """Shrink a balloon detector box to remaining Japanese ink when the box is mostly empty."""
    x1, y1, x2, y2 = (int(v) for v in box)
    ink = overlay_ink_bbox(image, [x1, y1, x2, y2])
    if not ink:
        return [x1, y1, x2, y2]
    ix1, iy1, ix2, iy2 = ink
    w, h = max(1, x2 - x1), max(1, y2 - y1)
    iw, ih = max(1, ix2 - ix1), max(1, iy2 - iy1)
    if iw * ih > w * h * 0.7 and iw > w * 0.75 and ih > h * 0.75:
        return [x1, y1, x2, y2]
    pad = max(3, min(iw, ih) // 8)
    return [
        max(x1, ix1 - pad),
        max(y1, iy1 - pad),
        min(x2, ix2 + pad),
        min(y2, iy2 + pad),
    ]


def _clip_box(box: list[int], limit: list[int]) -> list[int]:
    return [
        max(limit[0], box[0]),
        max(limit[1], box[1]),
        min(limit[2], box[2]),
        min(limit[3], box[3]),
    ]


def _union_box(a: list[int], b: list[int]) -> list[int]:
    return [min(a[0], b[0]), min(a[1], b[1]), max(a[2], b[2]), max(a[3], b[3])]


def _background_rgb(image, box: list[int]) -> tuple[int, int, int]:
    rgb = image.convert("RGB")
    samples: list[tuple[int, int, int]] = []
    for x, y in _background_probe_points(image, box):
        if box[0] <= x < box[2] and box[1] <= y < box[3]:
            samples.append(rgb.getpixel((int(x), int(y))))
    if not samples:
        return _median_rgb(image, box)
    samples.sort()
    return samples[len(samples) // 2]


def _cover_overlay_ink(draw, orig: list[int], image, source, color) -> None:
    leftover = leftover_ink_bbox(source, image, orig) if image is not None else None
    ink = overlay_ink_bbox(image, orig) if image is not None else None
    cover = leftover or ink
    if not cover:
        return
    pad = max(1, min(cover[2] - cover[0], cover[3] - cover[1]) // 16)
    fill = _clip_box(
        [cover[0] - pad, cover[1] - pad, cover[2] + pad, cover[3] + pad],
        orig,
    )
    draw.rectangle(fill, fill=color)


def _fill_overlay_background(
    draw, orig: list[int], tightened: list[int], text_box: list[int], image=None, source=None
) -> None:
    pad = max(3, min(max(8, text_box[2] - text_box[0]), max(8, text_box[3] - text_box[1])) // 10)
    fill = [
        text_box[0] - pad,
        text_box[1] - pad,
        text_box[2] + pad,
        text_box[3] + pad,
    ]
    bg_img = source if source is not None else image
    luma = text_background_luma(bg_img, orig) if bg_img is not None else 255
    ink = overlay_ink_bbox(image, orig) if image is not None else None
    if luma < 120:
        bg = _background_rgb(bg_img, orig) if bg_img is not None else (40, 40, 40)
        draw.rectangle(_clip_box(orig, orig), fill=bg)
        return
    if luma < 230:
        color = _background_rgb(bg_img, orig) if bg_img is not None else (160, 160, 160)
        _cover_overlay_ink(draw, orig, image, bg_img, color)
        return
    leftover = leftover_ink_bbox(bg_img, image, orig) if image is not None else None
    cover = ink
    if leftover and (cover is None or overlap_frac(leftover, orig) >= 0.05):
        cover = leftover if cover is None else _union_box(cover, leftover)
    if cover and overlap_frac(cover, fill) >= 0.15:
        ipad = max(2, min(cover[2] - cover[0], cover[3] - cover[1]) // 12)
        fill = _union_box(
            fill,
            [cover[0] - ipad, cover[1] - ipad, cover[2] + ipad, cover[3] + ipad],
        )
    fill = _clip_box(fill, orig)
    fw, fh = max(1, fill[2] - fill[0]), max(1, fill[3] - fill[1])
    draw.rounded_rectangle(
        fill, radius=min(16, fw, fh) // 6, fill="white"
    )


_CONTENTS_MARK_RE = re.compile(r"〈第.+[話话]〉|〈最終[話话]〉|〈最终话〉")


def is_contents_entry(text: str) -> bool:
    """Manga contents lines: page mark plus 〈第N话〉. Not a book-specific title."""
    return bool(_CONTENTS_MARK_RE.search(str(text or "")))


def close_split_gaps(
    parts: list[tuple[str, list[int]]], limit: int = 80
) -> list[tuple[str, list[int]]]:
    if len(parts) < 2:
        return parts
    ordered = sorted(parts, key=lambda item: item[1][1])
    boxes = [list(item[1]) for item in ordered]
    for i in range(len(boxes) - 1):
        gap = boxes[i + 1][1] - boxes[i][3]
        if 0 < gap <= limit:
            mid = boxes[i][3] + gap // 2
            boxes[i][3] = mid
            boxes[i + 1][1] = mid
    return [(ordered[i][0], boxes[i]) for i in range(len(ordered))]


def overlay_layout_box(image, box: list[int]) -> list[int]:
    """Render inside the balloon. Keep page-sized detectors clipped to ink."""
    raw = [int(v) for v in box]
    ink = overlay_ink_bbox(image, raw) or raw
    grown = grow_overlay_box(image, ink)
    fitted = grown if _box_area(grown) >= _box_area(ink) else ink
    fitted = _clip_box(fitted, raw)
    raw_area = _box_area(raw)
    if raw_area < HUGE_BOX_AREA:
        return raw
    return fitted


def _restore_balloon(image, source, box: list[int]) -> None:
    if source is None or image is None or not box or len(box) < 4:
        return
    x1, y1, x2, y2 = _clip_box(
        [int(v) for v in box],
        [0, 0, image.size[0], image.size[1]],
    )
    if x2 <= x1 or y2 <= y1:
        return
    image.paste(source.crop((x1, y1, x2, y2)), (x1, y1))


def _flood_mask(
    width: int,
    height: int,
    allowed: list[bool],
    seeds: list[tuple[int, int]],
) -> list[bool]:
    reached = [False] * (width * height)
    queue: deque[tuple[int, int]] = deque()

    def mark(x: int, y: int) -> None:
        i = y * width + x
        if not allowed[i] or reached[i]:
            return
        reached[i] = True
        queue.append((x, y))

    for x, y in seeds:
        if 0 <= x < width and 0 <= y < height:
            mark(x, y)
    while queue:
        x, y = queue.popleft()
        if x > 0:
            mark(x - 1, y)
        if x + 1 < width:
            mark(x + 1, y)
        if y > 0:
            mark(x, y - 1)
        if y + 1 < height:
            mark(x, y + 1)
    return reached


def _inpaint_white_balloon_holes(crop, luma: bytes, pixels: list[tuple[int, int, int]]):
    """Fill interior glyph holes with balloon white. Keep outline and outside art."""
    width, height = crop.size
    n = width * height
    white = 225
    is_white = [value >= white for value in luma]
    nonwhite = [not bit for bit in is_white]
    border_seeds = (
        [(x, 0) for x in range(width)]
        + [(x, height - 1) for x in range(width)]
        + [(0, y) for y in range(height)]
        + [(width - 1, y) for y in range(height)]
    )
    border_dark = _flood_mask(width, height, nonwhite, border_seeds)
    cx, cy = width // 2, height // 2
    seed = (cx, cy)
    if not is_white[cy * width + cx]:
        found = None
        for radius in range(1, max(width, height) // 2):
            for dx, dy in ((radius, 0), (-radius, 0), (0, radius), (0, -radius)):
                x, y = cx + dx, cy + dy
                if 0 <= x < width and 0 <= y < height and is_white[y * width + x]:
                    found = (x, y)
                    break
            if found:
                seed = found
                break
        else:
            seed = None
    interior = (
        _flood_mask(width, height, is_white, [seed]) if seed is not None else [False] * n
    )
    if sum(1 for bit in interior if bit) < max(40, n // 8):
        interior = is_white
    outside = _flood_mask(
        width, height, [not bit for bit in interior], border_seeds
    )
    holes = [not interior[i] and not outside[i] for i in range(n)]
    whites = [pixels[i] for i, bit in enumerate(is_white) if bit]
    fill = whites[len(whites) // 2] if whites else (250, 250, 250)
    out = list(pixels)
    filled = 0
    radius = 4
    need = 16
    for y in range(height):
        row = y * width
        for x in range(width):
            i = row + x
            value = luma[i]
            if holes[i]:
                out[i] = fill
                filled += 1
                continue
            if value >= white:
                continue
            if not border_dark[i]:
                out[i] = fill
                filled += 1
                continue
            edge = min(x, y, width - 1 - x, height - 1 - y)
            if edge < 3:
                continue
            nearby = 0
            has_ext = False
            y0, y1 = max(0, y - radius), min(height, y + radius + 1)
            x0, x1 = max(0, x - radius), min(width, x + radius + 1)
            for yy in range(y0, y1):
                rr = yy * width
                for xx in range(x0, x1):
                    if interior[rr + xx]:
                        nearby += 1
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    if dx == 0 and dy == 0:
                        continue
                    nx, ny = x + dx, y + dy
                    if nx < 0 or ny < 0 or nx >= width or ny >= height:
                        has_ext = True
                        continue
                    ni = ny * width + nx
                    if outside[ni]:
                        has_ext = True
            # Rim outlined letters sit on the balloon stroke so they flood
            # as border_dark; still wipe them when they are inset and
            # surrounded by balloon white. True outline stays near the crop edge.
            if nearby >= need and (not has_ext or edge >= 6):
                out[i] = fill
                filled += 1
    if filled < 20:
        return None
    return out


_INPAINT_OFFSETS = (
    (-1, 0),
    (1, 0),
    (0, -1),
    (0, 1),
    (-1, -1),
    (1, -1),
    (-1, 1),
    (1, 1),
    (-2, 0),
    (2, 0),
    (0, -2),
    (0, 2),
    (-3, 0),
    (3, 0),
    (0, -3),
    (0, 3),
    (-4, 0),
    (4, 0),
    (0, -4),
    (0, 4),
    (-6, 0),
    (6, 0),
    (0, -6),
    (0, 6),
    (-8, 0),
    (8, 0),
    (0, -8),
    (0, 8),
    (-12, 0),
    (12, 0),
    (0, -12),
    (0, 12),
)


def _inpaint_contrast_glyphs(crop, luma: bytes, pixels: list[tuple[int, int, int]]):
    """Replace glyphs that contrast with the crop border, light or dark."""
    width, height = crop.size
    if width < 4 or height < 4 or not luma:
        return None
    border: list[int] = []
    last = height - 1
    for x in range(width):
        border.append(luma[x])
        border.append(luma[last * width + x])
    for y in range(1, last):
        border.append(luma[y * width])
        border.append(luma[y * width + width - 1])
    border.sort()
    bg = border[len(border) // 2]
    contrast = max(_INK_CONTRAST * 2, 80)
    bg_rgb = [
        pixels[i] for i, value in enumerate(luma) if abs(int(value) - bg) < contrast
    ]
    fallback = bg_rgb[len(bg_rgb) // 2] if bg_rgb else pixels[0]
    out = list(pixels)
    filled = 0
    for i, value in enumerate(luma):
        if abs(int(value) - bg) < contrast:
            continue
        x, y = i % width, i // width
        if min(x, y, width - 1 - x, height - 1 - y) < 2:
            continue
        found = None
        for dx, dy in _INPAINT_OFFSETS:
            nx, ny = x + dx, y + dy
            if nx < 0 or ny < 0 or nx >= width or ny >= height:
                continue
            if abs(int(luma[ny * width + nx]) - bg) < contrast:
                found = pixels[ny * width + nx]
                break
        out[i] = found or fallback
        filled += 1
    if filled < 8:
        return None
    return out


def _inpaint_dilated_light(
    crop, luma: bytes, pixels: list[tuple[int, int, int]], floor: int = 180, radius: int = 8
):
    """Wipe white-on-dark title glyphs, including anti-aliased rims. Skip mid-tone art."""
    width, height = crop.size
    if width < 4 or height < 4 or not luma:
        return None
    core = [int(value) >= floor for value in luma]
    if not any(core):
        return None
    marked = list(core)
    if radius > 0:
        for i, on in enumerate(core):
            if not on:
                continue
            x, y = i % width, i // width
            for dy in range(-radius, radius + 1):
                ny = y + dy
                if ny < 0 or ny >= height:
                    continue
                span = radius - abs(dy)
                row = ny * width
                x0 = max(0, x - span)
                x1 = min(width - 1, x + span)
                for nx in range(x0, x1 + 1):
                    marked[row + nx] = True
    bg_pixels = [pixels[i] for i, hit in enumerate(marked) if not hit]
    if not bg_pixels:
        return None
    fallback = bg_pixels[len(bg_pixels) // 2]
    out = list(pixels)
    filled = 0
    for i, hit in enumerate(marked):
        if not hit:
            continue
        x, y = i % width, i // width
        found = None
        for dx, dy in _INPAINT_OFFSETS:
            nx, ny = x + dx, y + dy
            if nx < 0 or ny < 0 or nx >= width or ny >= height:
                continue
            ni = ny * width + nx
            if not marked[ni] and int(luma[ni]) < floor:
                found = pixels[ni]
                break
        out[i] = found or fallback
        filled += 1
    if filled < 8:
        return None
    return out


def _inpaint_glyphs(image, source, box: list[int], dark: int = 110, light: int = 130) -> None:
    """Replace original glyphs with nearby balloon/screentone pixels. No plate."""
    if source is None or image is None or not box or len(box) < 4:
        return
    x1, y1, x2, y2 = _clip_box(
        [int(v) for v in box],
        [0, 0, image.size[0], image.size[1]],
    )
    if x2 <= x1 or y2 <= y1:
        return
    crop = source.convert("RGB").crop((x1, y1, x2, y2))
    gray = crop.convert("L")
    width, height = crop.size
    luma = gray.tobytes()
    pixels = list(crop.tobytes())
    pixels = [
        (pixels[i], pixels[i + 1], pixels[i + 2])
        for i in range(0, len(pixels), 3)
    ]
    holes = None
    median = sorted(luma)[len(luma) // 2] if luma else 0
    if balloon_is_white(source, [x1, y1, x2, y2]):
        holes = _inpaint_white_balloon_holes(crop, luma, pixels)
    elif text_background_luma(source, [x1, y1, x2, y2]) < 120:
        holes = _inpaint_dilated_light(crop, luma, pixels)
    if holes is None:
        contrast = _inpaint_contrast_glyphs(crop, luma, pixels)
        out = contrast if contrast is not None else list(pixels)
    else:
        out = holes
    crop.putdata(out)
    image.paste(crop, (x1, y1))


def _crop_rgb_luma(image, box: list[int]):
    x1, y1, x2, y2 = box
    crop = image.convert("RGB").crop((x1, y1, x2, y2))
    luma = crop.convert("L").tobytes()
    raw = crop.tobytes()
    pixels = [
        (raw[i], raw[i + 1], raw[i + 2]) for i in range(0, len(raw), 3)
    ]
    return crop, luma, pixels


def _restore_white_balloon_mit_extras(image, source, box: list[int]) -> None:
    """Copy source balloon white over MIT leftover columns. Never restore source ink."""
    if source is None or image is None or not box or len(box) < 4:
        return
    x1, y1, x2, y2 = _clip_box(
        [int(v) for v in box],
        [0, 0, image.size[0], image.size[1]],
    )
    if x2 <= x1 or y2 <= y1:
        return
    src_crop, src_luma, src_px = _crop_rgb_luma(source, [x1, y1, x2, y2])
    dst_crop, dst_luma, dst_px = _crop_rgb_luma(image, [x1, y1, x2, y2])
    out = list(dst_px)
    changed = False
    for i, (sa, da) in enumerate(zip(src_luma, dst_luma)):
        if sa >= 230 and da < 200:
            out[i] = src_px[i]
            changed = True
    if not changed:
        return
    dst_crop.putdata(out)
    image.paste(dst_crop, (x1, y1))


def _inpaint_current_white_holes(image, box: list[int]) -> None:
    """Wipe dark holes on the current MIT canvas. Do not paste the original page."""
    if image is None or not box or len(box) < 4:
        return
    x1, y1, x2, y2 = _clip_box(
        [int(v) for v in box],
        [0, 0, image.size[0], image.size[1]],
    )
    if x2 <= x1 or y2 <= y1:
        return
    crop, luma, pixels = _crop_rgb_luma(image, [x1, y1, x2, y2])
    holes = _inpaint_white_balloon_holes(crop, luma, pixels)
    if holes is None:
        return
    crop.putdata(holes)
    image.paste(crop, (x1, y1))


def render_translation(image, box: list[int], text: str, source=None) -> None:
    from PIL import ImageDraw

    body = local_qwen.repair_ocr_bang(str(text or ""))
    if not body.strip():
        return
    bg_img = source if source is not None else image
    raw = [int(v) for v in box]
    luma = text_background_luma(bg_img, raw)
    orig = overlay_layout_box(bg_img, raw)
    # Never lay a flat plate. White balloons keep MIT Lama; only leftover
    # source ink is wiped on the current canvas. Dark titles restore+inpaint.
    glyph_only = True
    light_ink = overlay_light_ink_bbox(bg_img, orig)
    if balloon_is_white(bg_img, orig):
        wipe = grow_overlay_box(bg_img, orig)
        _restore_white_balloon_mit_extras(image, bg_img, wipe)
        _inpaint_current_white_holes(image, wipe)
    elif luma < 120:
        _restore_balloon(image, bg_img, orig)
        _inpaint_glyphs(image, bg_img, orig)
    else:
        wipe = _clip_box(raw, [0, 0, image.size[0], image.size[1]])
        _restore_balloon(image, bg_img, wipe)
        _inpaint_glyphs(image, bg_img, wipe)
    if luma < 120:
        x1, y1, x2, y2 = orig
    else:
        x1, y1, x2, y2 = fit_overlay_box_to_text(
            tighten_overlay_box(image, orig), body
        )
    tightened = [x1, y1, x2, y2]
    width, height = max(8, x2 - x1), max(8, y2 - y1)
    pad = max(4, min(width, height) // 18)
    inner_w, inner_h = max(8, width - pad * 2), max(8, height - pad * 2)
    draw = ImageDraw.Draw(image)
    dark = luma < 120
    fill_color = (250, 250, 250) if dark else (20, 20, 20)
    stroke_w = 2 if dark else 0
    stroke_fill = (20, 20, 20)
    vertical = height >= width
    if vertical:
        size, columns = choose_vertical_overlay_layout(inner_w, inner_h, body)
        if columns:
            col_h = int(size * _VERTICAL_COL_HEIGHT)
            col_w = max(size, int(size * _VERTICAL_COL_STRIDE))
            cols = max(1, len(columns))
            block_w = cols * col_w
            block_h = max((len(col) * col_h) for col in columns)
            left0 = x1 + pad + max(0, (inner_w - block_w) // 2)
            top0 = y1 + pad + max(0, (inner_h - block_h) // 2)
            if not glyph_only:
                _fill_overlay_background(
                    draw,
                    orig,
                    tightened,
                    [left0, top0, left0 + block_w, top0 + block_h],
                    image,
                    bg_img,
                )
            for col_i, column in enumerate(columns):
                # manga columns: right to left
                cx = left0 + (cols - 1 - col_i) * col_w
                for row, char in enumerate(column):
                    cy = top0 + row * col_h
                    glyph_size = max(12, int(size * 0.72)) if char in _HEART_CHARS else size
                    font = _font(glyph_size, char)
                    bbox = draw.textbbox((0, 0), char, font=font)
                    gw, gh = bbox[2] - bbox[0], bbox[3] - bbox[1]
                    px = cx + max(0, (col_w - gw) // 2) - bbox[0]
                    py = cy + max(0, (col_h - gh) // 2) - bbox[1]
                    draw.text(
                        (px, py),
                        char,
                        font=font,
                        fill=fill_color,
                        stroke_width=stroke_w,
                        stroke_fill=stroke_fill,
                    )
            return
    else:
        start = max(16, min(inner_h, inner_w))
        start = min(start, int(inner_h * 0.9), int(inner_w * 0.95))
        for size in range(start, 13, -1):
            font = _font(size)
            lines = _wrap(body, font, inner_w)
            line_h = int(size * 1.25)
            block_h = line_h * len(lines)
            if block_h <= inner_h and all(
                _line_width(draw, line, size) <= inner_w + 1 for line in lines
            ):
                top = y1 + pad + max(0, (inner_h - block_h) // 2)
                widths = [_line_width(draw, line, size) for line in lines]
                block_w = max(widths) if widths else inner_w
                left_block = x1 + pad + max(0, (inner_w - block_w) // 2)
                if not glyph_only:
                    _fill_overlay_background(
                        draw,
                        orig,
                        tightened,
                        [left_block, top, left_block + int(block_w), top + block_h],
                        image,
                        bg_img,
                    )
                for i, line in enumerate(lines):
                    line_w = widths[i]
                    left = x1 + pad + max(0, (inner_w - line_w) // 2)
                    _draw_line(
                        draw,
                        left,
                        top + i * line_h,
                        line,
                        size,
                        fill=fill_color,
                        stroke_width=stroke_w,
                        stroke_fill=stroke_fill,
                    )
                return
    fallback = [x1 + pad, y1 + pad, x2 - pad, y2 - pad]
    if not glyph_only:
        _fill_overlay_background(draw, orig, tightened, fallback, image, bg_img)
    _draw_line(
        draw,
        x1 + pad,
        y1 + pad,
        body,
        14,
        fill=fill_color,
        stroke_width=stroke_w,
        stroke_fill=stroke_fill,
    )


def restore_source_art(work: Path, stems: list[str] | None = None) -> int:
    """Blit original pixels back for signatures and art MIT should not have painted."""
    from PIL import Image

    src_dir = work / "typeset_in"
    dest = work / "out"
    if not src_dir.is_dir() or not dest.is_dir():
        return 0
    stored = load_json(recall_path(work), {})
    if not isinstance(stored, dict):
        stored = {}
    restored = 0
    wanted = {item.lower() for item in stems} if stems else None
    for img in local_qwen.list_image_files(src_dir):
        if wanted and img.stem.lower() not in wanted and img.name.lower() not in wanted:
            continue
        out_img = None
        for suffix in local_qwen.IMAGE_SUFFIXES:
            candidate = dest / f"{img.stem}{suffix}"
            if candidate.is_file():
                out_img = candidate
                break
        if out_img is None:
            continue
        rows: list[tuple[str, list[int]]] = list(sidecar_labeled_boxes(work, img.stem))
        rec = stored.get(img.stem)
        regions = rec.get("regions") if isinstance(rec, dict) else None
        if isinstance(regions, list):
            for row in regions:
                if not isinstance(row, dict):
                    continue
                rows.append(
                    (str(row.get("text") or ""), row.get("xyxy") or [0, 0, 1, 1])
                )
        if not rows:
            continue
        original = Image.open(img).convert("RGB")
        page = Image.open(out_img).convert("RGB")
        changed = False
        pasted: list[list[int]] = []
        for text, raw_box in rows:
            box = clamp_box(raw_box, page.size[0], page.size[1])
            if any(_boxes_overlap(box, prev) for prev in pasted):
                continue
            pen = local_qwen.keep_source_art(text)
            if not pen:
                if is_contents_entry(text) or not skip_as_art(text, box):
                    continue
                if _box_area(box) >= HUGE_BOX_AREA:
                    continue
                if region_mean_abs_diff(original, page, box) < 18:
                    continue
                crop = box
            else:
                pad = 8
                crop = [
                    max(0, box[0] - pad),
                    max(0, box[1] - pad),
                    min(original.size[0], box[2] + pad),
                    min(original.size[1], box[3] + pad),
                ]
            page.paste(
                original.crop((crop[0], crop[1], crop[2], crop[3])),
                (crop[0], crop[1]),
            )
            pasted.append(box)
            changed = True
            restored += 1
        if changed:
            page.save(out_img)
            print(f"restore-art {img.stem} -> {out_img.name}", flush=True)
    return restored


def select_pages(work: Path, stems: list[str] | None) -> list[Path]:
    src = work / "typeset_in"
    images = local_qwen.list_image_files(src)
    if not stems:
        return images
    wanted = {item.lower() for item in stems}
    return [img for img in images if img.stem.lower() in wanted or img.name.lower() in wanted]


def recall_pages(work: Path, stems: list[str] | None = None) -> dict:
    from PIL import Image

    model = ensure_model()
    session = _session(model)
    ocr = _ocr_engine()
    mapping = load_json(work / "translations.json", {})
    if not isinstance(mapping, dict):
        mapping = {}
    stored = load_json(recall_path(work), {})
    if not isinstance(stored, dict):
        stored = {}
    pages = select_pages(work, stems)
    added = 0
    for img in pages:
        prev = stored.get(img.stem)
        if (
            not stems
            and isinstance(prev, dict)
            and prev.get("mtime") == img.stat().st_mtime
            and isinstance(prev.get("regions"), list)
        ):
            for row in prev["regions"]:
                text = str(row.get("text") or "")
                if text and not row.get("covered") and text not in mapping:
                    mapping[text] = ""
                    added += 1
            continue
        try:
            page = Image.open(img).convert("RGB")
        except OSError:
            print(f"recall skip unreadable {img.name}", flush=True)
            continue
        hits = detect_image(session, page)
        regions = []
        known = sidecar_texts(work, img.stem)
        for hit in dialogue_boxes(hits):
            crop = _crop(page, hit["xyxy"])
            text = local_qwen.repair_ocr_bang((ocr(crop) or "").strip())
            if not text or text in SKIP:
                continue
            covered = sidecar_covers(text, known)
            row = {
                "xyxy": hit["xyxy"],
                "text": text,
                "score": hit.get("score", 0),
                "covered": covered,
            }
            regions.append(row)
            if covered:
                continue
            if text not in mapping:
                mapping[text] = ""
                added += 1
            cleaned = local_qwen.repair_ocr_bang(text)
            if cleaned not in mapping:
                mapping[cleaned] = mapping.get(text, "")
                added += 1
        stored[img.stem] = {
            "mtime": img.stat().st_mtime,
            "regions": regions,
        }
        print(
            f"recall {img.stem} bubbles={len(regions)} missed={sum(1 for r in regions if not r['covered'])}",
            flush=True,
        )
    work.mkdir(parents=True, exist_ok=True)
    trans = work / "translations.json"
    existing = load_json(trans, {})
    if isinstance(existing, dict):
        for key, val in mapping.items():
            if key not in existing:
                existing[key] = val
            elif (not existing[key] or existing[key] == key) and val and val != key:
                existing[key] = val
        mapping = existing
    trans.write_text(json.dumps(mapping, ensure_ascii=False, indent=2), encoding="utf-8")
    recall_path(work).write_text(
        json.dumps(stored, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"recall added_keys={added} -> {recall_path(work)}", flush=True)
    return stored


def cluster_boxes_by_y_gap(
    boxes: list[list[int]], gap: int = 24
) -> list[list[list[int]]]:
    ordered = sorted((b for b in boxes if b), key=lambda b: (b[1], b[0]))
    if not ordered:
        return []
    clusters: list[list[list[int]]] = [[ordered[0]]]
    for box in ordered[1:]:
        prev = clusters[-1][-1]
        if box[1] - prev[3] >= gap:
            clusters.append([box])
        else:
            clusters[-1].append(box)
    return clusters


def split_title_boxes(
    text: str, boxes: list[list[int]]
) -> list[tuple[str, list[int]]]:
    parts = local_qwen.split_merged_titles(text)
    if len(parts) < 2 or len(boxes) < 2:
        return []
    clusters = cluster_boxes_by_y_gap(boxes)
    if len(clusters) != len(parts):
        return []
    out: list[tuple[str, list[int]]] = []
    for part, group in zip(parts, clusters):
        out.append(
            (
                part,
                [
                    min(b[0] for b in group),
                    min(b[1] for b in group),
                    max(b[2] for b in group),
                    max(b[3] for b in group),
                ],
            )
        )
    return out


def overlay_pages(work: Path, stems: list[str] | None = None) -> int:
    from PIL import Image

    stored = load_json(recall_path(work), {})
    if not isinstance(stored, dict) or not stored:
        print("overlay skip: no bubble_recall.json")
        return 0
    mapping = load_json(work / "translations.json", {})
    if not isinstance(mapping, dict):
        mapping = {}
    dest = work / "out"
    dest.mkdir(parents=True, exist_ok=True)
    painted = 0
    leftovers: list[dict] = []
    for stem, rec in stored.items():
        if stems and stem.lower() not in {s.lower() for s in stems}:
            continue
        regions = rec.get("regions") if isinstance(rec, dict) else None
        if not regions:
            continue
        out_img = None
        for suffix in local_qwen.IMAGE_SUFFIXES:
            candidate = dest / f"{stem}{suffix}"
            if candidate.is_file():
                out_img = candidate
                break
        if out_img is None:
            continue
        page = Image.open(out_img).convert("RGB")
        source = None
        for suffix in local_qwen.IMAGE_SUFFIXES:
            candidate = work / "typeset_in" / f"{stem}{suffix}"
            if candidate.is_file():
                try:
                    source = Image.open(candidate).convert("RGB")
                except OSError:
                    source = None
                break
        changed = False
        known = sidecar_texts(work, stem)
        boxes = sidecar_boxes(work, stem)
        labeled = sidecar_labeled_boxes(work, stem)
        painted_boxes: list[list[int]] = []
        for src, group in sidecar_labeled_groups(work, stem):
            parts = close_split_gaps(split_title_boxes(src, group))
            if not parts:
                if not group or not is_contents_entry(src):
                    continue
                parts = [
                    (
                        src,
                        [
                            min(item[0] for item in group),
                            min(item[1] for item in group),
                            max(item[2] for item in group),
                            max(item[3] for item in group),
                        ],
                    )
                ]
            for part, box in parts:
                box = clamp_box(box, page.size[0], page.size[1])
                dst = lookup_translation(mapping, part)
                if not local_qwen.translation_ok(part, dst):
                    continue
                if local_qwen.leftover_japanese(dst):
                    continue
                if not needs_overlay(part, box, page, source, dest=dst):
                    continue
                if any(_boxes_overlap(box, prev) for prev in painted_boxes):
                    continue
                render_translation(page, box, dst, source)
                painted += 1
                painted_boxes.append(box)
                changed = True
        for row in regions:
            src = str(row.get("text") or "")
            box = clamp_box(row.get("xyxy") or [0, 0, 1, 1], page.size[0], page.size[1])
            score = float(row.get("score") or 0)
            dst = lookup_translation(mapping, src)
            if not local_qwen.translation_ok(src, dst):
                miss = uncovered_src(src, known)
                alt = lookup_translation(mapping, miss) if miss else ""
                if miss and local_qwen.translation_ok(miss, alt):
                    dst = alt
                else:
                    dst = ""
            if not needs_overlay(src, box, page, source, score, dest=dst):
                if restore_untranslated_art(page, source, box, src, known, mapping):
                    changed = True
                elif restore_overlay_on_art(page, source, box, src):
                    changed = True
                continue
            if not dst or local_qwen.leftover_japanese(dst):
                if restore_untranslated_art(page, source, box, src, known, mapping):
                    changed = True
                if not dst:
                    print(f"overlay skip untranslated {stem}: {src[:40]}", flush=True)
                continue
            if any(_boxes_overlap(box, prev) for prev in painted_boxes):
                continue
            render_translation(page, box, dst, source)
            painted += 1
            painted_boxes.append(box)
            changed = True
        for src, group in sidecar_labeled_groups(work, stem):
            if is_contents_entry(src) or split_title_boxes(src, group):
                continue
            if not group:
                continue
            box = clamp_box(
                [
                    min(item[0] for item in group),
                    min(item[1] for item in group),
                    max(item[2] for item in group),
                    max(item[3] for item in group),
                ],
                page.size[0],
                page.size[1],
            )
            dst = lookup_translation(mapping, src)
            if not local_qwen.translation_ok(src, dst) or local_qwen.leftover_japanese(dst):
                continue
            if not needs_overlay(src, box, page, source, dest=dst):
                if restore_overlay_on_art(page, source, box, src):
                    changed = True
                continue
            if any(_boxes_overlap(box, prev) for prev in painted_boxes):
                continue
            render_translation(page, box, dst, source)
            painted += 1
            painted_boxes.append(box)
            changed = True
        for row in regions:
            src = str(row.get("text") or "")
            box = clamp_box(row.get("xyxy") or [0, 0, 1, 1], page.size[0], page.size[1])
            if skip_low_score_overlay(float(row.get("score") or 0), src):
                continue
            reason = leftover_reason(
                src, box, known, boxes, labeled, painted_boxes, mapping, page, source
            )
            if not reason:
                continue
            leftovers.append(
                {
                    "stem": stem,
                    "text": src,
                    "xyxy": box,
                    "reason": reason,
                }
            )
        if changed:
            page.save(out_img)
            print(f"overlay {stem} -> {out_img.name}", flush=True)
    report = {"count": len(leftovers), "items": leftovers}
    uncovered_path(work).write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"overlay painted={painted} leftover={len(leftovers)}", flush=True)
    return painted


def recall_cache_fresh(work: Path, stems: list[str] | None = None) -> bool:
    stored = load_json(recall_path(work), {})
    if not isinstance(stored, dict) or not stored:
        return False
    pages = select_pages(work, stems)
    if not pages:
        return False
    for img in pages:
        try:
            mtime = img.stat().st_mtime
        except OSError:
            return False
        prev = stored.get(img.stem)
        if (
            not isinstance(prev, dict)
            or prev.get("mtime") != mtime
            or not isinstance(prev.get("regions"), list)
        ):
            return False
    return True


def merge_cached_recall_keys(work: Path) -> int:
    stored = load_json(recall_path(work), {})
    mapping = load_json(work / "translations.json", {})
    if not isinstance(mapping, dict):
        mapping = {}
    added = 0

    def adopt(text: str) -> None:
        nonlocal added
        if not text or text in SKIP:
            return
        keys = [text]
        cleaned = local_qwen.repair_ocr_bang(text)
        if cleaned and cleaned not in keys:
            keys.append(cleaned)
        for key in keys:
            if not key or key in SKIP:
                continue
            existing = mapping.get(key, "")
            if local_qwen.translation_ok(key, existing):
                continue
            looked = lookup_translation(mapping, key)
            if local_qwen.translation_ok(key, looked):
                mapping[key] = looked
                added += 1
                continue
            if key not in mapping:
                mapping[key] = ""
                added += 1

    if isinstance(stored, dict):
        for rec in stored.values():
            if not isinstance(rec, dict):
                continue
            regions = rec.get("regions")
            if not isinstance(regions, list):
                continue
            for row in regions:
                if not isinstance(row, dict):
                    continue
                adopt(str(row.get("text") or ""))
    leftover = load_json(uncovered_path(work), {})
    items = leftover.get("items") if isinstance(leftover, dict) else None
    if isinstance(items, list):
        for item in items:
            if not isinstance(item, dict):
                continue
            if str(item.get("reason") or "") not in {"", "untranslated"}:
                continue
            adopt(str(item.get("text") or ""))
    if added:
        (work / "translations.json").write_text(
            json.dumps(mapping, ensure_ascii=False, indent=2), encoding="utf-8"
        )
    return added


def launch_with_mit(
    work: Path, *, overlay: bool = False, pages: list[str] | None = None
) -> int:
    import typeset_ocr_local

    script = Path(__file__).resolve()
    mit_py = typeset_ocr_local.mit_paths()[1]
    if not mit_py.is_file():
        print(f"bubble-recall skip: missing {mit_py}", flush=True)
        return 0
    cmd = [str(mit_py), "-u", str(script)]
    if overlay:
        cmd.append("--overlay")
    if pages:
        cmd.extend(["--pages", ",".join(pages)])
    cmd.append(str(work))
    env = os.environ.copy()
    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    env["CUDA_VISIBLE_DEVICES"] = ""
    print("bubble-recall", "overlay" if overlay else "detect", work, flush=True)
    return subprocess.run(cmd, cwd=str(script.parent), env=env).returncode


def main() -> int:
    argv = list(sys.argv[1:])
    argv, overlay = local_qwen.take_flag(argv, "--overlay")
    pages: list[str] | None = None
    if "--pages" in argv:
        idx = argv.index("--pages")
        if idx + 1 >= len(argv):
            print("usage: bubble_recall_local.py [--overlay] [--pages a,b] <work-dir>", file=sys.stderr)
            return 2
        pages = [part.strip() for part in argv[idx + 1].split(",") if part.strip()]
        argv = argv[:idx] + argv[idx + 2 :]
    if len(argv) < 1:
        print("usage: bubble_recall_local.py [--overlay] [--pages a,b] <work-dir>", file=sys.stderr)
        return 2
    work = Path(argv[0]).resolve()
    if overlay:
        overlay_pages(work, pages)
        return 0
    recall_pages(work, pages)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
