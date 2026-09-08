# Offline checks for local-chat vs qwen-mt routing. Does not call the network.
from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

import local_qwen
import translate_direct

TOOLS = Path(__file__).resolve().parent
PACK = TOOLS.parent


class LocalQwenRoutingTests(unittest.TestCase):
    def test_mt_name_detection(self) -> None:
        self.assertTrue(local_qwen.is_qwen_mt("qwen-mt-plus"))
        self.assertTrue(local_qwen.is_qwen_mt("Qwen-MT-flash"))
        self.assertFalse(local_qwen.is_qwen_mt(local_qwen.LOCAL_MODEL))
        self.assertFalse(local_qwen.is_qwen_mt("glm-4.5-airx"))

    def test_completions_url(self) -> None:
        self.assertEqual(
            local_qwen.completions_url("http://127.0.0.1:18135/v1"),
            "http://127.0.0.1:18135/v1/chat/completions",
        )
        self.assertEqual(
            local_qwen.completions_url("http://127.0.0.1:18135/v1/chat/completions"),
            "http://127.0.0.1:18135/v1/chat/completions",
        )

    def test_mt_payload_has_no_system(self) -> None:
        payload = local_qwen.mt_payload("qwen-mt-plus", ["こんにちは", "牧場"])
        self.assertEqual(payload["messages"][0]["role"], "user")
        self.assertEqual(len(payload["messages"]), 1)
        self.assertNotIn("system", json.dumps(payload))
        self.assertEqual(payload["translation_options"]["source_lang"], "Japanese")
        self.assertEqual(payload["translation_options"]["target_lang"], "Chinese")
        self.assertNotIn("chat_template_kwargs", payload)

    def test_chat_payload_has_system_and_think_off(self) -> None:
        payload = local_qwen.chat_payload(
            local_qwen.LOCAL_MODEL, ["こんにちは"], local_qwen.GAME_SYSTEM
        )
        roles = [m["role"] for m in payload["messages"]]
        self.assertEqual(roles, ["system", "user"])
        self.assertNotIn("translation_options", payload)
        self.assertFalse(payload["chat_template_kwargs"]["enable_thinking"])
        self.assertIn("こんにちは", payload["messages"][1]["content"])
        schema = payload["json_schema"]
        self.assertEqual(schema["type"], "array")
        self.assertEqual(schema["items"], {"type": "string"})
        self.assertEqual(schema["minItems"], 1)
        self.assertEqual(schema["maxItems"], 1)
        self.assertEqual(payload["response_format"]["type"], "json_schema")
        self.assertEqual(payload["response_format"]["json_schema"]["schema"], schema)

    def test_translation_schema_locks_batch_length(self) -> None:
        schema = local_qwen.translation_json_schema(8)
        self.assertEqual(schema["minItems"], 8)
        self.assertEqual(schema["maxItems"], 8)

    def test_protect_batch_is_json_safe_and_restores_codes(self) -> None:
        src = ["\\c[2]【\\N[1]】\\c[0]\\n\\C[27]♥\\C[0]おはよう"]
        protected, helds = local_qwen.protect_batch(src)
        json.loads(json.dumps(protected, ensure_ascii=False))
        self.assertNotRegex(protected[0], r"\\[cCnNiIvV]\[[^\]]*\]")
        self.assertIn("@@RPG0@@", protected[0])
        self.assertEqual(local_qwen.restore_batch(protected, helds), src)

    def test_parse_reply_json_and_fences(self) -> None:
        self.assertEqual(
            local_qwen.parse_reply('["你好","牧场"]', 2),
            ["你好", "牧场"],
        )
        self.assertEqual(
            local_qwen.parse_reply('```json\n["你好"]\n```', 1),
            ["你好"],
        )
        self.assertEqual(
            local_qwen.parse_reply('<think>x</think>["你好"]', 1),
            ["你好"],
        )
        self.assertEqual(local_qwen.parse_reply("你好", 1), ["你好"])
        self.assertEqual(
            local_qwen.parse_reply('["\\c[2]【\\N[1]】啊\\C[27]♥"]', 1),
            ["\\c[2]【\\N[1]】啊\\C[27]♥"],
        )

    def test_default_engine_is_mt(self) -> None:
        self.assertEqual(translate_direct.resolve_engine(None), "mt")
        self.assertEqual(translate_direct.resolve_engine("local"), "local")

    def test_cloud_bat_has_no_local_flag(self) -> None:
        bat = (PACK / "点我翻译游戏.bat").read_text(encoding="ascii", errors="replace")
        self.assertNotIn("--local", bat)
        self.assertNotIn("--progress-jsonl", bat)
        self.assertIn("one_click_rm.py", bat)

    def test_local_game_bat_keeps_local_flag(self) -> None:
        bat = (PACK / "点我用本地Qwen翻译游戏.bat").read_text(encoding="ascii", errors="replace")
        self.assertIn("--local", bat)
        self.assertNotIn("--progress-jsonl", bat)

    def test_fill_bat_stays_local(self) -> None:
        bat = (PACK / "点我用本地Qwen翻已抽出的图字.bat").read_text(
            encoding="ascii", errors="replace"
        )
        self.assertNotIn("--mt", bat)
        self.assertIn("fill_ocr_local.py", bat)

    def test_progress_jsonl_emits_parseable_lines(self) -> None:
        local_qwen.enable_progress(True)
        try:
            from io import StringIO
            import contextlib

            buf = StringIO()
            with contextlib.redirect_stdout(buf):
                local_qwen.emit("progress", "translate", done=1, total=2, message="ok")
            line = buf.getvalue().strip()
            rec = json.loads(line)
            self.assertEqual(rec["type"], "progress")
            self.assertEqual(rec["phase"], "translate")
            self.assertEqual(rec["done"], 1)
            self.assertEqual(rec["total"], 2)
        finally:
            local_qwen.enable_progress(False)

    def test_ocr_extract_uses_prep_manual_not_save_text(self) -> None:
        import ocr_extract_local

        cmd = ocr_extract_local.mit_ocr_command(
            Path("python.exe"),
            Path("font.ttc"),
            Path("cfg.json"),
            Path("in"),
            Path("out"),
        )
        self.assertIn("--prep-manual", cmd)
        self.assertNotIn("--save-text", cmd)

    def test_ocr_extract_explains_save_text_quit_without_strings(self) -> None:
        import ocr_extract_local

        self.assertTrue(ocr_extract_local.is_save_text_quit(4294967295))
        self.assertTrue(ocr_extract_local.is_save_text_quit(-1))
        self.assertIn("提前退出", ocr_extract_local.ocr_failed_message(4294967295, 0))
        self.assertEqual("", ocr_extract_local.ocr_failed_message(0, 12))

    def test_isolate_moves_ocr_sidecars_out_of_png_folder(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp) / "typeset_in"
            dest = Path(tmp) / "ocr_sidecars"
            folder.mkdir()
            (folder / "page.png").write_bytes(b"png")
            (folder / "page_ocr.json").write_text("[]", encoding="utf-8")
            (folder / "page_translations.txt").write_text("t", encoding="utf-8")
            moved = local_qwen.isolate_image_folder(folder, dest)
            self.assertEqual(moved, 2)
            names = sorted(p.name for p in folder.iterdir())
            self.assertEqual(names, ["page.png"])
            self.assertTrue((dest / "page_ocr.json").is_file())
            self.assertTrue((dest / "page_translations.txt").is_file())

    def test_ocr_extract_missing_dir_is_offline(self) -> None:
        import ocr_extract_local

        missing = TOOLS / "_no_such_png_dir"
        work = TOOLS / "_no_such_work"
        old = sys.argv
        try:
            sys.argv = ["ocr_extract_local.py", str(missing), str(work)]
            rc = ocr_extract_local.main()
        finally:
            sys.argv = old
        self.assertEqual(rc, 1)

    def test_only_loopback_bypasses_proxy(self) -> None:
        self.assertTrue(local_qwen.is_loopback_url(local_qwen.LOCAL_CHAT))
        self.assertFalse(
            local_qwen.is_loopback_url(
                "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"
            )
        )

    def test_proxy_adapter_left_in_place(self) -> None:
        proxy = TOOLS / "qwen_mt_proxy.py"
        self.assertTrue(proxy.is_file())
        src = proxy.read_text(encoding="utf-8")
        self.assertIn("18765", src)
        self.assertIn("translation_options", src)


if __name__ == "__main__":
    unittest.main()
