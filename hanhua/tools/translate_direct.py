# Translate ManualTransFile.json.
# Default: Aliyun qwen-mt-plus from MTool config (unchanged).
# --local: Local AI chat Qwen on 127.0.0.1:18135. Never prints API keys.
from __future__ import annotations

import json
import os
import sys
import time
from pathlib import Path

import local_qwen

CONF = Path(r"D:\grok\MTool\Tool\plugins\deepseek_config.json")
CHUNK = 20


def load_upstream() -> tuple[str, str, str]:
    cfg = json.loads(CONF.read_text(encoding="utf-8"))
    url = local_qwen.completions_url(str(cfg["baseUrl"]))
    return url, str(cfg["apiKey"]), str(cfg["model"])


def load_local() -> tuple[str, str, str]:
    return local_qwen.LOCAL_CHAT, local_qwen.LOCAL_KEY, local_qwen.LOCAL_MODEL


def resolve_engine(engine: str | None) -> str:
    if engine in ("local", "mt"):
        return engine
    env = str(os.environ.get("HANHUA_ENGINE") or "").strip().lower()
    if env in ("local", "mt"):
        return env
    return "mt"


def call(url: str, key: str, model: str, texts: list[str], *, engine: str) -> list[str]:
    if engine == "local" or not local_qwen.is_qwen_mt(model):
        return local_qwen.translate_chat(
            url,
            key,
            model,
            texts,
            local_qwen.GAME_SYSTEM,
            timeout=local_qwen.TIMEOUT if engine == "local" else 180,
        )
    payload = local_qwen.mt_payload(model, texts)
    data = local_qwen.post_chat(url, payload, key, timeout=180)
    return local_qwen.parse_reply(local_qwen.message_text(data), len(texts))


def _call_with_retry(
    url: str, key: str, model: str, batch: list[str], *, engine: str
) -> list[str]:
    last_exc: Exception | None = None
    for attempt in range(4):
        try:
            return call(url, key, model, batch, engine=engine)
        except Exception as exc:  # noqa: BLE001
            last_exc = exc
            wait = 2 ** attempt
            print(f"retry {attempt+1} after {wait}s: {type(exc).__name__}: {exc}")
            time.sleep(wait)
    assert last_exc is not None
    raise last_exc


def translate_mapping(
    data: dict[str, str], dest: Path, *, engine: str | None = None
) -> dict[str, str]:
    keys = [k for k, v in data.items() if not v or v == k]
    kind = resolve_engine(engine)
    if kind == "local":
        url, key, model = load_local()
        chunk = local_qwen.CHUNK
        ok, detail = local_qwen.probe()
        if not ok:
            raise RuntimeError(
                "local Qwen not listening on 127.0.0.1:18135 ("
                + detail
                + "). Open Local AI first; do not start MiniMax H3."
            )
        print(f"translate {len(keys)}/{len(data)} empty keys engine=local model={model}")
    else:
        url, key, model = load_upstream()
        chunk = CHUNK
        print(f"translate {len(keys)}/{len(data)} empty keys engine=mt model={model}")
    done = 0
    for i in range(0, len(keys), chunk):
        batch = keys[i : i + chunk]
        try:
            out = _call_with_retry(url, key, model, batch, engine=kind)
        except Exception as exc:  # noqa: BLE001
            print(f"batch failed, trying one by one: {type(exc).__name__}: {exc}")
            out = []
            for one in batch:
                try:
                    out.extend(_call_with_retry(url, key, model, [one], engine=kind))
                except Exception as one_exc:  # noqa: BLE001
                    preview = one.replace("\n", " ")[:40]
                    print(f"skip {type(one_exc).__name__}: {preview}")
                    out.append("")
        for src_text, dst_text in zip(batch, out):
            data[src_text] = dst_text
        done += len(batch)
        dest.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"progress {done}/{len(keys)}")
        local_qwen.emit("progress", "translate", done=done, total=len(keys), message=f"{done}/{len(keys)}")
    filled = sum(1 for k, v in data.items() if v and v != k)
    print(f"done filled={filled} total={len(data)}")
    return data


def main() -> int:
    argv = list(sys.argv[1:])
    engine = None
    argv, jsonl = local_qwen.take_flag(argv, "--progress-jsonl")
    local_qwen.enable_progress(jsonl)
    if "--local" in argv:
        engine = "local"
        argv = [a for a in argv if a != "--local"]
    if len(argv) < 1:
        print("usage: translate_direct.py [--local] src.json [dst.json]", file=sys.stderr)
        return 2
    src = Path(argv[0])
    dst = Path(argv[1]) if len(argv) > 1 else src
    data = json.loads(src.read_text(encoding="utf-8-sig"))
    if not isinstance(data, dict):
        print("translation file is not a JSON object", file=sys.stderr)
        return 1
    translate_mapping(data, dst, engine=engine)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
