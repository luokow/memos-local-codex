# Local AI chat Qwen (llama-server on loopback). Never prints API keys.
# Direct /v1/chat/completions — do not send this model through qwen_mt_proxy.py.
from __future__ import annotations

import json
import os
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

LOCAL_HOST = "127.0.0.1"
LOCAL_PORT = 18135
LOCAL_BASE = f"http://{LOCAL_HOST}:{LOCAL_PORT}/v1"
LOCAL_CHAT = f"{LOCAL_BASE}/chat/completions"
LOCAL_HEALTH = f"http://{LOCAL_HOST}:{LOCAL_PORT}/health"
LOCAL_MODEL = "qwen3.5:9b-uncensored-local"
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
    "你是日文漫画对白翻译器。将日文译成简体中文。"
    "数组必须逐条 1:1。保留语气词、拟声词和 ♥ 等符号。"
    "占位符 @@RPGn@@ 必须原样保留。"
    "不要解释，不要注音。"
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


def chat_user_content(texts: list[str]) -> str:
    return (
        "将以下文本数组逐条翻译成简体中文，只返回 JSON 字符串数组，禁止多余文字：\n"
        + GUARD
        + "\n"
        + json.dumps(texts, ensure_ascii=False)
    )


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
) -> dict[str, Any]:
    schema = translation_json_schema(len(texts))
    return {
        "model": model,
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": chat_user_content(texts)},
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
) -> list[str]:
    protected, helds = protect_batch(texts)
    payload = chat_payload(model, protected, system)
    data = post_chat(url, payload, key, timeout=timeout)
    return restore_batch(parse_reply(message_text(data), len(texts)), helds)


def translate_texts(texts: list[str], *, system: str = GAME_SYSTEM) -> list[str]:
    return translate_chat(LOCAL_CHAT, LOCAL_KEY, LOCAL_MODEL, texts, system)


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
