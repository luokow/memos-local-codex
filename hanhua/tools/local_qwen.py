# Local AI chat Qwen (llama-server on loopback). Never prints API keys.
# Direct /v1/chat/completions — do not send this model through qwen_mt_proxy.py.
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from collections.abc import Callable
from pathlib import Path
from typing import Any

LOCAL_HOST = "127.0.0.1"
LOCAL_PORT = 18135
LOCAL_BASE = f"http://{LOCAL_HOST}:{LOCAL_PORT}/v1"
LOCAL_CHAT = f"{LOCAL_BASE}/chat/completions"
LOCAL_HEALTH = f"http://{LOCAL_HOST}:{LOCAL_PORT}/health"
LOCAL_MODEL = "qwen3.5:9b-uncensored-local"
HANHUA_FILL_MODEL = "sakura-galtransl-7b-v3-7"
LOCAL_KEY = "local"
CHUNK = 8
TIMEOUT = 300
PROGRESS_JSONL = False
DEFAULT_MIT_ROOT = r"D:\grok\tools\manga-image-translator"


def take_flag(argv: list[str], flag: str) -> tuple[list[str], bool]:
    if flag in argv:
        return [a for a in argv if a != flag], True
    return argv, False


def enable_progress(enabled: bool) -> None:
    global PROGRESS_JSONL
    PROGRESS_JSONL = bool(enabled)


def log(msg: str) -> None:
    stream = sys.stderr if PROGRESS_JSONL else sys.stdout
    print(msg, flush=True, file=stream)


def emit(kind: str, phase: str, **kwargs: Any) -> None:
    if not PROGRESS_JSONL:
        return
    rec = {
        "type": kind,
        "phase": phase,
        "done": int(kwargs.get("done", 0) or 0),
        "total": int(kwargs.get("total", 0) or 0),
        "message": str(kwargs.get("message") or ""),
        "output": str(kwargs.get("output") or ""),
        "empty": int(kwargs.get("empty", 0) or 0),
    }
    sys.stdout.write(json.dumps(rec, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def mit_root() -> Path:
    env = str(os.environ.get("HANHUA_MIT_ROOT") or "").strip()
    return Path(env) if env else Path(DEFAULT_MIT_ROOT)


IMAGE_SUFFIXES = {".png", ".jpg", ".jpeg", ".webp", ".bmp"}
_HIRA_RE = re.compile(r"[\u3041-\u3096]")
_KATA_RE = re.compile(r"[\u30A1-\u30FA\u30FC]")
_HAN_RE = re.compile(r"[\u4e00-\u9fff]")
_HIRA_KEEP_RE = re.compile(r"[っ]")
_OCR_BANG_RE = (
    (re.compile(r"\s*1!+"), "！"),
    (re.compile(r"\s*1！+"), "！"),
    (re.compile(r"\s*1\?+"), "？"),
    (re.compile(r"\s*1？+"), "？"),
    (re.compile(r"1⁉"), "⁉"),
)
_DIGIT_BANG_RE = re.compile(
    r"(?<![0-9０-９])[ 　]*[1１][ 　\n\r]*([!！⁉?？]+)"
)
_KANA_TWELVE_RE = re.compile(
    r"(?<=[\u3041-\u3096\u30A1-\u30FA♥♡])[ 　]*12(?![0-9０-９])"
)
_KANA_TWO_Q_RE = re.compile(
    r"(?<=[\u3041-\u3096\u30A1-\u30FA♥♡])[ 　]*[2２]$"
)
_PROTECT_NUM_RE = re.compile(r"第[0-9０-９]+[話话]|[0-9０-９]+人")
_HAA_OCR_RE = re.compile(r"^[４4][０0][：:…．.]*[♥♡]?$")
_KANA_COLON_RE = re.compile(r"(?<=[\u3040-\u30ff])[：:](?=[\u3040-\u30ff])")
_REPEAT_RE = re.compile(r"(.)\1{5,}")
_SKIP_KEYS = {"", "trans", "text"}


def list_image_files(folder: Path) -> list[Path]:
    if not folder.is_dir():
        return []
    return sorted(
        p
        for p in folder.iterdir()
        if p.is_file() and p.suffix.lower() in IMAGE_SUFFIXES
    )


def copy_images(src: Path, dest: Path) -> list[Path]:
    dest.mkdir(parents=True, exist_ok=True)
    copied: list[Path] = []
    for img in list_image_files(src):
        target = dest / img.name
        shutil.copy2(img, target)
        copied.append(target)
    return copied


def output_image_exists(folder: Path, stem: str) -> bool:
    return any((folder / f"{stem}{ext}").is_file() for ext in IMAGE_SUFFIXES)


def output_image_mtime(folder: Path, stem: str) -> float | None:
    latest: float | None = None
    for ext in IMAGE_SUFFIXES:
        path = folder / f"{stem}{ext}"
        if not path.is_file():
            continue
        stamp = path.stat().st_mtime
        if latest is None or stamp > latest:
            latest = stamp
    return latest


def snapshot_output_mtimes(
    images: list[Path], output_dirs: list[Path]
) -> dict[str, float]:
    snap: dict[str, float] = {}
    for img in images:
        for folder in output_dirs:
            stamp = output_image_mtime(folder, img.stem)
            if stamp is None:
                continue
            prev = snap.get(img.stem)
            if prev is None or stamp > prev:
                snap[img.stem] = stamp
    return snap


def ocr_json_complete(path: Path) -> bool:
    """True when OCR ran: has text, or an explicit skip after empty retry."""
    if not path.is_file():
        return False
    try:
        parsed = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return False
    rows = parsed if isinstance(parsed, list) else [parsed] if isinstance(parsed, dict) else []
    has_text = False
    has_skip = False
    for row in rows:
        if isinstance(row, dict):
            if str(row.get("text") or "").strip():
                has_text = True
            if row.get("skip"):
                has_skip = True
        elif isinstance(row, str) and row.strip():
            has_text = True
    return has_text or has_skip


def missing_ocr_pages(images: list[Path], ocr_dirs: list[Path]) -> list[Path]:
    missing: list[Path] = []
    for img in images:
        if not any(ocr_json_complete(folder / f"{img.stem}_ocr.json") for folder in ocr_dirs):
            missing.append(img)
    return missing


def _digit_bang_repl(match: re.Match[str]) -> str:
    marks = match.group(1)
    if "⁉" in marks:
        return "⁉"
    has_q = "?" in marks or "？" in marks
    has_b = "!" in marks or "！" in marks
    if has_q and has_b:
        return "！？"
    if has_q:
        return "？"
    return "！"


def repair_ocr_bang(text: str) -> str:
    """manga-ocr often reads ！ as 1 and ？ as 2. Keep real numbers like 第1話."""
    out = str(text or "")
    held: list[str] = []

    def protect(match: re.Match[str]) -> str:
        held.append(match.group(0))
        return f"\x00N{len(held) - 1}\x00"

    out = _PROTECT_NUM_RE.sub(protect, out)
    for pat, repl in _OCR_BANG_RE:
        out = pat.sub(repl, out)
    out = _DIGIT_BANG_RE.sub(_digit_bang_repl, out)
    out = _KANA_TWELVE_RE.sub("！？", out)
    out = _KANA_TWO_Q_RE.sub("？", out)
    stripped = out.strip()
    if stripped in {"1", "１"}:
        out = "！"
    elif stripped in {"12", "１2", "1２", "１２"}:
        out = "！？"
    for i, val in enumerate(held):
        out = out.replace(f"\x00N{i}\x00", val)
    out = _KANA_COLON_RE.sub("", out)
    stripped = out.strip()
    if _HAA_OCR_RE.match(stripped):
        heart = "♥" if ("♥" in stripped or "♡" in stripped) else ""
        out = "はぁ…" + heart
    if _HAN_RE.search(out):
        out = out.replace("...", "…")
        out = re.sub(r"[.．]{2,}", "…", out)
        out = re.sub(r"…{2,}", "…", out)
        out = out.replace("!?", "！？")
        out = out.replace("?!", "？！")
    return out


def has_ocr_digit_artifact(text: str) -> bool:
    value = text or ""
    if value.strip() in {"1", "１", "12", "１2", "1２", "１２"}:
        return True
    if _HAA_OCR_RE.match(value.strip()):
        return True
    if _DIGIT_BANG_RE.search(value) or re.search(r"[1１]\s*[!！⁉?？]", value):
        if _PROTECT_NUM_RE.search(value) and not re.search(
            r"(?<![第0-9０-９話话人])[1１]\s*[!！⁉?？]", value
        ):
            return False
        return True
    if _KANA_TWELVE_RE.search(value):
        return True
    return False


def is_repetition_hallucination(dst: str, src: str) -> bool:
    value = dst or ""
    if not _REPEAT_RE.search(value):
        return False
    return len(value) > len(src or "") + 3


def has_hiragana(text: str) -> bool:
    return bool(_HIRA_RE.search(text or ""))


def _latin_or_digit(text: str) -> bool:
    core = re.sub(r"[^\w]", "", text or "", flags=re.UNICODE)
    core = re.sub(r"[\u3040-\u30ff\u4e00-\u9fff]", "", core)
    return bool(core) and not _HIRA_RE.search(text or "") and not _KATA_RE.search(
        text or ""
    ) and not _HAN_RE.search(text or "")


def _short_sfx(text: str) -> bool:
    if _HIRA_RE.search(text or "") or _HAN_RE.search(text or ""):
        return False
    core = "".join(_KATA_RE.findall(text or ""))
    return 1 <= len(core) <= 3


def _cjk_name_or_sign(text: str) -> bool:
    """Kanji-only names/signs already read as Chinese (怜奈, 保健室). Keep 更衣室-length lines out."""
    value = text or ""
    if _HIRA_RE.search(value) or _KATA_RE.search(value):
        return False
    han = _HAN_RE.findall(value)
    return 2 <= len(han) <= 4


def leftover_japanese(dst: str) -> bool:
    text = dst or ""
    hira = _HIRA_KEEP_RE.sub("", text)
    if _HIRA_RE.search(hira):
        return True
    if _HAN_RE.search(text):
        for run in _KATA_RE.findall(text):
            stem = run.replace("ッ", "").replace("ー", "")
            if len(stem) >= 2:
                return True
    return False


def skip_rewrite(src: str) -> bool:
    """True for sfx/names/latin that a rewrite pass should leave alone."""
    source = str(src or "")
    if not source.strip() or len(source.strip()) <= 1:
        return True
    return _short_sfx(source) or _latin_or_digit(source) or _cjk_name_or_sign(source)


def translation_ok(src: str, dst: str) -> bool:
    value = "" if dst is None else str(dst)
    if not value.strip():
        return False
    source = str(src or "")
    if has_ocr_digit_artifact(value):
        return False
    if is_repetition_hallucination(value, source):
        return False
    if value == source or value.strip() == source.strip():
        if has_ocr_digit_artifact(source):
            return False
        return _short_sfx(source) or _latin_or_digit(source) or _cjk_name_or_sign(source)
    return not leftover_japanese(value)


def normalize_mapping(data: dict[str, str]) -> list[str]:
    """Repair OCR 1/12 artifacts in place and alias cleaned keys. Returns changed keys."""
    changed: list[str] = []
    for raw, dst in list(data.items()):
        if raw in _SKIP_KEYS:
            continue
        cleaned = repair_ocr_bang(raw)
        value = repair_ocr_bang("" if dst is None else str(dst))
        if data.get(raw) != value:
            data[raw] = value
            changed.append(raw)
        if cleaned != raw:
            prev = data.get(cleaned, "")
            if not prev or not translation_ok(cleaned, prev):
                if data.get(cleaned) != value:
                    data[cleaned] = value
                    changed.append(cleaned)
            elif not translation_ok(raw, data.get(raw, "")):
                data[raw] = prev
                if raw not in changed:
                    changed.append(raw)
    return changed


def name_glossary(data: dict[str, str], limit: int = 24) -> str:
    rows: list[str] = []
    seen: set[str] = set()
    for key, val in data.items():
        if key in _SKIP_KEYS or not translation_ok(key, val):
            continue
        if leftover_japanese(val) or val.strip() == key.strip():
            continue
        honorific = bool(re.search(r"(君|ちゃん|さん|様)$", key))
        short_name = 2 <= len(key) <= 4 and (
            _HIRA_RE.search(key) or _KATA_RE.search(key) or _HAN_RE.search(key)
        )
        if not honorific and not short_name:
            continue
        item = f"{key}={val}"
        if item in seen:
            continue
        seen.add(item)
        rows.append(item)
        if len(rows) >= limit:
            break
    return "；".join(rows)


def pack_root() -> Path:
    return Path(__file__).resolve().parent.parent


def mit_dict_args() -> list[str]:
    args: list[str] = []
    for flag, name in (("--pre-dict", "mit_pre_dict.txt"), ("--post-dict", "mit_post_dict.txt")):
        path = pack_root() / name
        if path.is_file():
            args.extend([flag, str(path)])
    return args


def mapped_translation(mapping: dict[str, str], query: str) -> str:
    raw = "" if query is None else str(query)
    for key in (raw, raw.strip(), repair_ocr_bang(raw), repair_ocr_bang(raw.strip())):
        if key in mapping and mapping[key] is not None:
            return str(mapping[key])
    return ""


def store_translation(data: dict[str, str], raw: str, dst: str) -> None:
    value = repair_ocr_bang("" if dst is None else str(dst))
    cleaned = repair_ocr_bang(raw)
    accepted = value if translation_ok(raw, value) or translation_ok(cleaned, value) else ""
    data[raw] = accepted
    data[cleaned] = accepted
    for key in list(data):
        if key in _SKIP_KEYS:
            continue
        if repair_ocr_bang(key) == cleaned:
            data[key] = accepted


def completed_image_pages(
    pngs: list[Path],
    *,
    ocr_dirs: list[Path] | None = None,
    output_dirs: list[Path] | None = None,
    baseline: dict[str, float] | None = None,
) -> int:
    """Count finished pages. Input images themselves do not count as done.

    When baseline is set, output files only count if their mtime is newer than
    the snapshot. Catch-up typeset overwrites existing out/ pages; existence
    alone would report 100% immediately.
    """
    done = 0
    for png in pngs:
        stem = png.stem
        finished = False
        for folder in ocr_dirs or []:
            if ocr_json_complete(folder / f"{stem}_ocr.json"):
                finished = True
                break
        if not finished:
            for folder in output_dirs or []:
                if not output_image_exists(folder, stem):
                    continue
                if baseline is None:
                    finished = True
                    break
                stamp = output_image_mtime(folder, stem)
                if stamp is not None and stamp > baseline.get(stem, 0.0):
                    finished = True
                    break
        if finished:
            done += 1
    return done


def run_mit_with_page_progress(
    cmd: list[str],
    *,
    cwd: str,
    env: dict[str, str],
    phase: str,
    total: int,
    count: Callable[[], int],
    interval: float = 0.5,
) -> int:
    """Run MIT and emit jsonl whenever the on-disk page count increases."""
    proc = subprocess.Popen(cmd, cwd=cwd, env=env)
    last = -1
    total = max(0, int(total))
    try:
        while True:
            done = min(total, max(0, int(count() or 0)))
            if done != last:
                last = done
                emit("progress", phase, done=done, total=total, message=f"{done}/{total}")
            if proc.poll() is not None:
                break
            time.sleep(interval)
        done = min(total, max(0, int(count() or 0)))
        if done != last:
            emit("progress", phase, done=done, total=total, message=f"{done}/{total}")
        return int(proc.returncode or 0)
    except BaseException:
        if proc.poll() is None:
            proc.kill()
        raise


def isolate_image_folder(folder: Path, dest: Path) -> int:
    """Move non-image sidecars out so MIT does not try to open *_ocr.json."""
    if not folder.is_dir():
        return 0
    dest.mkdir(parents=True, exist_ok=True)
    moved = 0
    for item in list(folder.iterdir()):
        if not item.is_file():
            continue
        if item.suffix.lower() in IMAGE_SUFFIXES:
            continue
        target = dest / item.name
        if target.exists():
            target.unlink()
        item.replace(target)
        moved += 1
    return moved

GAME_SYSTEM = (
    "你是专业的日文游戏文本翻译器。将日文翻译成简体中文。"
    "数组必须逐条 1:1，禁止把多条合并成一条。"
    "严格保留换行符 \\n。"
    "占位符 @@RPGn@@ 是原文控制码，必须原样保留，不要翻译或删改。"
    "不要抄写原文末尾的类型数字。"
)
IMAGE_SYSTEM = (
    "你是日文漫画对白翻译器。译成角色会说出口的简体中文，像汉化组润过，不要逐字对译。"
    "数组必须逐条 1:1。保留语气词、拟声词和 ♥ 等符号。"
    "按中文习惯重组：もっと…ちょうだい是「再多给我／还要」，不是「再让我…一点」。"
    "色情词按口吻直译（鸡巴／肉棒／那里），禁止用「下面」代替生殖器。不要审查，不要用*号代替。"
    "禁止把日文原样返回。对白、招牌、说明必须译成简体中文。"
    "禁止在译文里留平假名。片假名单词要译成中文，仅两三个字的拟声词可留片假名。"
    "数字1紧挨感叹号是OCR把！认成1，必须写成！不要抄1；句尾12多半是！？。"
    "译名前后保持一致。占位符 @@RPGn@@ 必须原样保留。"
    "不要解释，不要注音。"
)
IMAGE_RETRY_SYSTEM = (
    IMAGE_SYSTEM
    + "这句仍是日文或夹着日文，必须译成简体中文。"
    + "禁止原样返回。禁止在译文里留平假名。"
)
# SakuraLLM GalTransl official system (v3). Do not mix with IMAGE_SYSTEM.
GALTRANSL_SYSTEM = (
    "你是一个视觉小说翻译模型，可以通顺地使用给定的术语表以指定的风格将日文翻译成简体中文，"
    "并联系上下文正确使用人称代词，注意不要混淆使役态和被动态的主语和宾语，"
    "不要擅自添加原文中没有的特殊符号，也不要擅自增加或减少换行。"
)
GUARD = (
    "规则：逐条 1:1，数组长度必须与输入相同；禁止把多条合并成一条；"
    "禁止把其它条目的译文写进当前条；保留换行 \\n 和占位符 @@RPGn@@；"
    "不要抄写或添加原文末尾的类型数字 0-9；"
    "只返回 JSON 字符串数组。"
)

_THINK_RE = re.compile(r"<think>.*?</think>", re.DOTALL | re.IGNORECASE)
_CODE_RE = re.compile(r"\\(?:[cCnNiIvV]|msgev)\[[^\]]*\]")


def is_qwen_mt(model: str) -> bool:
    return "qwen-mt" in str(model or "").lower()


def is_galtransl_model(model: str) -> bool:
    name = str(model or "").lower()
    return "galtransl" in name or "sakura" in name


def galtransl_user_content(texts: list[str], glossary: str = "") -> str:
    gloss = str(glossary or "").strip() or "（空）"
    return (
        "参考以下术语表（可为空，格式为src->dst #备注）：\n"
        + gloss
        + "\n根据以上术语表的对应关系和备注，将下面的文本从日文翻译成简体中文：\n"
        + "\n".join(str(item) for item in texts)
    )


def galtransl_glossary(data: dict[str, str], limit: int = 24) -> str:
    raw = name_glossary(data, limit=limit)
    rows: list[str] = []
    for item in raw.split("；"):
        if "=" not in item:
            continue
        src, dst = item.split("=", 1)
        rows.append(f"{src}->{dst}")
    blob = "".join(str(k) for k in data)
    if re.search(r"くん|君", blob) and not any(r.startswith("センパイ->") for r in rows):
        rows.insert(0, "先輩->学长 #男性前辈")
        rows.insert(0, "センパイ->学长 #男性前辈")
    return "\n".join(rows)


def list_local_models() -> list[str]:
    req = urllib.request.Request(f"{LOCAL_BASE}/models", method="GET")
    with urlopen_http(req, 3, bypass_proxy=True) as resp:
        payload = json.loads(resp.read().decode("utf-8"))
    ids: list[str] = []
    for row in payload.get("data") or []:
        if isinstance(row, dict) and row.get("id"):
            ids.append(str(row["id"]))
    return ids


def active_local_model() -> str:
    env = str(os.environ.get("HANHUA_LOCAL_MODEL") or "").strip()
    if env:
        return env
    try:
        ids = list_local_models()
        if ids:
            return ids[0]
    except Exception:  # noqa: BLE001
        pass
    return LOCAL_MODEL


def fill_local_model() -> str:
    """Image fill always requests GalTransl unless HANHUA_LOCAL_MODEL overrides."""
    env = str(os.environ.get("HANHUA_LOCAL_MODEL") or "").strip()
    return env or HANHUA_FILL_MODEL


def completions_url(base: str) -> str:
    u = str(base or "").strip().rstrip("/")
    if not u:
        return u
    if re.search(r"/chat/completions$", u, re.I):
        return u
    if re.search(r"/(v1|v3|v4|paas/v4)$", u, re.I):
        return u + "/chat/completions"
    return u + "/chat/completions"


def mt_payload(model: str, texts: list[str]) -> dict[str, Any]:
    return {
        "model": model,
        "messages": [{"role": "user", "content": json.dumps(texts, ensure_ascii=False)}],
        "translation_options": {"source_lang": "Japanese", "target_lang": "Chinese"},
    }


def chat_user_content(texts: list[str], *, system: str = GAME_SYSTEM) -> str:
    spoken = "漫画对白" in str(system or "")
    lead = (
        "把下面日文对白译成角色会说出口的简体中文，不要逐字对应。只返回 JSON 字符串数组，禁止多余文字：\n"
        if spoken
        else "将以下文本数组逐条翻译成简体中文，只返回 JSON 字符串数组，禁止多余文字：\n"
    )
    return lead + GUARD + "\n" + json.dumps(texts, ensure_ascii=False)


def translation_json_schema(count: int) -> dict[str, Any]:
    n = max(1, int(count))
    return {
        "type": "array",
        "items": {"type": "string"},
        "minItems": n,
        "maxItems": n,
    }


def chat_payload(
    model: str,
    texts: list[str],
    system: str,
    *,
    temperature: float = 0.3,
    max_tokens: int = 2048,
    glossary: str = "",
) -> dict[str, Any]:
    if is_galtransl_model(model):
        return {
            "model": model,
            "messages": [
                {"role": "system", "content": GALTRANSL_SYSTEM},
                {"role": "user", "content": galtransl_user_content(texts, glossary)},
            ],
            "temperature": temperature,
            "top_p": 0.8,
            "max_tokens": max_tokens,
            "chat_template_kwargs": {"enable_thinking": False},
        }
    schema = translation_json_schema(len(texts))
    return {
        "model": model,
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": chat_user_content(texts, system=system)},
        ],
        "temperature": temperature,
        "max_tokens": max_tokens,
        "chat_template_kwargs": {"enable_thinking": False},
        # llama.cpp grammar: same-length JSON string array (control codes are placeholders).
        "json_schema": schema,
        "response_format": {
            "type": "json_schema",
            "json_schema": {"name": "translation_array", "schema": schema, "strict": True},
        },
    }


def message_text(data: dict[str, Any]) -> str:
    msg = ((data.get("choices") or [{}])[0].get("message") or {})
    content = msg.get("content") or ""
    if isinstance(content, list):
        content = "".join(
            str(p.get("text") if isinstance(p, dict) else p) for p in content
        )
    text = str(content).strip()
    if text:
        return text
    reasoning = msg.get("reasoning_content")
    return str(reasoning or "").strip()


def parse_galtransl_reply(content: str, expected: int) -> list[str]:
    try:
        return parse_reply(content, expected)
    except RuntimeError:
        pass
    text = _THINK_RE.sub("", str(content or "")).strip()
    lines = [line.strip() for line in text.splitlines()]
    if len(lines) == expected:
        return lines
    filled = [line for line in lines if line]
    if len(filled) == expected:
        return filled
    if expected == 1 and text:
        return [text]
    raise RuntimeError("unexpected galtransl reply length")


def parse_reply(content: str, expected: int) -> list[str]:
    text = _THINK_RE.sub("", str(content or "")).strip()
    text = re.sub(r"^```(?:json)?\s*", "", text, flags=re.I)
    text = re.sub(r"\s*```$", "", text)
    arr = _as_string_array(_try_json(text))
    if arr is None:
        start = text.find("[")
        if start >= 0:
            slice_ = text[start:]
            end = slice_.rfind("]")
            while end >= 0:
                arr = _as_string_array(_try_json(slice_[: end + 1]))
                if arr is not None:
                    break
                end = slice_.rfind("]", 0, end)
    if arr is not None and len(arr) == expected:
        return arr
    if arr is not None and expected == 1 and arr:
        return [arr[0]]
    if expected == 1 and text:
        return [text]
    raise RuntimeError("unexpected chat reply length")


def _protect_codes(text: str) -> tuple[str, list[str]]:
    held: list[str] = []

    def repl(match: re.Match[str]) -> str:
        held.append(match.group(0))
        return f"@@RPG{len(held) - 1}@@"

    return _CODE_RE.sub(repl, text), held


def protect_batch(texts: list[str]) -> tuple[list[str], list[list[str]]]:
    protected: list[str] = []
    helds: list[list[str]] = []
    for text in texts:
        item, held = _protect_codes(text)
        protected.append(item)
        helds.append(held)
    return protected, helds


def restore_batch(texts: list[str], helds: list[list[str]]) -> list[str]:
    return [str(_restore_codes(text, held)) for text, held in zip(texts, helds)]


def _restore_codes(value: Any, held: list[str]) -> Any:
    if isinstance(value, str):
        for i, code in enumerate(held):
            value = value.replace(f"@@RPG{i}@@", code)
        return value
    if isinstance(value, list):
        return [_restore_codes(item, held) for item in value]
    if isinstance(value, dict):
        return {k: _restore_codes(v, held) for k, v in value.items()}
    return value


def _try_json(text: str) -> Any:
    protected, held = _protect_codes(text)
    try:
        parsed = json.loads(protected)
    except json.JSONDecodeError:
        return None
    return _restore_codes(parsed, held)


def _as_string_array(parsed: Any) -> list[str] | None:
    if isinstance(parsed, list):
        return ["" if x is None else str(x) for x in parsed]
    if isinstance(parsed, dict) and isinstance(parsed.get("translations"), list):
        return ["" if x is None else str(x) for x in parsed["translations"]]
    return None


def is_loopback_url(url: str) -> bool:
    u = str(url or "").lower()
    return "://127.0.0.1" in u or "://localhost" in u or "://[::1]" in u


def urlopen_http(req: urllib.request.Request, timeout: int, *, bypass_proxy: bool):
    if bypass_proxy:
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        return opener.open(req, timeout=timeout)
    return urllib.request.urlopen(req, timeout=timeout)


def post_chat(url: str, payload: dict[str, Any], key: str, timeout: int = TIMEOUT) -> dict[str, Any]:
    req = urllib.request.Request(
        url,
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": "Bearer " + key,
        },
        method="POST",
    )
    try:
        with urlopen_http(req, timeout, bypass_proxy=is_loopback_url(url)) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "replace")[:240].replace("\n", " ")
        raise RuntimeError(f"HTTP {exc.code} {body}") from exc


def probe() -> tuple[bool, str]:
    req = urllib.request.Request(LOCAL_HEALTH, method="GET")
    try:
        with urlopen_http(req, 3, bypass_proxy=True) as resp:
            raw = resp.read().decode("utf-8", "replace")[:200]
            return True, f"health {resp.status} {raw}"
    except Exception as exc:  # noqa: BLE001
        return False, f"{type(exc).__name__}: {exc}"


def translate_chat(
    url: str,
    key: str,
    model: str,
    texts: list[str],
    system: str,
    *,
    timeout: int = TIMEOUT,
    glossary: str = "",
) -> list[str]:
    protected, helds = protect_batch(texts)
    payload = chat_payload(model, protected, system, glossary=glossary)
    data = post_chat(url, payload, key, timeout=timeout)
    raw = message_text(data)
    parsed = (
        parse_galtransl_reply(raw, len(texts))
        if is_galtransl_model(model)
        else parse_reply(raw, len(texts))
    )
    return restore_batch(parsed, helds)


def translate_texts(
    texts: list[str],
    *,
    system: str = GAME_SYSTEM,
    model: str | None = None,
    glossary: str = "",
) -> list[str]:
    name = str(model or active_local_model() or LOCAL_MODEL).strip() or LOCAL_MODEL
    return translate_chat(
        LOCAL_CHAT, LOCAL_KEY, name, texts, system, glossary=glossary
    )


def smoke() -> int:
    ok, detail = probe()
    if not ok:
        print("local Qwen not listening on 127.0.0.1:18135")
        print(detail)
        print("Open Local AI / Qwen Local Chat first. Do not start MiniMax H3 / ComfyUI.")
        print("System proxy must be bypassed for loopback (this script already does).")
        return 2
    print(detail)
    out = translate_texts(["こんにちは"], system=GAME_SYSTEM)
    print("in=こんにちは")
    print("out=" + (out[0] if out else ""))
    if not out or not str(out[0]).strip():
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(smoke())
