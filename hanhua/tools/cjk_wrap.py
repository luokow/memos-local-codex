"""CJK column/line breaks for vertical manga typeset.

Keep in sync with manga_translator.rendering.text_render.wrap_cjk_columns.
Greedy glyph wrap splits 衬衫/大腿/间宫 and leaves a 1-char last column.
"""
from __future__ import annotations

import re
from pathlib import Path

_PREFERRED_AFTER = set(
    "的了着过地得吗呢吧啊呀嘛哇哦嗯呵诶"
    "而与和或但就都也还又在从向对把被让给到是有之其并且跟同"
    "，。！？、；：…—～♥♡」』）)】》"
)
_NO_START = set("！？。、，；：…—～♥♡」』）)】》")


def wrap_cjk_columns(text: str, max_chars: int) -> list[str]:
    text = "".join(str(text or "").replace("\n", "").split())
    if not text:
        return []
    max_chars = max(1, int(max_chars))
    cap = max_chars + 1
    n = len(text)
    cols: list[str] = []
    i = 0
    while i < n:
        remaining = n - i
        if remaining <= cap:
            cols.append(text[i:])
            break
        target = i + max_chars
        window_end = min(n, i + cap)
        preferred: list[int] = []
        for pos in range(i + 1, window_end + 1):
            if pos >= n:
                preferred.append(n)
                continue
            if text[pos] in _NO_START:
                continue
            if text[pos - 1] in _PREFERRED_AFTER:
                preferred.append(pos)
        if preferred:
            pos = min(preferred, key=lambda p: (abs(p - target), -p))
            if n - pos == 1:
                pos = n
        elif remaining <= cap + 2:
            pos = n
        else:
            pos = min(target, n)
            while pos < n and text[pos] in _NO_START:
                pos += 1
            if n - pos == 1:
                pos = n
        if pos <= i:
            pos = min(i + max_chars, n)
        cols.append(text[i:pos])
        i = pos
    while len(cols) >= 2 and len(cols[-1]) <= 1:
        cols[-2] += cols[-1]
        cols.pop()
    while (
        len(cols) >= 2
        and len(cols[-1]) <= 2
        and len(cols[-2]) + len(cols[-1]) <= cap + 1
    ):
        cols[-2] += cols[-1]
        cols.pop()
    return cols


_COORDS_RE = re.compile(r"coords:\s*\[([^\]]+)\]")
_TEXT_RE = re.compile(r"^text:\s+(.*)$", re.M)


def _box(nums: list[int]) -> list[int] | None:
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


def _parse_nums(raw: str) -> list[int]:
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


def parse_sidecar_regions(text: str) -> list[tuple[str, list[list[int]]]]:
    regions: list[tuple[str, list[list[int]]]] = []
    current = ""
    boxes: list[list[int]] = []

    def flush() -> None:
        src = str(current or "").strip()
        if src and boxes:
            regions.append((src, list(boxes)))

    for line in str(text or "").splitlines():
        if line.startswith("--") and line.endswith("--"):
            flush()
            current = ""
            boxes = []
            continue
        match = _TEXT_RE.match(line)
        if match:
            flush()
            current = match.group(1).strip()
            boxes = []
            continue
        found = _COORDS_RE.search(line)
        if found:
            box = _box(_parse_nums(found.group(1)))
            if box:
                boxes.append(box)
    flush()
    return regions


def allocate_counts(total: int, weights: list[int]) -> list[int]:
    n = len(weights)
    if n <= 0:
        return []
    total = max(0, int(total))
    if total == 0:
        return [0] * n
    cleaned = [max(0, int(w)) for w in weights]
    wsum = sum(cleaned) or n
    raw = [total * w / wsum for w in cleaned]
    counts = [int(x) for x in raw]
    leftover = total - sum(counts)
    order = sorted(range(n), key=lambda i: (-(raw[i] - counts[i]), i))
    for i in range(leftover):
        counts[order[i % n]] += 1
    if total >= n:
        for i, count in enumerate(counts):
            if count == 0:
                donor = max(range(n), key=lambda k: counts[k])
                if counts[donor] > 1:
                    counts[donor] -= 1
                    counts[i] = 1
    return counts


def split_source_by_columns(text: str, boxes: list[list[int]]) -> str:
    src = str(text or "").replace("\n", "").strip()
    if not src or len(boxes) <= 1:
        return src
    widths = [max(1, box[2] - box[0]) for box in boxes]
    heights = [max(1, box[3] - box[1]) for box in boxes]
    vertical = sum(heights) >= sum(widths)
    weights = heights if vertical else widths
    counts = allocate_counts(len(src), weights)
    parts: list[str] = []
    index = 0
    for count in counts:
        part = src[index : index + count]
        if part:
            parts.append(part)
        index += count
    if index < len(src) and parts:
        parts[-1] += src[index:]
    return "\n".join(parts) if len(parts) > 1 else src


def load_line_sources(work: Path) -> dict[str, str]:
    sources: dict[str, str] = {}
    root = Path(work)
    for folder in (root / "ocr_sidecars", root / "ocr_dump", root / "typeset_in"):
        if not folder.is_dir():
            continue
        for path in folder.glob("*_translations.txt"):
            try:
                raw = path.read_text(encoding="utf-8-sig")
            except OSError:
                continue
            for text, boxes in parse_sidecar_regions(raw):
                lined = split_source_by_columns(text, boxes)
                if "\n" not in lined:
                    continue
                prev = sources.get(text, "")
                if lined.count("\n") >= prev.count("\n"):
                    sources[text] = lined
    return sources


def fill_query(key: str, line_sources: dict[str, str] | None = None) -> str:
    import local_qwen

    cleaned = local_qwen.repair_ocr_bang(key)
    sources = line_sources or {}
    lined = sources.get(str(key or "")) or sources.get(cleaned) or ""
    if not lined:
        return cleaned
    return "\n".join(local_qwen.repair_ocr_bang(part) for part in lined.split("\n"))


def keep_or_force_breaks(jp_lined: str, zh: str) -> str:
    source = str(jp_lined or "")
    dest = str(zh or "")
    n = source.count("\n") + 1 if source.strip() else 1
    if n <= 1:
        return dest
    if dest.count("\n") >= n - 1:
        return dest
    compact = "".join(dest.split())
    if not compact:
        return dest
    cols = wrap_cjk_columns(compact, max(1, (len(compact) + n - 1) // n))
    return "\n".join(cols) if len(cols) > 1 else dest
