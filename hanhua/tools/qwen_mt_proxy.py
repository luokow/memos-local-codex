"""OpenAI-compatible adapter so LinguaGacha can call qwen-mt-plus.

qwen-mt rejects system/developer roles. This process:
- strips those roles
- adds translation_options
- if the user payload is JSONLINE/JSON of sources, translates the source list
  and rebuilds JSONLINE for LinguaGacha
"""
from __future__ import annotations

import json
import os
import re
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HOST = "127.0.0.1"
PORT = int(os.environ.get("QWEN_MT_PROXY_PORT", "18765"))
CONF = Path(r"D:\grok\MTool\Tool\plugins\deepseek_config.json")
JSONLINE_RE = re.compile(r"\{.*\}")


def load_upstream() -> tuple[str, str, str]:
    cfg = json.loads(CONF.read_text(encoding="utf-8"))
    base = str(cfg["baseUrl"]).rstrip("/")
    if base.endswith("/chat/completions"):
        url = base
    elif base.endswith("/v1"):
        url = base + "/chat/completions"
    else:
        url = base + "/chat/completions"
    return url, str(cfg["apiKey"]), str(cfg["model"])


UP_URL, UP_KEY, UP_MODEL = load_upstream()


def call_qwen(texts: list[str] | str) -> str:
    if isinstance(texts, list):
        content = json.dumps(texts, ensure_ascii=False)
    else:
        content = texts
    payload = {
        "model": UP_MODEL,
        "messages": [{"role": "user", "content": content}],
        "translation_options": {"source_lang": "Japanese", "target_lang": "Chinese"},
    }
    req = urllib.request.Request(
        UP_URL,
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": "Bearer " + UP_KEY,
        },
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=180) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    msg = (data.get("choices") or [{}])[0].get("message") or {}
    out = msg.get("content") or ""
    if isinstance(out, list):
        parts = []
        for item in out:
            if isinstance(item, dict):
                parts.append(str(item.get("text") or item.get("content") or ""))
            else:
                parts.append(str(item))
        out = "".join(parts)
    return str(out).strip()


def extract_jsonline_sources(text: str) -> list[dict] | None:
    rows: list[dict] = []
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line.startswith("{") or not line.endswith("}"):
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(obj, dict):
            rows.append(obj)
    if len(rows) < 1:
        return None
    # LinguaGacha JSONLINE rows carry a source-like field
    keys = set().union(*(row.keys() for row in rows))
    if not ({"source", "src", "text", "original", "ja"} & keys):
        return None
    return rows


def source_key(row: dict) -> str | None:
    for k in ("source", "src", "text", "original", "ja"):
        if k in row and isinstance(row[k], str):
            return k
    return None


def adapt_user_content(user_text: str) -> str:
    rows = extract_jsonline_sources(user_text)
    if rows:
        key = source_key(rows[0])
        if key:
            srcs = [str(row.get(key) or "") for row in rows]
            raw = call_qwen(srcs)
            try:
                translated = json.loads(raw)
                if not isinstance(translated, list):
                    translated = [raw]
            except json.JSONDecodeError:
                translated = [raw]
            while len(translated) < len(rows):
                translated.append("")
            out_lines = []
            for row, dst in zip(rows, translated[: len(rows)]):
                new_row = dict(row)
                new_row["translation"] = dst if isinstance(dst, str) else str(dst)
                out_lines.append(json.dumps(new_row, ensure_ascii=False))
            return "\n".join(out_lines)
    # last user blob: send as-is (already one user message)
    return call_qwen(user_text)


def last_user_text(messages: list) -> str:
    users = []
    for msg in messages or []:
        if not isinstance(msg, dict):
            continue
        if msg.get("role") == "user":
            c = msg.get("content")
            if isinstance(c, str):
                users.append(c)
            elif isinstance(c, list):
                bits = []
                for part in c:
                    if isinstance(part, dict):
                        bits.append(str(part.get("text") or part.get("content") or ""))
                    else:
                        bits.append(str(part))
                users.append("".join(bits))
    if not users:
        return ""
    return users[-1]


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt: str, *args) -> None:
        sys_stderr = __import__("sys").stderr
        sys_stderr.write("[qwen-mt-proxy] " + (fmt % args) + "\n")

    def _json(self, code: int, obj: dict) -> None:
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802
        if self.path in ("/health", "/v1/models", "/models"):
            self._json(200, {
                "object": "list",
                "data": [{"id": UP_MODEL, "object": "model", "owned_by": "local-proxy"}],
            })
            return
        self._json(404, {"error": {"message": "not found"}})

    def do_POST(self) -> None:  # noqa: N802
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b"{}"
        try:
            req = json.loads(raw.decode("utf-8"))
        except json.JSONDecodeError:
            self._json(400, {"error": {"message": "invalid json"}})
            return
        if not self.path.rstrip("/").endswith("chat/completions"):
            self._json(404, {"error": {"message": "only /v1/chat/completions"}})
            return
        user_text = last_user_text(req.get("messages") or [])
        try:
            content = adapt_user_content(user_text)
        except Exception as exc:  # noqa: BLE001
            self._json(502, {"error": {"message": str(exc)}})
            return
        now = int(time.time())
        self._json(200, {
            "id": f"chatcmpl-proxy-{now}",
            "object": "chat.completion",
            "created": now,
            "model": UP_MODEL,
            "choices": [{
                "index": 0,
                "message": {"role": "assistant", "content": content},
                "finish_reason": "stop",
            }],
            "usage": {"prompt_tokens": 0, "completion_tokens": 0, "total_tokens": 0},
        })


def main() -> None:
    httpd = ThreadingHTTPServer((HOST, PORT), Handler)
    print(f"qwen-mt proxy http://{HOST}:{PORT}/v1 model={UP_MODEL}", flush=True)
    httpd.serve_forever()


if __name__ == "__main__":
    main()
