# RPG Maker MV/MZ extract/inject matching Translator++ "MV/MZ Parser V2".
# Consecutive 401 lines after 101 are one cell joined by \n; writeback rebuilds 401 rows.
from __future__ import annotations

import argparse
import copy
import json
import re
import shutil
import sys
from pathlib import Path
from typing import Any

KANA_RE = re.compile(r"[\u3040-\u30ff]")
JP_RE = re.compile(r"[\u3040-\u30ff\u3400-\u9fff]")
MAX_DIALOG_LINE = 4
_require_kana = False
DB_FIELDS = {
    "Actors.json": ("name", "nickname", "profile"),
    "Classes.json": ("name",),
    "Skills.json": ("name", "description", "message1", "message2"),
    "Items.json": ("name", "description"),
    "Weapons.json": ("name", "description"),
    "Armors.json": ("name", "description"),
    "Enemies.json": ("name",),
    "States.json": ("name", "message1", "message2", "message3", "message4"),
    "Troops.json": ("name",),
    "MapInfos.json": ("name",),
}


def is_jp(text: Any) -> bool:
    if not isinstance(text, str):
        return False
    if _require_kana:
        return bool(KANA_RE.search(text))
    return bool(JP_RE.search(text))


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def dump_json(path: Path, data: Any) -> None:
    path.write_text(
        json.dumps(data, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )


def add_item(items: dict[str, str], locations: dict[str, list[dict[str, Any]]], src: str, loc: dict[str, Any]) -> None:
    if not is_jp(src):
        return
    items.setdefault(src, "")
    locations.setdefault(src, []).append(loc)


def extract_event_list(
    lst: list[Any],
    items: dict[str, str],
    locations: dict[str, list[dict[str, Any]]],
    file_name: str,
    owner: str,
) -> None:
    if not isinstance(lst, list):
        return
    i = 0
    while i < len(lst):
        cmd = lst[i] or {}
        code = cmd.get("code")
        params = cmd.get("parameters") or []
        if code == 101:
            speaker = params[4] if len(params) >= 5 else None
            if is_jp(speaker):
                add_item(items, locations, speaker, {
                    "file": file_name, "kind": "speaker", "owner": owner, "index": i,
                })
            j = i + 1
            lines: list[str] = []
            while j < len(lst) and (lst[j] or {}).get("code") == 401:
                line = ((lst[j] or {}).get("parameters") or [""])[0]
                lines.append(line if isinstance(line, str) else str(line))
                j += 1
            if lines:
                add_item(items, locations, "\n".join(lines), {
                    "file": file_name, "kind": "message", "owner": owner,
                    "index": i, "count": len(lines),
                })
            i = j
            continue
        if code == 105:
            j = i + 1
            lines = []
            while j < len(lst) and (lst[j] or {}).get("code") == 405:
                line = ((lst[j] or {}).get("parameters") or [""])[0]
                lines.append(line if isinstance(line, str) else str(line))
                j += 1
            if lines:
                add_item(items, locations, "\n".join(lines), {
                    "file": file_name, "kind": "scroll", "owner": owner,
                    "index": i, "count": len(lines),
                })
            i = j
            continue
        if code == 102 and params and isinstance(params[0], list):
            for ci, choice in enumerate(params[0]):
                if is_jp(choice):
                    add_item(items, locations, choice, {
                        "file": file_name, "kind": "choice", "owner": owner,
                        "index": i, "choice": ci,
                    })
            i += 1
            continue
        if code in (320, 324, 325) and len(params) >= 2 and is_jp(params[1]):
            add_item(items, locations, params[1], {
                "file": file_name, "kind": "actor_field", "owner": owner, "index": i,
            })
        i += 1


def extract_map(path: Path, items: dict[str, str], locations: dict[str, list[dict[str, Any]]]) -> None:
    data = load_json(path)
    name = path.name
    if is_jp(data.get("displayName")):
        add_item(items, locations, data["displayName"], {"file": name, "kind": "displayName"})
    events = data.get("events") or []
    for ev in events:
        if not ev:
            continue
        eid = ev.get("id")
        if is_jp(ev.get("name")):
            add_item(items, locations, ev["name"], {"file": name, "kind": "eventName", "eventId": eid})
        for pi, page in enumerate(ev.get("pages") or []):
            extract_event_list(
                page.get("list") or [], items, locations, name, f"event:{eid}:page:{pi}",
            )


def extract_common(path: Path, items: dict[str, str], locations: dict[str, list[dict[str, Any]]]) -> None:
    data = load_json(path)
    for ev in data or []:
        if not ev:
            continue
        eid = ev.get("id")
        if is_jp(ev.get("name")):
            add_item(items, locations, ev["name"], {"file": path.name, "kind": "ceName", "eventId": eid})
        extract_event_list(ev.get("list") or [], items, locations, path.name, f"common:{eid}")


def extract_db_array(path: Path, fields: tuple[str, ...], items: dict[str, str], locations: dict[str, list[dict[str, Any]]]) -> None:
    data = load_json(path)
    for obj in data or []:
        if not obj:
            continue
        oid = obj.get("id")
        for field in fields:
            val = obj.get(field)
            if is_jp(val):
                add_item(items, locations, val, {
                    "file": path.name, "kind": "db", "id": oid, "field": field,
                })


def extract_system(path: Path, items: dict[str, str], locations: dict[str, list[dict[str, Any]]]) -> None:
    data = load_json(path)
    for key in ("gameTitle", "currencyUnit"):
        if is_jp(data.get(key)):
            add_item(items, locations, data[key], {"file": path.name, "kind": "system", "field": key})
    terms = data.get("terms") or {}
    for group in ("basic", "commands", "params"):
        arr = terms.get(group) or []
        for i, val in enumerate(arr):
            if is_jp(val):
                add_item(items, locations, val, {
                    "file": path.name, "kind": "terms", "group": group, "index": i,
                })
    messages = terms.get("messages") or {}
    if isinstance(messages, dict):
        for k, val in messages.items():
            if is_jp(val):
                add_item(items, locations, val, {
                    "file": path.name, "kind": "termMessage", "key": k,
                })


def find_data_dir(game: Path) -> Path | None:
    game = game.resolve()
    if game.is_file():
        game = game.parent
    candidates = [game / "data", game / "www" / "data"]
    if game.name.lower() == "data":
        candidates.insert(0, game)
    if game.name.lower() == "www":
        candidates.insert(0, game / "data")
    for cand in candidates:
        if (cand / "System.json").is_file():
            return cand
    return None


def data_looks_encrypted(data_dir: Path) -> bool:
    path = data_dir / "System.json"
    if not path.is_file():
        return False
    raw = path.read_bytes()[:16].lstrip()
    if raw.startswith((b"{", b"[", b"\xef\xbb\xbf")):
        return False
    try:
        json.loads(path.read_text(encoding="utf-8-sig"))
        return False
    except (OSError, UnicodeError, json.JSONDecodeError):
        return True


def extract_game(
    data_dir: Path, *, require_kana: bool = False
) -> tuple[dict[str, str], dict[str, list[dict[str, Any]]]]:
    global _require_kana
    items: dict[str, str] = {}
    locations: dict[str, list[dict[str, Any]]] = {}
    prev = _require_kana
    _require_kana = require_kana
    try:
        return _extract_game(data_dir, items, locations)
    finally:
        _require_kana = prev


def _extract_game(
    data_dir: Path,
    items: dict[str, str],
    locations: dict[str, list[dict[str, Any]]],
) -> tuple[dict[str, str], dict[str, list[dict[str, Any]]]]:
    for path in sorted(data_dir.glob("Map*.json")):
        if path.name == "MapInfos.json":
            continue
        extract_map(path, items, locations)
    ce = data_dir / "CommonEvents.json"
    if ce.exists():
        extract_common(ce, items, locations)
    for fname, fields in DB_FIELDS.items():
        fpath = data_dir / fname
        if fpath.exists():
            extract_db_array(fpath, fields, items, locations)
    sys_path = data_dir / "System.json"
    if sys_path.exists():
        extract_system(sys_path, items, locations)
    return items, locations


def rebuild_message(header: dict[str, Any], text: str) -> list[dict[str, Any]]:
    lines = str(text).replace("\r", "").split("\n")
    out: list[dict[str, Any]] = []
    indent = header.get("indent", 0)
    for n, line in enumerate(lines):
        if n % MAX_DIALOG_LINE == 0:
            out.append(copy.deepcopy(header))
        out.append({"code": 401, "indent": indent, "parameters": [line]})
    return out


def rebuild_scroll(header: dict[str, Any], text: str) -> list[dict[str, Any]]:
    lines = str(text).replace("\r", "").split("\n")
    out = [copy.deepcopy(header)]
    indent = header.get("indent", 0)
    for line in lines:
        out.append({"code": 405, "indent": indent, "parameters": [line]})
    return out


def inject_event_list(lst: list[Any], trans: dict[str, str]) -> list[Any]:
    if not isinstance(lst, list):
        return lst
    out: list[Any] = []
    i = 0
    while i < len(lst):
        cmd = lst[i] or {}
        code = cmd.get("code")
        params = cmd.get("parameters") or []
        if code == 101:
            new_cmd = copy.deepcopy(cmd)
            if len(params) >= 5 and params[4] in trans and trans[params[4]]:
                new_cmd["parameters"][4] = trans[params[4]]
            j = i + 1
            lines: list[str] = []
            while j < len(lst) and (lst[j] or {}).get("code") == 401:
                line = ((lst[j] or {}).get("parameters") or [""])[0]
                lines.append(line if isinstance(line, str) else str(line))
                j += 1
            src = "\n".join(lines)
            dst = trans.get(src) if src in trans else None
            if dst:
                out.extend(rebuild_message(new_cmd, dst))
            else:
                out.append(new_cmd)
                out.extend(copy.deepcopy(lst[k]) for k in range(i + 1, j))
            i = j
            continue
        if code == 105:
            j = i + 1
            lines = []
            while j < len(lst) and (lst[j] or {}).get("code") == 405:
                line = ((lst[j] or {}).get("parameters") or [""])[0]
                lines.append(line if isinstance(line, str) else str(line))
                j += 1
            src = "\n".join(lines)
            dst = trans.get(src) if src in trans else None
            if dst:
                out.extend(rebuild_scroll(cmd, dst))
            else:
                out.append(copy.deepcopy(cmd))
                out.extend(copy.deepcopy(lst[k]) for k in range(i + 1, j))
            i = j
            continue
        if code == 102 and params and isinstance(params[0], list):
            new_cmd = copy.deepcopy(cmd)
            new_choices = []
            for choice in new_cmd["parameters"][0]:
                new_choices.append(trans.get(choice) or choice)
            new_cmd["parameters"][0] = new_choices
            out.append(new_cmd)
            i += 1
            continue
        if code in (320, 324, 325) and len(params) >= 2:
            new_cmd = copy.deepcopy(cmd)
            src = new_cmd["parameters"][1]
            if src in trans and trans[src]:
                new_cmd["parameters"][1] = trans[src]
            out.append(new_cmd)
            i += 1
            continue
        out.append(copy.deepcopy(cmd))
        i += 1
    return out


def inject_map(path: Path, trans: dict[str, str]) -> None:
    data = load_json(path)
    dn = data.get("displayName")
    if dn in trans and trans[dn]:
        data["displayName"] = trans[dn]
    events = data.get("events") or []
    for ev in events:
        if not ev:
            continue
        if ev.get("name") in trans and trans[ev["name"]]:
            ev["name"] = trans[ev["name"]]
        for page in ev.get("pages") or []:
            page["list"] = inject_event_list(page.get("list") or [], trans)
    dump_json(path, data)


def inject_common(path: Path, trans: dict[str, str]) -> None:
    data = load_json(path)
    for ev in data or []:
        if not ev:
            continue
        if ev.get("name") in trans and trans[ev["name"]]:
            ev["name"] = trans[ev["name"]]
        ev["list"] = inject_event_list(ev.get("list") or [], trans)
    dump_json(path, data)


def inject_db_array(path: Path, fields: tuple[str, ...], trans: dict[str, str]) -> None:
    data = load_json(path)
    for obj in data or []:
        if not obj:
            continue
        for field in fields:
            val = obj.get(field)
            if val in trans and trans[val]:
                obj[field] = trans[val]
    dump_json(path, data)


def inject_system(path: Path, trans: dict[str, str]) -> None:
    data = load_json(path)
    for key in ("gameTitle", "currencyUnit"):
        val = data.get(key)
        if val in trans and trans[val]:
            data[key] = trans[val]
    terms = data.get("terms") or {}
    for group in ("basic", "commands", "params"):
        arr = terms.get(group) or []
        terms[group] = [trans.get(v) or v for v in arr]
    messages = terms.get("messages") or {}
    if isinstance(messages, dict):
        for k, val in list(messages.items()):
            if val in trans and trans[val]:
                messages[k] = trans[val]
    dump_json(path, data)


def inject_game(data_dir: Path, trans: dict[str, str]) -> int:
    filled = {k: v for k, v in trans.items() if v and v != k}
    for path in sorted(data_dir.glob("Map*.json")):
        if path.name == "MapInfos.json":
            inject_db_array(path, ("name",), filled)
        else:
            inject_map(path, filled)
    ce = data_dir / "CommonEvents.json"
    if ce.exists():
        inject_common(ce, filled)
    for fname, fields in DB_FIELDS.items():
        fpath = data_dir / fname
        if fpath.exists() and fname != "MapInfos.json":
            inject_db_array(fpath, fields, filled)
    sys_path = data_dir / "System.json"
    if sys_path.exists():
        inject_system(sys_path, filled)
    return len(filled)


def cmd_extract(args: argparse.Namespace) -> int:
    data_dir = find_data_dir(Path(args.game))
    if data_dir is None:
        print("missing data dir", file=sys.stderr)
        return 1
    items, locations = extract_game(data_dir)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "ManualTransFile.json").write_text(
        json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    (out / "locations.json").write_text(
        json.dumps({"count": len(items), "locations": locations}, ensure_ascii=False),
        encoding="utf-8",
    )
    actors = data_dir / "Actors.json"
    if actors.exists():
        shutil.copy2(actors, out / "Actors.json")
    print(f"extracted {len(items)} unique strings -> {out / 'ManualTransFile.json'}")
    return 0


def cmd_inject(args: argparse.Namespace) -> int:
    trans_path = Path(args.trans)
    trans = json.loads(trans_path.read_text(encoding="utf-8-sig"))
    if not isinstance(trans, dict):
        print("translation file is not a JSON object", file=sys.stderr)
        return 1
    data_dir = find_data_dir(Path(args.game))
    if data_dir is None:
        print("missing data dir", file=sys.stderr)
        return 1
    filled = inject_game(data_dir, trans)
    empty = sum(1 for k, v in trans.items() if not v or v == k)
    print(f"injected {filled} translations, {empty} still empty/identity")
    return 0


def cmd_stats(args: argparse.Namespace) -> int:
    trans = json.loads(Path(args.trans).read_text(encoding="utf-8-sig"))
    total = len(trans)
    filled = sum(1 for k, v in trans.items() if v and v != k)
    jp_left = sum(1 for v in trans.values() if is_jp(v) and v)
    print(f"total={total} filled={filled} still_jp={jp_left}")
    return 0


def main() -> int:
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    e = sub.add_parser("extract")
    e.add_argument("--game", required=True)
    e.add_argument("--out", required=True)
    i = sub.add_parser("inject")
    i.add_argument("--game", required=True)
    i.add_argument("--trans", required=True)
    s = sub.add_parser("stats")
    s.add_argument("--trans", required=True)
    args = p.parse_args()
    if args.cmd == "extract":
        return cmd_extract(args)
    if args.cmd == "inject":
        return cmd_inject(args)
    return cmd_stats(args)


if __name__ == "__main__":
    raise SystemExit(main())
