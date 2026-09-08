# Switch MTool 自配 baseUrl+model between Aliyun qwen-mt and Local AI chat.
# Only patches those two fields. Does not print secrets. Does not start MTool.
from __future__ import annotations

import json
import sys
from pathlib import Path

import local_qwen

CONF = Path(r"D:\grok\MTool\Tool\plugins\deepseek_config.json")
BACKUP = Path(r"D:\grok\MTool\Tool\plugins\deepseek_engine_backup.json")


def load_cfg() -> dict:
    return json.loads(CONF.read_text(encoding="utf-8"))


def save_cfg(cfg: dict) -> None:
    CONF.write_text(json.dumps(cfg, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def status() -> int:
    cfg = load_cfg()
    model = str(cfg.get("model") or "")
    url = str(cfg.get("baseUrl") or "")
    kind = "mt" if local_qwen.is_qwen_mt(model) else "chat"
    print(f"model={model}")
    print(f"baseUrl={url}")
    print(f"protocol={kind}")
    print(f"backup_exists={BACKUP.is_file()}")
    return 0


def switch_local() -> int:
    cfg = load_cfg()
    model = str(cfg.get("model") or "")
    url = str(cfg.get("baseUrl") or "")
    already = (
        not local_qwen.is_qwen_mt(model)
        and "18135" in url
        and model == local_qwen.LOCAL_MODEL
    )
    if already:
        print("already local chat")
        print(f"model={local_qwen.LOCAL_MODEL}")
        print(f"baseUrl={local_qwen.LOCAL_BASE}")
        return 0
    if local_qwen.is_qwen_mt(model) or not BACKUP.is_file():
        BACKUP.write_text(
            json.dumps({"baseUrl": url, "model": model}, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"saved restore snapshot {BACKUP.name}")
    cfg["baseUrl"] = local_qwen.LOCAL_BASE
    cfg["model"] = local_qwen.LOCAL_MODEL
    save_cfg(cfg)
    print("switched 自配 to local chat")
    print(f"model={local_qwen.LOCAL_MODEL}")
    print(f"baseUrl={local_qwen.LOCAL_BASE}")
    print("Fully quit MTool (tray too) before the next 自配 run.")
    return 0


def switch_cloud() -> int:
    if not BACKUP.is_file():
        print("no cloud snapshot. 自配 was never switched, or backup is missing.")
        return 1
    snap = json.loads(BACKUP.read_text(encoding="utf-8"))
    url = str(snap.get("baseUrl") or "")
    model = str(snap.get("model") or "")
    if not url or not model:
        print("cloud snapshot missing baseUrl/model")
        return 1
    cfg = load_cfg()
    cfg["baseUrl"] = url
    cfg["model"] = model
    save_cfg(cfg)
    print("restored 自配 to saved cloud engine")
    print(f"model={model}")
    print(f"baseUrl={url}")
    print("Fully quit MTool (tray too) before the next 自配 run.")
    return 0


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    cmd = (argv[0] if argv else "status").strip().lower()
    if cmd in ("status", ""):
        return status()
    if cmd in ("local", "chat"):
        return switch_local()
    if cmd in ("cloud", "mt", "aliyun"):
        return switch_cloud()
    print("usage: switch_mtool_engine.py [status|local|cloud]")
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
