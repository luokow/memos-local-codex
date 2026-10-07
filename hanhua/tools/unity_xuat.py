# Reusable Unity display-layer wrap around BepInEx + XUnity.AutoTranslator.
# Official path: install XUAT, dump Addressable MonoBehaviour/TextAsset
# (UnityPy typed parse + object-bytes UTF-8), fill Text\*.txt, restart to display.
# Do not UTF-16-scan whole bundles (textures become garbage keys).
# No per-game hardcoded lines.
from __future__ import annotations

import os
import re
import shutil
import struct
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path

TOOLS = Path(__file__).resolve().parent
PACK = TOOLS.parent

JP_RE = re.compile(r"[\u3040-\u30ff\u4e00-\u9fff]")
HIRA_RE = re.compile(r"[\u3041-\u3096]")
KATA_RE = re.compile(r"[\u30A1-\u30FA\u30FC]")
HAN_RE = re.compile(r"[\u4e00-\u9fff]")
CTRL_RE = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f]")
JP_RAW_U8 = re.compile(
    rb"(?:[\xe3][\x80-\xbf][\x80-\xbf]|[\xe4-\xe9][\x80-\xbf][\x80-\xbf]|[\xef][\xbc-\xbd][\x80-\xbf]){2,}"
)
DUMP_SKIP = (
    "shader",
    "uniform",
    "float",
    "vector",
    "texture",
    "material",
    "addressable",
    "m_script",
)
PREEXTRACT_NAME = "00_preextract.txt"
TERMS_NAME = "00_terms.txt"
VARIANT_NAME = "07_variants.txt"
UNMATCHED_NAME = "08_unmatched.txt"
GALTRANSL_DICT_DIR = TOOLS / "vendor" / "galtransl_dict"
GPT_DICT_FILE = GALTRANSL_DICT_DIR / "GPT字典.txt"
# Official dests that fight galge H, or substring-hit オーダーメイド.
GPT_DICT_SKIP = frozenset({"責め", "膣", "メイド"})
GLOSSARY_HAYSTACK_SKIP = frozenset({TERMS_NAME, VARIANT_NAME, "00_missing_loc.txt"})
TERMS_STUB = (
    "# 本盘术语，预填时交给 GalTransl（src->dst）。\n"
    "# 只写短词/专名，不要写整句对白。下一盘另写一份。\n"
    "# 格式与词库相同：\n"
    "# 固有名词=译名\n"
)
UNMATCHED_PREFIX = "unmatched "
ELLIPSIS_PREFIXES = ("……", "…", "⋯", "...")
TRAILING_PUNCT = ("?", "？", "!", "！")
SPRITE_TAIL_RE = re.compile(r"[\ue000-\uf8ff♡♥❤]+$")
SPRITE_TAG_RE = re.compile(r"(?:<sprite\\?=\d+>)+$", re.I)
SPRITE_TAG_ANY_RE = re.compile(r"<sprite\\?=\d+>", re.I)
COLOR_OPEN_RE = re.compile(r"<color=[^>]+>", re.I)
COLOR_CLOSE_RE = re.compile(r"</color>", re.I)
STRAY_COLOR_RE = re.compile(r"<color>(?!=)", re.I)
TMP_TAG_RE = re.compile(r"</?[^>]+>")
# JP decorative TMP fonts often have 達/屍 but not 达/尸. Only swap these pairs.
MISSING_LOC_RE = re.compile(
    r"^No translation found for ['‘'](.+)['’'] in ([A-Za-z0-9_]+)$"
)
SKIP_EXE = {
    "unitycrashhandler32.exe",
    "unitycrashhandler64.exe",
    "unitycrashhandler.exe",
    "unityplayer.exe",
}

# Shared UI only. Game names / story lines stay in that game's Text\*.txt.
UI_GLOSSARY = {
    "はい": "是",
    "いいえ": "否",
    "話す": "对话",
    "はじめる": "开始",
    "つづきから": "继续",
    "おまけ": "附录",
    "とじる": "关闭",
    "閉じる": "关闭",
    "もどる": "返回",
    "戻る": "返回",
    "設定": "设置",
    "せってい": "设置",
    "タイトルに戻る": "返回标题",
    "タイトルへ戻る": "返回标题",
    "ゲームに戻る": "返回游戏",
    "ゲームを終了する": "结束游戏",
    "終了する": "结束",
    "フルスクリーン": "全屏",
    "セーブ": "存档",
    "ロード": "读档",
    "マニュアルセーブ": "手动存档",
    "オートセーブ": "自动存档",
    "プロローグ": "序章",
    "エピローグ": "终章",
}

# Generic galge terms for GalTransl + dest polish. Not a game's story lines.
DEFAULT_TERM_GLOSSARY = {
    "おちんちん": "肉棒",
    "おちんぽ": "肉棒",
    "ディルド君": "假阳具",
    "ディルド": "假阳具",
    "サキュバス": "魅魔",
    "お口マンコ": "嘴穴",
    "サンドバッグ": "泄欲沙袋",
    "どうしよっか": "做什么好呢",
    "はむあみゅ": "唔呣唔",
    "はむはみゅ": "唔呣唔呣",
    "はむはむ": "唔呣唔呣",
    "はむ": "唔呣",
    "ビクビク": "一抽一抽",
    "ビクンビクン": "一抽一抽",
    "ヒクヒク": "一抽一抽",
    "ドクドク": "一股股",
    "どくどく": "一股股",
    "れろれろ": "舔舔",
    "はいはい": "好啦好啦",
    "オカズ": "配菜",
    "これこれ": "对对",
    "ストローク": "套弄",
    "生挿入": "无套插入",
    "デカブラ": "大号胸罩",
    "オナホ": "飞机杯",
    "我慢汁": "前液",
    "おっぱい": "奶子",
    "タマタマ": "蛋蛋",
    "キンタマ": "蛋蛋",
    "ヒクつく": "抽动",
}

# GalTransl often uses these dests for the JP term. Longest slip first.
TERM_DEST_SLIPS: dict[str, tuple[str, ...]] = {
    "教習": ("驾驶课程", "驾校", "驾驶", "讲习", "培训"),
    "お口マンコ": ("嘴巴小穴",),
    "はいはい": ("好好好",),
    "オカズ": ("助兴品", "助兴剂", "助兴对象"),
    "これこれ": ("这个这个",),
    "生挿入": ("中途插入", "生插入", "生插"),
    "デカブラ": (
        "巨大鸡巴",
        "大鸡鸡",
        "大奶罩",
        "大奶炮",
        "大奶子",
        "大胸脯",
        "大屁股",
        "大炮",
        "大屌",
        "巨乳",
        "大奶",
    ),
    "ディルド": (
        "假鸡君",
        "假鸡炮",
        "假鸡巴",
        "假鸡儿",
        "假鸡鸡",
        "假屌",
        "自慰棒",
        "假鸡",
    ),
    "サキュバス": ("梦魔",),
    "オナホ": ("自慰器",),
    "我慢汁": ("先走液", "忍耐汁"),
    "おっぱい": ("胸部",),
    "タマタマ": ("睾丸",),
    "キンタマ": ("睾丸",),
}

# (src needles, bad dest, good dest). Only rewrite when src actually has the JP.
COMMON_DEST_FIXES: tuple[tuple[tuple[str, ...], str, str], ...] = (
    (("おちんちん", "おちんぽ"), "小鸡鸡", "肉棒"),
    (("おちんちん", "おちんぽ"), "小弟弟", "肉棒"),
    (("おちんちん", "おちんぽ"), "鸡鸡", "肉棒"),
    (("ディルド",), "假鸡炮", "假阳具"),
    (("ディルド",), "假鸡巴", "假阳具"),
    (("ディルド",), "假鸡儿", "假阳具"),
    (("ディルド",), "假屌", "假阳具"),
    (("ディルド",), "假鸡鸡", "假阳具"),
    (("まんこ穴",), "穴口", "小穴"),
    (("サンドバッグ",), "沙包", "泄欲沙袋"),
    (("相棒",), "搭档假阳具", "老搭档假阳具"),
    (("途中脱落",), "中途脱队", "中途撑不住"),
    (("カリ高",), "这么大的龟头", "冠沟这么高"),
    (("二人",), "两个人", "两人"),
    (("どくどく", "ドクドク"), "咕嘟咕嘟", "一股股"),
    (("はむあみゅ",), "哈呣啊咪呜", "唔呣唔"),
    (("はむあみゅ",), "哈呣啊咪啾", "唔呣唔"),
    (("はむあみゅ",), "哈唔啊唔", "唔呣唔"),
    (("はむはみゅ",), "哈呣哈咪啾", "唔呣唔呣"),
    (("はむはみゅ",), "哈呣哈咪", "唔呣唔呣"),
    (("はむはみゅ",), "哈唔哈唔", "唔呣唔呣"),
    (("はむはむ",), "哈呣哈呣", "唔呣唔呣"),
    (("あむあみゅ",), "啊唔啊咪唔", "唔呣唔"),
    (("あむあみゅ",), "啊呣啊咪", "唔呣唔"),
    (("あむあみゅ",), "啊唔啊唔", "唔呣唔"),
    (("あむはみゅ",), "啊唔哈咪", "唔呣唔"),
    (("あむはみゅ",), "啊唔哈唔", "唔呣唔"),
    (("あむはむ",), "啊呣哈呣", "唔呣唔呣"),
    (("あむあみゅ", "あむ"), "啊唔", "唔"),
    (("はむ", "はぁむ", "は〜む", "はーむ", "はみゅ"), "哈呣", "唔呣"),
    (("はむ", "はぁむ", "は〜む", "はーむ", "はみゅ"), "哈唔", "唔呣"),
    (("ビクビク", "ビクンビクン", "ビクッビクッ", "ヒクヒク", "ヒクつ"), "一颤一颤", "一抽一抽"),
    (("ビク",), "一跳一跳", "一抽一抽"),
    (("脈動", "脈打"), "脉动着", "跳动着"),
    (("脈動", "脈打"), "脉动", "跳动"),
    (("ちろちろ",), "舔得滋噜滋噜的", "舔个不停"),
    (("れろ",), "滋噜滋噜", "舔舔"),
    (("れろ",), "滋噜噜", "舔舔"),
    (("れろ",), "滋噜", "舔"),
    (("れろ",), "咧咯咧咯", "舔舔"),
    (("れろ",), "咧咯", "舔"),
    (("ぞりゅ", "ずりゅ"), "滋噜噜", "啾噜噜"),
    (("ぞりゅ", "ずりゅ"), "滋噜", "啾噜"),
    (("れぇ",), "滋噜噜噜", "嗯——"),
    (("れぇ",), "舔～～～", "嗯——"),
    (("れぇ",), "舔～～", "嗯——"),
    (("れぇ",), "来……舔舔……请吧", "来……嗯——……请吧"),
    (("ストローク",), "抽插", "套弄"),
    (("貴方",), "您", "你"),
    (("フェラされる",), "被她口交", "被口交"),
    (("糸",), "丝线", "丝"),
    (("好きにしていい",), "随你处置", "随便用"),
    (("舌",), "这根沾满了唾液的舌头", "这条沾满了唾液的舌头"),
    (("舌",), "这根舌头", "这条舌头"),
    (("作り始め",), "制造起", "酝酿"),
    (("かける用の精子",), "精子", "精液"),
    (("捻り上げ",), "拧转蛋蛋", "往上挤蛋蛋"),
    (("ぶっかけ",), "把精液射出来", "把精液射在身上"),
    (("セックスできる",), "继续做爱", "做爱"),
    (("入れたい", "イれたい"), "想射在", "想放进"),
    (("公開", "開示"), "公開", "公开"),
    (("デカブラ",), "这根", "这件"),
    (("デカブラ",), "那根", "那件"),
    (("怖くない",), "没什么可怕的", "没什么好怕的"),
    (("大丈夫",), "不用担心啦", "放心啦"),
    (("ヒクつ", "ヒクヒク"), "颤抖起来", "抽动起来"),
    (("ヒクつ", "ヒクヒク"), "颤抖", "抽动"),
    (("足が擦れる",), "摩擦着腿", "摩擦着双腿"),
    (("糸",), "牵出一条丝", "拉出一条丝"),
)

MISSING_LOC_NOTE = (
    "# Unity missing-table English is handled per-game in 00_gamecommon.txt.\n"
)

IL2CPP_BEPINEX = "BepInEx-IL2CPP-be785.zip"
IL2CPP_XUAT = "XUnity.AutoTranslator-BepInEx-IL2CPP-5.6.2.zip"
MONO_BEPINEX = "BepInEx_win_x64_5.4.23.2.zip"
MONO_XUAT = "XUnity.AutoTranslator-BepInEx-5.6.2.zip"
# Official GitHub assets. Local vendor wins; download only when missing.
# IL2CPP BepInEx prefers vendored BE 785; GitHub fallback is 6.0.0-pre.2.
PLUGIN_DOWNLOADS = {
    MONO_BEPINEX: (
        "https://github.com/BepInEx/BepInEx/releases/download/"
        "v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip"
    ),
    "BepInEx_win_x86_5.4.23.2.zip": (
        "https://github.com/BepInEx/BepInEx/releases/download/"
        "v5.4.23.2/BepInEx_win_x86_5.4.23.2.zip"
    ),
    MONO_XUAT: (
        "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/"
        "v5.6.2/XUnity.AutoTranslator-BepInEx-5.6.2.zip"
    ),
    IL2CPP_XUAT: (
        "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/"
        "v5.6.2/XUnity.AutoTranslator-BepInEx-IL2CPP-5.6.2.zip"
    ),
    "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip": (
        "https://github.com/BepInEx/BepInEx/releases/download/"
        "v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip"
    ),
    "BepInEx-Unity.IL2CPP-win-x86-6.0.0-pre.2.zip": (
        "https://github.com/BepInEx/BepInEx/releases/download/"
        "v6.0.0-pre.2/BepInEx-Unity.IL2CPP-win-x86-6.0.0-pre.2.zip"
    ),
}
NAME_HONORIFIC_RE = re.compile(
    r"([\u4e00-\u9fff\u30A1-\u30FA]{2,6})(?:さん|ちゃん|くん|さま)"
)

HARMONY_UNSUPPORTED = (
    "Harmony is not supported in this runtime and no alternate MonoMod hook"
)
XUAT_LOADING = "Loading [XUnity Auto Translator"
BRUTEFORCE_LOADING = "Loading [AutoTranslator.IL2CPP.BruteForceFix"
SET_TEXT_HOOK_FAIL = "Cannot hook 'TMPro.TMP_Text.set_text'"
HARMONY_FLOOD_MIN = 5
BRUTEFORCE_DLL = "AutoTranslator.IL2CPP.BruteForceFix.dll"


@dataclass(frozen=True)
class HookReport:
    exists: bool
    xuat_loaded: bool
    bruteforce_loaded: bool
    harmony_unsupported: int
    set_text_hook_failed: bool
    ok: bool
    code: str
    message: str


def bepinex_log_path(root: Path) -> Path:
    return root / "BepInEx" / "LogOutput.log"


def diagnose_hooks(root: Path, text: str | None = None) -> HookReport:
    """Read an existing BepInEx log. No log is not a failure (game never launched)."""
    path = bepinex_log_path(root)
    if text is None:
        if not path.is_file():
            return HookReport(
                exists=False,
                xuat_loaded=False,
                bruteforce_loaded=False,
                harmony_unsupported=0,
                set_text_hook_failed=False,
                ok=True,
                code="no_log",
                message="",
            )
        text = path.read_text(encoding="utf-8", errors="replace")
    harmony_n = text.count(HARMONY_UNSUPPORTED)
    xuat_loaded = XUAT_LOADING in text
    bruteforce_loaded = BRUTEFORCE_LOADING in text
    set_text_failed = SET_TEXT_HOOK_FAIL in text
    dll = root / "BepInEx" / "plugins" / BRUTEFORCE_DLL
    backend = detect_backend(root)

    if not xuat_loaded:
        return HookReport(
            exists=True,
            xuat_loaded=False,
            bruteforce_loaded=bruteforce_loaded,
            harmony_unsupported=harmony_n,
            set_text_hook_failed=set_text_failed,
            ok=False,
            code="xuat_missing",
            message=(
                "BepInEx 日志里没有 XUnity Auto Translator。"
                "用点我启动汉化版.bat 启动，不要直接开 exe。"
            ),
        )
    if harmony_n >= HARMONY_FLOOD_MIN or set_text_failed:
        return HookReport(
            exists=True,
            xuat_loaded=True,
            bruteforce_loaded=bruteforce_loaded,
            harmony_unsupported=harmony_n,
            set_text_hook_failed=set_text_failed,
            ok=False,
            code="harmony_flood",
            message=(
                "XUAT 文本钩全挂（Harmony is not supported）。"
                "IL2CPP 不要开 ForceMonoModHooks。"
                "词库再满也不上屏。完全退出后用点我启动汉化版.bat 再开，"
                "确认 LogOutput 不再出现该警告。"
            ),
        )
    if backend == "il2cpp" and dll.is_file() and not bruteforce_loaded:
        return HookReport(
            exists=True,
            xuat_loaded=True,
            bruteforce_loaded=False,
            harmony_unsupported=harmony_n,
            set_text_hook_failed=set_text_failed,
            ok=False,
            code="bruteforce_missing",
            message=(
                "XUAT 已加载但 BruteForceFix 没加载。"
                "IL2CPP 的 SetCharArray 对白不会上中文。"
                "依赖不要钉 XUAT 版本号。完全退出再开，"
                "确认 LogOutput 有 Loading [AutoTranslator.IL2CPP.BruteForceFix。"
            ),
        )
    return HookReport(
        exists=True,
        xuat_loaded=True,
        bruteforce_loaded=bruteforce_loaded,
        harmony_unsupported=harmony_n,
        set_text_hook_failed=set_text_failed,
        ok=True,
        code="ok",
        message="",
    )


def log(msg: str) -> None:
    print(msg, flush=True)


def vendor_dirs() -> list[Path]:
    extra = str(os.environ.get("HANHUA_UNITY_VENDOR") or "").strip()
    dirs = [TOOLS / "vendor" / "unity", Path(r"D:\grok\circlemate_hanhua\dl")]
    if extra:
        dirs.insert(0, Path(extra))
    return dirs


def find_zip(name: str) -> Path | None:
    for folder in vendor_dirs():
        hit = folder / name
        if hit.is_file():
            return hit
    return None


def pe_is_64bit(exe: Path) -> bool:
    try:
        with exe.open("rb") as fh:
            if fh.read(2) != b"MZ":
                return True
            fh.seek(0x3C)
            pe = struct.unpack("<I", fh.read(4))[0]
            fh.seek(pe)
            if fh.read(4) != b"PE\x00\x00":
                return True
            machine = struct.unpack("<H", fh.read(2))[0]
        return machine == 0x8664
    except OSError:
        return True


def download_vendor_zip(name: str, url: str) -> Path:
    dest = TOOLS / "vendor" / "unity" / name
    dest.parent.mkdir(parents=True, exist_ok=True)
    if dest.is_file():
        return dest
    log(f"下载 {name}")
    req = urllib.request.Request(url, headers={"User-Agent": "hanhua-unity"})
    tmp = dest.with_suffix(dest.suffix + ".part")
    with urllib.request.urlopen(req, timeout=120) as resp, tmp.open("wb") as out:
        shutil.copyfileobj(resp, out)
    if not zipfile.is_zipfile(tmp):
        tmp.unlink(missing_ok=True)
        raise FileNotFoundError(f"下载的 {name} 不是 zip: {url}")
    os.replace(tmp, dest)
    return dest


def plugin_zip_names(backend: str, exe: Path | None = None) -> list[tuple[str, str | None]]:
    x64 = True if exe is None else pe_is_64bit(exe)
    if backend == "il2cpp":
        be_fallback = (
            "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip"
            if x64
            else "BepInEx-Unity.IL2CPP-win-x86-6.0.0-pre.2.zip"
        )
        return [
            (IL2CPP_BEPINEX, None),
            (be_fallback, PLUGIN_DOWNLOADS.get(be_fallback)),
            (IL2CPP_XUAT, PLUGIN_DOWNLOADS.get(IL2CPP_XUAT)),
        ]
    if backend == "mono":
        be = MONO_BEPINEX if x64 else "BepInEx_win_x86_5.4.23.2.zip"
        return [
            (be, PLUGIN_DOWNLOADS.get(be)),
            (MONO_XUAT, PLUGIN_DOWNLOADS.get(MONO_XUAT)),
        ]
    return []


def ensure_plugin_zips(backend: str, exe: Path | None = None) -> list[Path]:
    be_path: Path | None = None
    xuat_path: Path | None = None
    for name, url in plugin_zip_names(backend, exe):
        is_xuat = "AutoTranslator" in name
        if is_xuat and xuat_path is not None:
            continue
        if not is_xuat and be_path is not None:
            continue
        hit = find_zip(name)
        if hit is None and url:
            hit = download_vendor_zip(name, url)
        if hit is None:
            continue
        if is_xuat:
            xuat_path = hit
        else:
            be_path = hit
        if be_path and xuat_path:
            break
    if be_path is None or xuat_path is None:
        missing = []
        if be_path is None:
            missing.append("BepInEx")
        if xuat_path is None:
            missing.append("XUnity.AutoTranslator")
        raise FileNotFoundError(
            "缺少插件包: "
            + ", ".join(missing)
            + f"。放到 {TOOLS / 'vendor' / 'unity'} 后再拖一次。"
        )
    return [be_path, xuat_path]


def find_unity_root(path: Path) -> Path | None:
    path = path.expanduser()
    if path.is_file():
        path = path.parent
    if not path.is_dir():
        return None
    path = path.resolve()
    if path.name.endswith("_Data"):
        path = path.parent
    cur = path
    for _ in range(4):
        if _is_unity_root(cur) and find_game_exe(cur):
            return cur
        if cur.parent == cur:
            break
        cur = cur.parent
    hits = _collect_unity_roots(path, max_depth=2)
    if len(hits) == 1:
        return hits[0]
    return None


def _collect_unity_roots(path: Path, max_depth: int) -> list[Path]:
    hits: list[Path] = []

    def walk(cur: Path, depth: int) -> None:
        if len(hits) > 2 or depth > max_depth:
            return
        try:
            children = list(cur.iterdir())
        except OSError:
            return
        for child in children:
            if not child.is_dir():
                continue
            if _is_unity_root(child) and find_game_exe(child):
                hits.append(child)
                if len(hits) > 2:
                    return
            walk(child, depth + 1)

    if _is_unity_root(path) and find_game_exe(path):
        hits.append(path)
    walk(path, 1)
    return hits


def _is_unity_root(root: Path) -> bool:
    if (root / "UnityPlayer.dll").is_file():
        return True
    if (root / "GameAssembly.dll").is_file():
        return True
    try:
        return any(p.is_dir() and p.name.endswith("_Data") for p in root.iterdir())
    except OSError:
        return False


def detect_backend(root: Path) -> str:
    if (root / "GameAssembly.dll").is_file():
        return "il2cpp"
    data_dirs = [p for p in root.glob("*_Data") if p.is_dir()]
    for data in data_dirs:
        if (data / "Managed").is_dir():
            return "mono"
    if data_dirs:
        return "il2cpp"
    return "unknown"


def find_game_exe(root: Path) -> Path | None:
    data_stems = {p.name[: -len("_Data")] for p in root.glob("*_Data") if p.is_dir()}
    exes = [
        p
        for p in root.glob("*.exe")
        if p.name.lower() not in SKIP_EXE and not p.name.lower().startswith("unity")
    ]
    for exe in exes:
        if exe.stem in data_stems:
            return exe
    return exes[0] if exes else None


def text_dir(root: Path) -> Path:
    return root / "BepInEx" / "Translation" / "zh-CN" / "Text"


def xuat_unescape(s: str) -> str:
    out: list[str] = []
    table = {"n": "\n", "r": "\r", "t": "\t", "=": "=", "\\": "\\"}
    i = 0
    while i < len(s):
        if s[i] == "\\" and i + 1 < len(s):
            out.append(table.get(s[i + 1], s[i + 1]))
            i += 2
        else:
            out.append(s[i])
            i += 1
    return "".join(out)


def xuat_escape(s: str) -> str:
    return (
        s.replace("\\", "\\\\")
        .replace("\r", "\\r")
        .replace("\n", "\\n")
        .replace("\t", "\\t")
        .replace("=", "\\=")
    )


def xuat_split(line: str) -> tuple[str, str] | None:
    i = 0
    while i < len(line):
        if line[i] == "=" and (i == 0 or line[i - 1] != "\\"):
            return line[:i], line[i + 1 :]
        i += 1
    return None


def parse_xuat(path: Path) -> dict[str, str]:
    data: dict[str, str] = {}
    if not path.is_file():
        return data
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        if not line or line.lstrip().startswith("#") or "=" not in line:
            continue
        parts = xuat_split(line)
        if not parts:
            continue
        src, dst = xuat_unescape(parts[0]), xuat_unescape(parts[1])
        data[src] = dst
    return data


def write_xuat(path: Path, mapping: dict[str, str], order: list[str] | None = None) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    keys = order if order is not None else list(mapping)
    lines = ["\ufeff"]
    for src in keys:
        dst = mapping.get(src, "")
        if dst == "":
            continue
        lines.append(f"{xuat_escape(src)}={xuat_escape(dst)}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def looks_jp(text: str) -> bool:
    return bool(JP_RE.search(text))


def dump_looks_jp(text: str) -> bool:
    if not text or len(text) < 2 or len(text) > 800:
        return False
    if not text.strip() or CTRL_RE.search(text):
        return False
    low = text.lower()
    if any(x in low for x in DUMP_SKIP):
        return False
    if text.startswith("/") or "://" in text:
        return False
    h = len(HIRA_RE.findall(text))
    k = len(KATA_RE.findall(text))
    n = len(HAN_RE.findall(text))
    if h + k == 0:
        return n >= 2
    return True


def _walk_dump_strings(obj, out: set[str], depth: int = 0) -> None:
    if depth > 18:
        return
    if isinstance(obj, str):
        if dump_looks_jp(obj):
            out.add(obj)
        return
    if isinstance(obj, dict):
        for value in obj.values():
            _walk_dump_strings(value, out, depth + 1)
        return
    if isinstance(obj, (list, tuple)):
        for value in obj:
            _walk_dump_strings(value, out, depth + 1)


def iter_unity_asset_files(root: Path) -> list[Path]:
    files: list[Path] = []
    for data in root.glob("*_Data"):
        aa = data / "StreamingAssets" / "aa"
        if aa.is_dir():
            files.extend(sorted(aa.rglob("*.bundle")))
        for name in ("data.unity3d", "resources.assets", "sharedassets0.assets"):
            path = data / name
            if path.is_file():
                files.append(path)
    return files


def iter_bytecode_strings(data: bytes) -> list[str]:
    """Length-prefixed UTF-8 in Nemk GameScript bytecode (opcode 6 + u32le)."""
    found: list[str] = []
    i = 0
    n = len(data)
    while i + 5 <= n:
        if data[i] != 6:
            i += 1
            continue
        ln = struct.unpack_from("<I", data, i + 1)[0]
        start = i + 5
        if ln < 2 or ln > 800 or start + ln > n:
            i += 1
            continue
        chunk = data[start : start + ln]
        try:
            text = chunk.decode("utf-8")
        except UnicodeDecodeError:
            i += 1
            continue
        if "\x00" in text or not dump_looks_jp(text):
            i += 1
            continue
        found.append(text)
        i = start + ln
    return found


def bytecode_display_keys(src: str) -> list[str]:
    """Stored script tag vs TMP private-use sprite on screen."""
    keys = [src]
    alt = SPRITE_TAG_ANY_RE.sub("\ue000", src)
    if alt != src:
        keys.append(alt)
    return keys


def dump_file_jp(path: Path) -> set[str]:
    try:
        import UnityPy
    except ImportError:
        return set()
    found: set[str] = set()
    try:
        env = UnityPy.load(str(path))
    except Exception:
        return found
    for obj in env.objects:
        if obj.type.name not in ("MonoBehaviour", "TextAsset"):
            continue
        try:
            parsed = obj.parse_as_dict()
            _walk_dump_strings(parsed, found)
            bc = parsed.get("bytecode") if isinstance(parsed, dict) else None
            if bc is not None:
                raw_bc = bytes(bc) if not isinstance(bc, (bytes, bytearray)) else bytes(bc)
                for text in iter_bytecode_strings(raw_bc):
                    found.update(bytecode_display_keys(text))
        except Exception:
            pass
        try:
            raw = obj.get_raw_data()
        except Exception:
            raw = None
        if not raw:
            continue
        for text in iter_bytecode_strings(raw):
            found.update(bytecode_display_keys(text))
        for match in JP_RAW_U8.finditer(raw):
            try:
                text = match.group().decode("utf-8")
            except Exception:
                continue
            if dump_looks_jp(text):
                found.add(text)
    return found


def unmatched_has_kana(text: str) -> bool:
    return bool(HIRA_RE.search(text) or KATA_RE.search(text))


def parse_unmatched_log(text: str) -> list[str]:
    found: list[str] = []
    seen: set[str] = set()
    for line in text.splitlines():
        idx = line.find(UNMATCHED_PREFIX)
        if idx < 0:
            continue
        src = xuat_unescape(line[idx + len(UNMATCHED_PREFIX) :].strip())
        if not src or src in seen or not unmatched_has_kana(src):
            continue
        seen.add(src)
        found.append(src)
    return found


def compose_from_map(src: str, mapping: dict[str, str]) -> str | None:
    cur = mapping.get(src, "")
    if cur and not is_identity(src, cur) and not HIRA_RE.search(cur):
        return cur
    parts = src.split("\n")
    if len(parts) > 1:
        dests: list[str] = []
        for part in parts:
            if not part:
                dests.append("")
                continue
            got = compose_from_map(part, mapping)
            if not got:
                return None
            dests.append(got)
        return "\n".join(dests)
    for pref in ELLIPSIS_PREFIXES:
        if not src.startswith(pref):
            continue
        rest = src[len(pref) :]
        if not rest:
            continue
        got = compose_from_map(rest, mapping)
        if got:
            return pref + got
    for pref in ELLIPSIS_PREFIXES:
        if not src.endswith(pref):
            continue
        rest = src[: -len(pref)]
        if not rest:
            continue
        got = compose_from_map(rest, mapping)
        if got:
            return got + pref
    for punct in TRAILING_PUNCT:
        if not src.endswith(punct):
            continue
        rest = src[: -len(punct)]
        if not rest:
            continue
        got = compose_from_map(rest, mapping)
        if got:
            return got + punct
    sprite = SPRITE_TAIL_RE.search(src)
    if sprite and sprite.start() > 0:
        got = compose_from_map(src[: sprite.start()], mapping)
        if got:
            return got + sprite.group()
    tag = SPRITE_TAG_RE.search(src)
    if tag and tag.start() > 0:
        got = compose_from_map(src[: tag.start()], mapping)
        if got:
            return got + tag.group()
    for sep in ELLIPSIS_PREFIXES:
        if sep not in src:
            continue
        parts = src.split(sep)
        if len(parts) < 2:
            continue
        dests: list[str] = []
        ok = True
        for part in parts:
            if not part:
                dests.append("")
                continue
            got = compose_from_map(part, mapping)
            if not got:
                ok = False
                break
            dests.append(got)
        if ok and any(dests):
            return sep.join(dests)
    return None


def variant_pairs(src: str, dst: str) -> list[tuple[str, str]]:
    """One-level runtime decorations: ellipsis prefix/suffix and trailing punct."""
    if (
        not src
        or not dst
        or is_regex_key(src)
        or is_identity(src, dst)
        or HIRA_RE.search(dst)
    ):
        return []
    out: list[tuple[str, str]] = []
    if not src.startswith(ELLIPSIS_PREFIXES):
        for pref in ELLIPSIS_PREFIXES:
            out.append((pref + src, pref + dst))
    if not src.endswith(ELLIPSIS_PREFIXES):
        for pref in ELLIPSIS_PREFIXES:
            out.append((src + pref, dst + pref))
    if not src.endswith(TRAILING_PUNCT):
        for punct in TRAILING_PUNCT:
            out.append((src + punct, dst + punct))
    if not src.endswith(ELLIPSIS_PREFIXES) and not src.endswith(TRAILING_PUNCT):
        for pref in ELLIPSIS_PREFIXES:
            for punct in TRAILING_PUNCT:
                out.append((src + pref + punct, dst + pref + punct))
    return out


def expand_variant_keys(folder: Path) -> int:
    """Write exact keys for dump dests plus runtime …… / ? / !. Not display matching."""
    if not folder.is_dir():
        return 0
    mapping: dict[str, str] = {}
    for path in sorted(folder.glob("*.txt")):
        if path.name.startswith("_Misses") or path.name == VARIANT_NAME:
            continue
        mapping.update(parse_xuat(path))
    variants: dict[str, str] = {}
    for src, dst in mapping.items():
        for vs, vd in variant_pairs(src, dst):
            cur = mapping.get(vs, "")
            if cur and not is_identity(vs, cur) and not HIRA_RE.search(cur):
                continue
            variants[vs] = vd
    write_xuat(folder / VARIANT_NAME, variants)
    log(f"variant keys {len(variants)} -> {VARIANT_NAME}")
    return len(variants)


def migrate_unmatched_file(folder: Path) -> None:
    """00_unmatched sorted before 01_filled and lost to identity dests."""
    old = folder / "00_unmatched.txt"
    dest = folder / UNMATCHED_NAME
    if not old.is_file() or old.resolve() == dest.resolve():
        return
    merged = parse_xuat(old)
    if dest.is_file():
        merged.update(parse_xuat(dest))
    if merged:
        write_xuat(dest, merged)
    old.unlink()


def ingest_unmatched_log(root: Path, text: str | None = None) -> int:
    """Turn BruteForceFix unmatched lines into exact glossary keys.

    Dump splits newline / drops runtime ellipsis. Screen text is the joined form.
    Write the exact keys; do not add more display-side matching heuristics.
    """
    folder = text_dir(root)
    folder.mkdir(parents=True, exist_ok=True)
    migrate_unmatched_file(folder)
    log_path = bepinex_log_path(root)
    if text is None:
        if not log_path.is_file():
            expand_variant_keys(folder)
            return 0
        text = log_path.read_text(encoding="utf-8", errors="replace")
    keys = parse_unmatched_log(text)
    if not keys:
        expand_variant_keys(folder)
        return 0
    mapping = load_all_maps(folder)
    extracted = parse_xuat(folder / UNMATCHED_NAME) if (folder / UNMATCHED_NAME).is_file() else {}
    added = 0
    for src in keys:
        cur = mapping.get(src, "")
        if cur and not is_identity(src, cur) and not HIRA_RE.search(cur):
            continue
        dst = compose_from_map(src, mapping) or extracted.get(src) or src
        if dst and dst != src:
            dst = polish_filled_dest(src, dst, terms=load_term_dests(folder))
        if src not in extracted:
            added += 1
        extracted[src] = dst
        mapping[src] = dst
    if extracted:
        write_xuat(folder / UNMATCHED_NAME, extracted)
        log(f"unmatched 收进 {UNMATCHED_NAME} new={added} total={len(extracted)}")
    expand_variant_keys(folder)
    return added


def dump_addressable_jp(root: Path) -> int:
    """Pull Japanese from Addressable MonoBehaviour/TextAsset. Not a whole-bundle scan."""
    try:
        import UnityPy  # noqa: F401
    except ImportError:
        log("UnityPy 未安装，跳过 Addressable 预抽。")
        return 0
    found: set[str] = set()
    files = iter_unity_asset_files(root)
    for i, path in enumerate(files):
        found.update(dump_file_jp(path))
        if i and i % 500 == 0:
            log(f"预抽 {i}/{len(files)} unique={len(found)}")
    if not found:
        return 0
    folder = text_dir(root)
    folder.mkdir(parents=True, exist_ok=True)
    mapping = load_all_maps(folder)
    added = 0
    extracted = parse_xuat(folder / PREEXTRACT_NAME) if (folder / PREEXTRACT_NAME).is_file() else {}
    for src in sorted(found, key=lambda s: (len(s), s)):
        cur = mapping.get(src, "")
        if cur and not is_identity(src, cur) and not HIRA_RE.search(cur):
            continue
        composed = compose_from_map(src, mapping)
        dst = composed or extracted.get(src) or mapping.get(src) or src
        if src not in extracted:
            added += 1
        extracted[src] = dst
        mapping[src] = dst
    write_xuat(folder / PREEXTRACT_NAME, extracted)
    log(f"Addressable 预抽 unique={len(found)} new={added} -> {PREEXTRACT_NAME}")
    return added


def is_identity(src: str, dst: str) -> bool:
    return bool(src) and src == dst and looks_jp(src)


def is_regex_key(src: str) -> bool:
    return src.startswith("r:") or src.startswith("sr:")


def load_all_maps(folder: Path, *, skip_names: set[str] | None = None) -> dict[str, str]:
    data: dict[str, str] = {}
    skip = skip_names or set()
    if not folder.is_dir():
        return data
    for path in sorted(folder.glob("*.txt")):
        if path.name.startswith("_Misses") or path.name in skip:
            continue
        data.update(parse_xuat(path))
    return data


def extract_zip(zip_path: Path, dest: Path) -> None:
    dest.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path) as zf:
        zf.extractall(dest)


def xuat_installed(root: Path) -> bool:
    plugin = root / "BepInEx" / "plugins" / "XUnity.AutoTranslator"
    return plugin.is_dir() and any(plugin.glob("XUnity.AutoTranslator.Plugin.*.dll"))


def install_bruteforce_fix(root: Path) -> None:
    src = TOOLS / "vendor" / "unity" / "bruteforce" / "bin" / "BruteForceFix.dll"
    if not src.is_file():
        return
    dest = root / "BepInEx" / "plugins" / "AutoTranslator.IL2CPP.BruteForceFix.dll"
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dest)


def install_plugins(root: Path, backend: str, exe: Path | None = None) -> None:
    if xuat_installed(root) and (
        (backend == "il2cpp" and (root / "winhttp.dll").is_file())
        or (backend == "mono" and (root / "BepInEx" / "core").is_dir())
    ):
        log("插件已在，跳过解压。")
        install_bruteforce_fix(root)
        return
    if backend not in {"il2cpp", "mono"}:
        raise RuntimeError(f"unrecognized Unity backend: {backend}")
    be, xuat = ensure_plugin_zips(backend, exe or find_game_exe(root))
    log(f"解压 {be.name}")
    extract_zip(be, root)
    log(f"解压 {xuat.name}")
    extract_zip(xuat, root)
    install_bruteforce_fix(root)


def set_ini_key(path: Path, section: str, key: str, value: str) -> None:
    text = path.read_text(encoding="utf-8-sig") if path.is_file() else ""
    lines = text.splitlines()
    sec = f"[{section}]"
    out: list[str] = []
    in_sec = False
    found_sec = False
    found_key = False
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            if in_sec and not found_key:
                out.append(f"{key}={value}")
                found_key = True
            in_sec = stripped.lower() == sec.lower()
            if in_sec:
                found_sec = True
            out.append(line)
            continue
        if in_sec and stripped.lower().startswith(key.lower() + "="):
            out.append(f"{key}={value}")
            found_key = True
            continue
        out.append(line)
    if in_sec and not found_key:
        out.append(f"{key}={value}")
        found_key = True
    if not found_sec:
        if out and out[-1].strip():
            out.append("")
        out.append(sec)
        out.append(f"{key}={value}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(out) + "\n", encoding="utf-8")


def write_translator_config(root: Path) -> Path:
    cfg = root / "BepInEx" / "config" / "AutoTranslatorConfig.ini"
    defaults = {
        ("Service", "Endpoint"): "",
        ("Service", "FallbackEndpoint"): "",
        ("General", "Language"): "zh-CN",
        ("General", "FromLanguage"): "ja",
        ("Files", "Directory"): r"Translation\zh-CN",
        ("Files", "OutputFile"): r"Translation\zh-CN\Text\_AutoGeneratedTranslations.txt",
        ("TextFrameworks", "EnableIMGUI"): "False",
        ("TextFrameworks", "EnableUGUI"): "True",
        ("TextFrameworks", "EnableNGUI"): "True",
        ("TextFrameworks", "EnableTextMeshPro"): "True",
        ("TextFrameworks", "EnableFairyGUI"): "True",
        ("TextFrameworks", "EnableUIElements"): "True",
        ("Behaviour", "UseStaticTranslations"): "True",
        ("Behaviour", "FallbackFontTextMeshPro"): "Microsoft YaHei",
        ("Behaviour", "ReloadTranslationsOnFileChange"): "False",
        ("Behaviour", "MaxRetryCount"): "3",
        ("Behaviour", "EnableSilentMode"): "False",
        ("Behaviour", "IgnoreVirtualTextSetterCallingRules"): "True",
        # IL2CPP XUAT text setters ship Harmony Prefix/Postfix only.
        # True skips Harmony and then fails: "no alternate MonoMod hook".
        ("Behaviour", "ForceMonoModHooks"): "False",
        ("Behaviour", "EnableTextPathLogging"): "False",
    }
    if not cfg.is_file():
        cfg.parent.mkdir(parents=True, exist_ok=True)
        cfg.write_text(
            "[Service]\nEndpoint=\nFallbackEndpoint=\n\n"
            "[General]\nLanguage=zh-CN\nFromLanguage=ja\n",
            encoding="utf-8",
        )
    for (section, key), value in defaults.items():
        set_ini_key(cfg, section, key, value)
    return cfg


def silence_bepinex_console(root: Path) -> None:
    cfg = root / "BepInEx" / "config" / "BepInEx.cfg"
    set_ini_key(cfg, "Logging.Console", "Enabled", "false")


def write_ui_glossary(root: Path) -> Path:
    path = text_dir(root) / "00_ui.txt"
    if path.is_file():
        return path
    write_xuat(path, dict(UI_GLOSSARY), list(UI_GLOSSARY))
    return path


def write_terms_stub(root: Path) -> Path:
    path = text_dir(root) / TERMS_NAME
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_file():
        return path
    path.write_text(TERMS_STUB, encoding="utf-8")
    return path


def collect_term_keys(mapping: dict[str, str]) -> list[str]:
    """Honorifics and repeated short keys from dump. No per-game names in tools."""
    honor: list[str] = []
    seen: set[str] = set()
    standalones: list[str] = []
    for src in mapping:
        if is_regex_key(src) or is_script_sample(src):
            continue
        vis = tmp_visible_text(src)
        for match in NAME_HONORIFIC_RE.finditer(vis):
            name = match.group(1)
            if name in UI_GLOSSARY or name in seen:
                continue
            seen.add(name)
            honor.append(name)
        if (
            2 <= len(vis) <= 6
            and HAN_RE.search(vis)
            and not HIRA_RE.search(vis)
            and vis not in UI_GLOSSARY
            and vis not in seen
        ):
            standalones.append(vis)
            seen.add(vis)
    frequent: list[str] = []
    for short in standalones:
        hits = 0
        for src in mapping:
            if short in tmp_visible_text(src):
                hits += 1
                if hits >= 3:
                    frequent.append(short)
                    break
    out: list[str] = []
    for key in honor + frequent:
        if key not in out:
            out.append(key)
        if len(out) >= 48:
            break
    return out


def upsert_terms(folder: Path, updates: dict[str, str]) -> Path:
    path = folder / TERMS_NAME
    path.parent.mkdir(parents=True, exist_ok=True)
    existing = parse_xuat(path) if path.is_file() else {}
    comments = TERMS_STUB.rstrip()
    if path.is_file():
        raw = path.read_text(encoding="utf-8-sig")
        kept = [
            line
            for line in raw.splitlines()
            if line.lstrip().startswith("#") or not line.strip()
        ]
        if kept:
            comments = "\n".join(kept).rstrip()
    for src, dst in updates.items():
        cur = existing.get(src, "")
        if cur and not is_identity(src, cur) and not HIRA_RE.search(cur):
            continue
        if dst:
            existing[src] = dst
        elif src not in existing:
            existing[src] = src
    lines = [comments, ""]
    for src, dst in existing.items():
        if not src or not dst:
            continue
        lines.append(f"{xuat_escape(src)}={xuat_escape(dst)}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return path


def seed_terms_from_mapping(folder: Path, mapping: dict[str, str]) -> int:
    keys = collect_term_keys(mapping)
    if not keys:
        return 0
    before = parse_xuat(folder / TERMS_NAME) if (folder / TERMS_NAME).is_file() else {}
    upsert_terms(folder, {src: before.get(src) or src for src in keys})
    added = sum(1 for src in keys if src not in before)
    if added:
        log(f"术语候选 {added} 条 -> {TERMS_NAME}")
    return added


def fill_term_dests(
    folder: Path, mapping: dict[str, str], translate
) -> int:
    path = folder / TERMS_NAME
    if not path.is_file():
        return 0
    terms = parse_xuat(path)
    pending = [
        src
        for src, dst in terms.items()
        if src and (not dst or is_identity(src, dst) or HIRA_RE.search(dst))
    ]
    if not pending:
        return 0
    filled = 0
    out = translate(pending)
    updates: dict[str, str] = {}
    for src, dst in zip(pending, out):
        text = polish_filled_dest(
            src, str(dst or "").strip(), terms=load_term_dests(folder)
        )
        if text and text != src and not HIRA_RE.search(text):
            updates[src] = text
            mapping[src] = text
            filled += 1
    if updates:
        upsert_terms(folder, updates)
        log(f"术语预填 {filled}/{len(pending)}")
    return filled


def parse_gpt_dict(path: Path | None = None) -> dict[str, str]:
    """GalTransl Dict/GPT字典.txt. Tab src/dst; skip comments and GPT_DICT_SKIP."""
    target = path or GPT_DICT_FILE
    out: dict[str, str] = {}
    if not target.is_file():
        return out
    for raw in target.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("//") or line.startswith("="):
            continue
        line = line.replace("    ", "\t")
        parts = line.split("\t")
        if len(parts) < 2:
            continue
        jp, cn = parts[0].strip(), parts[1].strip()
        if not jp or not cn or jp in GPT_DICT_SKIP:
            continue
        if jp in {
            "pre_jp",
            "post_jp",
            "pre_src",
            "post_src",
            "pre_dst",
            "post_dst",
            "pre_zh",
            "post_zh",
            "mono",
            "diag",
        }:
            continue
        out.setdefault(jp, cn)
    return out


def glossary_haystack(
    folder: Path | None, srcs: list[str] | None = None
) -> str | None:
    """Official CGptDict.gen_prompt: only terms that appear in this batch/dump."""
    if srcs:
        return "\n".join(srcs)
    if folder is None or not folder.is_dir():
        return None
    parts: list[str] = []
    for path in sorted(folder.glob("*.txt")):
        if path.name.startswith("_Misses") or path.name in GLOSSARY_HAYSTACK_SKIP:
            continue
        for src in parse_xuat(path):
            vis = tmp_visible_text(src)
            if vis:
                parts.append(vis)
    return "\n".join(parts) if parts else None


def fill_glossary_text(
    folder: Path, limit: int = 48, srcs: list[str] | None = None
) -> str:
    """Default galge terms + official GPT dict ∩ dump/batch + per-game 00_terms.

    Official GalTransl only sends a dict row when this round's sentences contain
    the search word (CGptDict.gen_prompt) and sorts by key length (sortDict).
    """
    merged: dict[str, str] = dict(DEFAULT_TERM_GLOSSARY)
    haystack = glossary_haystack(folder, srcs)
    if haystack is not None:
        for jp, cn in parse_gpt_dict().items():
            merged.setdefault(jp, cn)
    path = folder / TERMS_NAME
    if path.is_file():
        for src, dst in parse_xuat(path).items():
            if src and dst and not is_identity(src, dst) and not HIRA_RE.search(dst):
                merged[src] = dst
    rows: list[str] = []
    items = sorted(merged.items(), key=lambda kv: len(kv[0]), reverse=True)
    for src, dst in items:
        if not src or not dst or is_identity(src, dst) or HIRA_RE.search(dst):
            continue
        if haystack is not None and src not in haystack:
            continue
        rows.append(f"{src}->{dst}")
        if len(rows) >= limit:
            break
    return "\n".join(rows)


def break_sprite_dest(dst: str) -> str:
    """Keep <sprite=N> hearts, wrap beats so LOG is not one wall of text."""
    if not dst or not SPRITE_TAG_ANY_RE.search(dst):
        return dst
    out: list[str] = []
    last = 0
    for match in SPRITE_TAG_ANY_RE.finditer(dst):
        out.append(dst[last : match.end()])
        last = match.end()
        if last < len(dst) and dst[last] != "\n":
            out.append("\n")
    out.append(dst[last:])
    return "".join(out)


def repair_rich_text_dest(src: str, dst: str) -> str:
    """GalTransl often turns </color> into 』 or a stray <color>."""
    if not dst or not COLOR_OPEN_RE.search(src):
        return dst
    out = dst
    if not COLOR_CLOSE_RE.search(out):
        out = STRAY_COLOR_RE.sub("</color>", out)
    if not COLOR_CLOSE_RE.search(out) and "』" in out:
        out = out.replace("』", "</color>", 1)
    if COLOR_OPEN_RE.search(out) and not COLOR_CLOSE_RE.search(out):
        out = re.sub(
            r"(<color=[^>]+>.*?)(?=(?:<sprite\\?=\d+>|[\ue000])?$)",
            r"\1</color>",
            out,
            count=1,
            flags=re.I | re.S,
        )
    return out


def tmp_visible_text(src: str) -> str:
    return TMP_TAG_RE.sub("", src.replace("\\n", "\n")).replace("\n", "")


def is_tagged_kana_title(src: str) -> bool:
    """Vertical TMP katakana titles (BGM). GalTransl eats <rotate> and invents dest."""
    if "<rotate" not in src.lower():
        return False
    inner = tmp_visible_text(src)
    if len(inner) < 4:
        return False
    kata = len(KATA_RE.findall(inner))
    if kata == 0:
        return False
    hira = len(HIRA_RE.findall(inner))
    han = len(HAN_RE.findall(inner))
    return kata >= hira and kata >= han


def prefer_src_kanji(src: str, dst: str) -> str:
    """Only the pairs that vanish in JP decorative fonts. Do not tradify whole dests."""
    if not src or not dst:
        return dst
    out = dst
    if "達" in src and "达人" in out:
        out = out.replace("达人", "達人")
    if "屍" in src:
        out = out.replace("尸横", "屍横")
    if "スタート" in src and "点击开始" in out:
        out = out.replace("点击开始", "点击スタート")
    return out


def load_term_dests(folder: Path | None) -> dict[str, str]:
    merged = dict(DEFAULT_TERM_GLOSSARY)
    if folder is None:
        return merged
    path = folder / TERMS_NAME
    if not path.is_file():
        return merged
    for src, dst in parse_xuat(path).items():
        if src and dst and not is_identity(src, dst) and not HIRA_RE.search(dst):
            merged[src] = dst
    return merged


def _replace_slip(text: str, old: str, new: str) -> str:
    """Replace old with new. If new is prefix+old, do not stack the prefix."""
    if not old or old not in text or old == new:
        return text
    if new.endswith(old):
        prefix = new[: -len(old)]
        if prefix:
            text = re.sub(
                rf"(?<!{re.escape(prefix)}){re.escape(old)}",
                lambda _match: new,
                text,
            )
            return re.sub(
                rf"(?:{re.escape(prefix)}){{2,}}(?={re.escape(old)})",
                prefix,
                text,
            )
    return text.replace(old, new)


def polish_common_dest(
    src: str, dst: str, terms: dict[str, str] | None = None
) -> str:
    """Rewrite common GalTransl slips when the Japanese source actually has that term."""
    if not src or not dst:
        return dst
    out = dst
    vis = tmp_visible_text(src)
    ui = UI_GLOSSARY.get(vis)
    if ui and out == "说":
        out = ui
    for needles, old, new in COMMON_DEST_FIXES:
        if old in out and any(n in src for n in needles):
            out = _replace_slip(out, old, new)
    if (
        "どうしよっか" in src
        and "怎么办好" not in out
        and not out.startswith("该怎么办")
        and (out == "怎么办" or out.startswith("怎么办"))
    ):
        out = "做什么好呢" + out[len("怎么办") :]
    if (
        "動きで" in src
        and "我就用" in out
        and not any(p in src for p in ("私", "僕", "俺", "あたし", "わたし", "わたくし"))
    ):
        out = out.replace("我就用", "用")
    if "れぇ" in src and "れろ" not in src and "舐め" not in src:
        out = out.replace("舔舔", "嗯——")
    mapping = dict(DEFAULT_TERM_GLOSSARY)
    if terms:
        mapping.update(terms)
    for jp, slips in TERM_DEST_SLIPS.items():
        if jp not in src:
            continue
        cn = mapping.get(jp)
        if not cn:
            continue
        for slip in slips:
            if slip in out:
                out = out.replace(slip, cn)
    return out


def restore_missing_sprites(src: str, dst: str) -> str:
    """Put <sprite> back when each Chinese period is one missing heart beat.

    Only when the dest dropped every tag and the period count matches the
    source tag count. A different count has no place to put the tag.
    """
    if not src or not dst or is_tagged_kana_title(src):
        return dst
    src_tags = SPRITE_TAG_ANY_RE.findall(src)
    if not src_tags or SPRITE_TAG_ANY_RE.search(dst):
        return dst
    tag = src_tags[0]
    if any(item != tag for item in src_tags):
        return dst
    if dst.count("。") != len(src_tags):
        return dst
    return dst.replace("。", "。" + tag)


def polish_filled_dest(
    src: str, dst: str, terms: dict[str, str] | None = None
) -> str:
    if not dst:
        return dst
    text = dst.replace("\ufffd", "").rstrip()
    if is_tagged_kana_title(src):
        vis = tmp_visible_text(text)
        if (
            "<rotate" in text.lower()
            and HAN_RE.search(vis)
            and not is_identity(src, text)
        ):
            return text
        return src
    text = prefer_src_kanji(src, text)
    text = polish_common_dest(src, text, terms=terms)
    text = restore_missing_sprites(src, text)
    return break_sprite_dest(repair_rich_text_dest(src, text))


def polish_text_folder(folder: Path) -> int:
    """Re-polish dests already on disk. Skip terms / regex / variant files."""
    if not folder.is_dir():
        return 0
    skip = {TERMS_NAME, VARIANT_NAME, "00_missing_loc.txt"}
    n = 0
    terms = load_term_dests(folder)
    for path in sorted(folder.glob("*.txt")):
        if path.name.startswith("_Misses") or path.name in skip:
            continue
        mapping = parse_xuat(path)
        if not mapping:
            continue
        order = list(mapping)
        changed = False
        for key in order:
            new = polish_filled_dest(key, mapping[key], terms=terms)
            if new != mapping[key]:
                mapping[key] = new
                changed = True
                n += 1
        if changed:
            write_xuat(path, mapping, order)
    return n


def write_missing_loc_regex(root: Path) -> Path:
    path = text_dir(root) / "00_missing_loc.txt"
    path.parent.mkdir(parents=True, exist_ok=True)
    existing = path.read_text(encoding="utf-8-sig") if path.is_file() else ""
    lines = [
        line
        for line in existing.splitlines()
        if not line.startswith('r:"^No translation found')
    ]
    body = "\n".join(lines).rstrip()
    if MISSING_LOC_NOTE.strip() not in body:
        body = (body + ("\n" if body else "") + MISSING_LOC_NOTE).rstrip() + "\n"
    path.write_text(body, encoding="utf-8")
    return path


def rewrite_identities(folder: Path) -> int:
    removed = 0
    if not folder.is_dir():
        return 0
    for path in sorted(folder.glob("*.txt")):
        if path.name.startswith("_Misses") or path.name in {
            PREEXTRACT_NAME,
            UNMATCHED_NAME,
            VARIANT_NAME,
            TERMS_NAME,
        }:
            continue
        raw = path.read_text(encoding="utf-8-sig")
        keep: list[str] = []
        changed = False
        for line in raw.splitlines():
            if not line or line.lstrip().startswith("#") or "=" not in line:
                keep.append(line)
                continue
            parts = xuat_split(line)
            if not parts:
                keep.append(line)
                continue
            src, dst = xuat_unescape(parts[0]), xuat_unescape(parts[1])
            if is_regex_key(src):
                keep.append(line)
                continue
            if is_identity(src, dst):
                if is_tagged_kana_title(src):
                    keep.append(line)
                    continue
                changed = True
                removed += 1
                continue
            keep.append(line)
        if changed:
            text = "\n".join(keep)
            if keep:
                text += "\n"
            path.write_text(text, encoding="utf-8")
    return removed


def launcher_bytes(exe_name: str) -> bytes:
    body = (
        "@echo off\r\n"
        'cd /d "%~dp0"\r\n'
        f'start "" "{exe_name}"\r\n'
    )
    return body.encode("ascii")


def write_launcher(root: Path, exe: Path) -> Path:
    path = root / "点我启动汉化版.bat"
    path.write_bytes(launcher_bytes(exe.name))
    return path


def write_marker(root: Path) -> Path:
    path = root / "hanhua_unity.txt"
    path.write_text(
        "BepInEx + XUnity.AutoTranslator (zh-CN static files).\n"
        "Play the game to dump new strings, then 补翻译 on the Local AI 汉化 page "
        "(or 点我填Unity已抽出的词库.bat).\n"
        "Restart the game after filling. Baked textures are not text.\n",
        encoding="utf-8",
    )
    return path


def short_vocal_dest(src: str) -> str:
    """Phonetic Chinese for short moans. Empty when src is not a vocal."""
    try:
        import local_qwen
    except ImportError:
        return ""
    return local_qwen.short_vocal_dest(src) or ""


def is_script_sample(src: str) -> bool:
    """Kana charts / punct dumps, not dialogue."""
    if len(src) < 8:
        return False
    return len(HAN_RE.findall(src)) < 2


def pending_for_fill(mapping: dict[str, str]) -> list[str]:
    pending: list[str] = []
    for src, dst in mapping.items():
        if is_regex_key(src):
            continue
        if src in UI_GLOSSARY:
            continue
        if not looks_jp(src):
            continue
        if is_script_sample(src):
            continue
        if is_tagged_kana_title(src):
            continue
        if not dst or HIRA_RE.search(dst):
            pending.append(src)
            continue
        if is_identity(src, dst):
            if not HIRA_RE.search(src) and not KATA_RE.search(src):
                continue
            pending.append(src)
    return pending


def merge_ui_into(mapping: dict[str, str]) -> None:
    for src, dst in UI_GLOSSARY.items():
        cur = mapping.get(src, "")
        if not cur or is_identity(src, cur) or HIRA_RE.search(cur):
            mapping[src] = dst


def dump_path(root: Path) -> Path:
    return text_dir(root) / "_AutoGeneratedTranslations.txt"


def has_dump(root: Path) -> bool:
    path = dump_path(root)
    return path.is_file() and path.stat().st_size > 0


def pending_count(root: Path) -> int:
    folder = text_dir(root)
    mapping = load_all_maps(folder, skip_names={VARIANT_NAME})
    merge_ui_into(mapping)
    seed_terms_from_mapping(folder, mapping)
    pending = set(pending_for_fill(mapping))
    terms_path = folder / TERMS_NAME
    if terms_path.is_file():
        for src, dst in parse_xuat(terms_path).items():
            if src and (not dst or is_identity(src, dst) or HIRA_RE.search(dst)):
                pending.add(src)
    return len(pending)


def fill_dump(root: Path, translate, on_progress=None) -> tuple[int, int]:
    folder = text_dir(root)
    folder.mkdir(parents=True, exist_ok=True)
    migrate_unmatched_file(folder)
    mapping = load_all_maps(folder, skip_names={VARIANT_NAME})
    merge_ui_into(mapping)
    terms = load_term_dests(folder)
    dump = dump_path(root)
    order = list(parse_xuat(dump)) if dump.is_file() else list(mapping)
    seen = set(order)
    for src in mapping:
        if src in seen:
            continue
        order.append(src)
        seen.add(src)
    seed_terms_from_mapping(folder, mapping)
    pending = pending_for_fill(mapping)
    total = len(pending)
    filled = 0

    def persist() -> None:
        filled_map = {}
        for src in order:
            dst = mapping.get(src, "")
            if not dst:
                continue
            mapping[src] = polish_filled_dest(src, dst, terms=terms)
            filled_map[src] = mapping[src]
        write_xuat(folder / "01_filled.txt", filled_map, order)
        for name in (PREEXTRACT_NAME, UNMATCHED_NAME):
            path = folder / name
            if not path.is_file():
                continue
            extracted = parse_xuat(path)
            for src in list(extracted):
                dst = mapping.get(src, "")
                if not dst:
                    continue
                if is_tagged_kana_title(src) or (
                    not is_identity(src, dst) and not HIRA_RE.search(dst)
                ):
                    extracted[src] = dst
            write_xuat(path, extracted)

    filled += fill_term_dests(folder, mapping, translate)
    pending = pending_for_fill(mapping)
    total = max(total, len(pending))
    leftover: list[str] = []
    for src in pending:
        dest = short_vocal_dest(src)
        if dest and dest != src and not HIRA_RE.search(dest):
            mapping[src] = dest
            filled += 1
        else:
            leftover.append(src)
    pending = leftover

    if pending:
        import local_qwen

        chunk = local_qwen.fill_chunk()
        for i in range(0, len(pending), chunk):
            batch = pending[i : i + chunk]
            out = translate(batch)
            for src, dst in zip(batch, out):
                text = polish_filled_dest(src, str(dst or "").strip(), terms=terms)
                if text and text != src and not HIRA_RE.search(text):
                    mapping[src] = text
                    filled += 1
            done = min(i + chunk, len(pending))
            if on_progress is not None:
                on_progress(done, len(pending))
            if done % 64 == 0 or done == len(pending):
                persist()
    else:
        persist()
    polish_text_folder(folder)
    expand_variant_keys(folder)
    return filled, total


def setup_game(root: Path) -> dict[str, str]:
    backend = detect_backend(root)
    if backend not in {"il2cpp", "mono"}:
        raise RuntimeError("不是可识别的 Unity 目录（需要 GameAssembly.dll 或 *_Data\\Managed）。")
    exe = find_game_exe(root)
    if exe is None:
        raise RuntimeError("这一层找不到游戏 exe。")
    install_plugins(root, backend, exe)
    write_translator_config(root)
    silence_bepinex_console(root)
    text_dir(root).mkdir(parents=True, exist_ok=True)
    write_ui_glossary(root)
    write_terms_stub(root)
    write_missing_loc_regex(root)
    removed = rewrite_identities(text_dir(root))
    dumped = dump_addressable_jp(root)
    ingest_unmatched_log(root)
    seed_terms_from_mapping(text_dir(root), load_all_maps(text_dir(root), skip_names={VARIANT_NAME}))
    launcher = write_launcher(root, exe)
    write_marker(root)
    return {
        "backend": backend,
        "exe": exe.name,
        "launcher": str(launcher),
        "identities_removed": str(removed),
        "preextracted": str(dumped),
        "text": str(text_dir(root)),
    }
