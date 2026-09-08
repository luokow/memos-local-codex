# Write LinguaGacha userdata/config.json from the existing MTool API line.
# Adds a Local AI chat entry; does not remove the 18765 qwen-mt adapter.
# Does not print the API key.
from __future__ import annotations

import json
from pathlib import Path

import local_qwen

ROOT = Path(__file__).resolve().parent.parent
MT = Path(r"D:\grok\MTool\Tool\plugins\deepseek_config.json")
LG_ROOT = ROOT / "LinguaGacha"
CUSTOM_ID = "custom-qwen-mt-plus"
LOCAL_ID = "custom-qwen-local-chat"


def upsert_model(models: list, entry: dict) -> list:
    out = [m for m in models if not (isinstance(m, dict) and m.get("id") == entry["id"])]
    out.append(entry)
    return out


def mt_entry(cfg: dict) -> dict:
    return {
        "id": CUSTOM_ID,
        "type": "CUSTOM_OPENAI",
        "name": "Qwen-MT via local adapter",
        "api_format": "OpenAI",
        "api_url": "http://127.0.0.1:18765/v1",
        "api_key": "local-proxy",
        "model_id": str(cfg.get("model") or "qwen-mt-plus"),
        "agent": {"context_window": 0, "max_output_tokens": 0},
        "request": {
            "extra_headers": {},
            "extra_body": {},
            "extra_headers_custom_enable": False,
            "extra_body_custom_enable": False,
        },
        "threshold": {
            "input_token_limit": 512,
            "output_token_limit": 2048,
            "rpm_limit": 0,
            "concurrency_limit": 4,
        },
        "thinking": {"level": "OFF"},
        "generation": {
            "temperature": 0.3,
            "temperature_custom_enable": True,
            "top_p": 0.95,
            "top_p_custom_enable": False,
        },
    }


def local_entry() -> dict:
    return {
        "id": LOCAL_ID,
        "type": "CUSTOM_OPENAI",
        "name": "Local Qwen chat (18135)",
        "api_format": "OpenAI",
        "api_url": local_qwen.LOCAL_BASE,
        "api_key": local_qwen.LOCAL_KEY,
        "model_id": local_qwen.LOCAL_MODEL,
        "agent": {"context_window": 0, "max_output_tokens": 0},
        "request": {
            "extra_headers": {},
            "extra_headers_custom_enable": False,
            "extra_body": {"chat_template_kwargs": {"enable_thinking": False}},
            "extra_body_custom_enable": True,
        },
        "threshold": {
            "input_token_limit": 384,
            "output_token_limit": 1024,
            "rpm_limit": 0,
            "concurrency_limit": 1,
        },
        "thinking": {"level": "OFF"},
        "generation": {
            "temperature": 0.3,
            "temperature_custom_enable": True,
            "top_p": 0.95,
            "top_p_custom_enable": False,
        },
    }


def main() -> None:
    cfg = json.loads(MT.read_text(encoding="utf-8"))
    userdata = LG_ROOT / "userdata"
    userdata.mkdir(parents=True, exist_ok=True)
    conf_path = userdata / "config.json"
    existing = {}
    if conf_path.exists():
        try:
            existing = json.loads(conf_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            existing = {}
    models = existing.get("models")
    if not isinstance(models, list):
        models = []
    models = upsert_model(models, mt_entry(cfg))
    models = upsert_model(models, local_entry())
    selection = existing.get("model_selection")
    if not isinstance(selection, dict):
        selection = {
            "translation": CUSTOM_ID,
            "analysis": CUSTOM_ID,
            "agent": CUSTOM_ID,
        }
    existing.update({
        "app_language": "ZH",
        "source_language": "JA",
        "target_language": "ZH",
        "output_folder_open_on_finish": False,
        "request_timeout": 180,
        "mtool_optimizer_enable": True,
        "skip_duplicate_source_text_enable": True,
        "models": models,
        "model_selection": selection,
    })
    conf_path.write_text(json.dumps(existing, ensure_ascii=False, indent=4), encoding="utf-8")
    print(f"wrote {conf_path}")
    print(f"kept {CUSTOM_ID} url=http://127.0.0.1:18765/v1")
    print(f"added {LOCAL_ID} url={local_qwen.LOCAL_BASE} model={local_qwen.LOCAL_MODEL}")


if __name__ == "__main__":
    main()
