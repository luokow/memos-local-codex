# Offline checks for local-chat vs qwen-mt routing. Does not call the network.
from __future__ import annotations

import json
import os
import subprocess
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

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

    def test_ocr_retry_progress_keeps_book_total(self) -> None:
        import tempfile
        import ocr_extract_local

        captured: dict[str, int] = {}

        def fake_run(cmd, **kwargs):
            captured["total"] = kwargs["total"]
            captured["done"] = kwargs["count"]()
            return 0

        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            pages = [work / "a.png", work / "b.png"]
            for page in pages:
                page.write_bytes(b"p")
            with patch.object(local_qwen, "run_mit_with_page_progress", side_effect=fake_run):
                ocr_extract_local.retry_missing_ocr(
                    pages[-1:],
                    work=work,
                    mit_py=Path(tmp) / "python.exe",
                    font=Path(tmp) / "font.ttc",
                    cfg=Path(tmp) / "cfg.json",
                    mit_root=work,
                    dump=work / "ocr_dump",
                    env={},
                    progress_total=2,
                    progress_offset=1,
                )
        self.assertEqual(captured["total"], 2)
        self.assertEqual(captured["done"], 1)

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
        self.assertNotIn("--overwrite", cmd)
        self.assertIn("--pre-dict", cmd)
        self.assertIn("--post-dict", cmd)

    def test_image_system_mentions_ocr_digit_bang(self) -> None:
        self.assertIn("感叹号", local_qwen.IMAGE_SYSTEM)
        self.assertIn("译名", local_qwen.IMAGE_SYSTEM)

    def test_mark_empty_ocr_writes_skip_not_bare_array(self) -> None:
        import tempfile
        import ocr_extract_local

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp)
            page = dest / "039.png"
            page.write_bytes(b"p")
            ocr_extract_local.mark_empty_ocr([page], dest)
            payload = json.loads((dest / "039_ocr.json").read_text(encoding="utf-8"))
            self.assertTrue(payload)
            self.assertTrue(payload[0].get("skip"))
            self.assertFalse(
                local_qwen.missing_ocr_pages([page], [dest]),
            )

    def test_aggressive_ocr_config_lowers_thresholds(self) -> None:
        import ocr_extract_local

        cfg = ocr_extract_local.aggressive_ocr_config()
        self.assertIsNotNone(cfg)
        data = json.loads(cfg.read_text(encoding="utf-8"))
        self.assertLess(data["detector"]["text_threshold"], 0.5)
        self.assertLess(data["detector"]["box_threshold"], 0.65)
        self.assertEqual(data["translator"]["translator"], "none")
        self.assertEqual(data["inpainter"]["inpainter"], "none")

    def test_ocr_extract_explains_save_text_quit_without_strings(self) -> None:
        import ocr_extract_local

        self.assertTrue(ocr_extract_local.is_save_text_quit(4294967295))
        self.assertTrue(ocr_extract_local.is_save_text_quit(-1))
        self.assertIn("提前退出", ocr_extract_local.ocr_failed_message(4294967295, 0))
        self.assertEqual("", ocr_extract_local.ocr_failed_message(0, 12))

    def test_completed_image_pages_ignores_input_png_and_orig(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "typeset_in"
            dump = Path(tmp) / "ocr_dump"
            src.mkdir()
            dump.mkdir()
            a = src / "a.png"
            b = src / "b.png"
            c = src / "c.png"
            a.write_bytes(b"a")
            b.write_bytes(b"b")
            c.write_bytes(b"c")
            (src / "a_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            (dump / "b.png").write_bytes(b"out")
            (dump / "c-orig.png").write_bytes(b"orig")
            pngs = [a, b, c]
            self.assertEqual(
                local_qwen.completed_image_pages(
                    pngs, ocr_dirs=[src, dump], output_dirs=[dump]
                ),
                2,
            )

    def test_mit_watch_emits_page_progress(self) -> None:
        import contextlib
        import os
        import tempfile
        from io import StringIO

        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            pngs = [tmp_path / "a.png", tmp_path / "b.png"]
            for png in pngs:
                png.write_bytes(b"x")
            out = tmp_path / "out"
            out.mkdir()
            writer = tmp_path / "writer.py"
            writer.write_text(
                "import pathlib, time\n"
                f"out = pathlib.Path(r'{out}')\n"
                "time.sleep(0.15)\n"
                "(out / 'a.png').write_bytes(b'1')\n"
                "time.sleep(0.25)\n"
                "(out / 'b.png').write_bytes(b'2')\n",
                encoding="utf-8",
            )
            local_qwen.enable_progress(True)
            try:
                buf = StringIO()
                with contextlib.redirect_stdout(buf):
                    rc = local_qwen.run_mit_with_page_progress(
                        [sys.executable, str(writer)],
                        cwd=str(tmp_path),
                        env=os.environ.copy(),
                        phase="ocr",
                        total=2,
                        count=lambda: local_qwen.completed_image_pages(
                            pngs, output_dirs=[out]
                        ),
                        interval=0.05,
                    )
            finally:
                local_qwen.enable_progress(False)
            self.assertEqual(rc, 0)
            dones = [
                json.loads(line)["done"]
                for line in buf.getvalue().splitlines()
                if line.strip()
            ]
            self.assertTrue(dones, "watcher must emit jsonl")
            self.assertIn(1, dones)
            self.assertEqual(dones[-1], 2)

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


class ImagePipelineQualityTests(unittest.TestCase):
    def test_copy_images_takes_jpg_cover_and_png_pages(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dest = Path(tmp) / "dest"
            src.mkdir()
            (src / "001_Cover.jpg").write_bytes(b"jpg")
            (src / "002_Chap.png").write_bytes(b"png")
            (src / "notes.txt").write_text("no", encoding="utf-8")
            copied = local_qwen.copy_images(src, dest)
            names = [p.name for p in copied]
            self.assertEqual(names, ["001_Cover.jpg", "002_Chap.png"])
            self.assertTrue((dest / "001_Cover.jpg").is_file())

    def test_completed_image_pages_counts_jpg_output(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "in"
            out = Path(tmp) / "out"
            src.mkdir()
            out.mkdir()
            cover = src / "001_Cover.jpg"
            page = src / "002.png"
            cover.write_bytes(b"j")
            page.write_bytes(b"p")
            (out / "001_Cover.jpg").write_bytes(b"done")
            self.assertEqual(
                local_qwen.completed_image_pages([cover, page], output_dirs=[out]),
                1,
            )

    def test_repair_ocr_bang_turns_misread_one_into_exclamation(self) -> None:
        self.assertEqual(local_qwen.repair_ocr_bang("えっ1!"), "えっ！")
        self.assertEqual(local_qwen.repair_ocr_bang("うっそ！マジで1!"), "うっそ！マジで！")
        self.assertEqual(local_qwen.repair_ocr_bang("えっ⁉そうなの1!"), "えっ⁉そうなの！")
        self.assertEqual(local_qwen.repair_ocr_bang("诶 1!"), "诶！")
        self.assertEqual(local_qwen.repair_ocr_bang("第1話は1人"), "第1話は1人")
        self.assertEqual(local_qwen.repair_ocr_bang("えっ1\n！"), "えっ！")
        self.assertEqual(local_qwen.repair_ocr_bang("假的！真的 1!"), "假的！真的！")
        self.assertEqual(local_qwen.repair_ocr_bang("る、瑠奈ちゃん12"), "る、瑠奈ちゃん！？")
        self.assertEqual(local_qwen.repair_ocr_bang("なに2"), "なに？")
        self.assertEqual(local_qwen.repair_ocr_bang("1"), "！")
        self.assertEqual(local_qwen.repair_ocr_bang("12"), "！？")
        self.assertEqual(local_qwen.repair_ocr_bang("第12話"), "第12話")
        self.assertEqual(local_qwen.repair_ocr_bang("39BURGER"), "39BURGER")
        self.assertEqual(local_qwen.repair_ocr_bang("あラインきてる"), "あラインきてる")

    def test_normalize_mapping_repairs_digit_bang_without_llm(self) -> None:
        data = {
            "えっ1!": "诶 1!",
            "る、瑠奈ちゃん12": "る、瑠奈ちゃん 12",
            "第1話は1人": "第1话是1人",
        }
        changed = local_qwen.normalize_mapping(data)
        self.assertEqual(data["えっ1!"], "诶！")
        self.assertEqual(data["えっ！"], "诶！")
        self.assertEqual(data["る、瑠奈ちゃん12"], "る、瑠奈ちゃん！？")
        self.assertEqual(data["第1話は1人"], "第1话是1人")
        self.assertIn("えっ1!", changed)
        self.assertIn("る、瑠奈ちゃん12", changed)

    def test_translation_ok_rejects_digit_bang_and_repetition(self) -> None:
        self.assertFalse(local_qwen.translation_ok("えっ1!", "诶 1!"))
        self.assertFalse(local_qwen.translation_ok("1", "1"))
        self.assertFalse(local_qwen.translation_ok("啊", "啊啊啊啊啊啊啊"))
        self.assertTrue(local_qwen.translation_ok("えっ1!", "诶！"))

    def test_translation_ok_rejects_japanese_echo_keeps_sfx(self) -> None:
        self.assertFalse(
            local_qwen.translation_ok(
                "自由奔放なゆるふわ系ギャルで姉達同様スタイル抜群で",
                "自由奔放なゆるふわ系ギャルで姉達同様スタイル抜群で",
            )
        )
        self.assertTrue(local_qwen.translation_ok("ドスン！", "ドスン！"))
        self.assertTrue(local_qwen.translation_ok("えっ1!", "诶！"))
        self.assertFalse(local_qwen.translation_ok("おはよう", ""))
        self.assertFalse(local_qwen.translation_ok("女子更衣室", "女子更衣室"))
        self.assertFalse(local_qwen.translation_ok("ドーテー", "ドーテー"))
        self.assertTrue(local_qwen.translation_ok("怜奈…", "怜奈…"))
        self.assertTrue(local_qwen.translation_ok("力希", "力希"))
        self.assertTrue(local_qwen.translation_ok("保健室", "保健室"))
        self.assertTrue(local_qwen.translation_ok("射精管理", "射精管理"))
        self.assertFalse(local_qwen.translation_ok("かみや君のおち○ちん", "神谷君的おち○ちん"))
        self.assertTrue(local_qwen.translation_ok("END", "END"))
        self.assertTrue(local_qwen.translation_ok("脳もち○ぽも溶けそう…ッ！", "脑子都要融化了…ッ！"))
        self.assertFalse(
            local_qwen.translation_ok("推済時、子たち", "推济时，孩子们"),
            "OCR garbage of a decorative title must not be hard-translated",
        )
        self.assertTrue(
            local_qwen.translation_ok(
                "私たちに精子を蒔いて孕ませて♥",
                "把精子播在我们身上让我们怀孕吧♥",
            )
        )
        self.assertTrue(local_qwen.translation_ok("保健室", "保健室"))
        self.assertFalse(
            local_qwen.translation_ok(
                "あ♥",
                "而且还是个连个一起抽烟聊天的朋友都没有的孤僻处男，身上满满的臭味呢～♡",
            ),
            "short moan must not keep an unrelated essay translation",
        )
        self.assertTrue(local_qwen.translation_ok("あ♥", "啊♥"))
        self.assertFalse(
            local_qwen.translation_ok(
                "Sweet Amber",
                "Sweet Amber漫画热牛奶浓一点vol.1",
            ),
            "English title must not absorb a Chinese magazine credit",
        )
        self.assertTrue(local_qwen.translation_ok("Bunny Time", "Bunny Time"))
        self.assertFalse(
            local_qwen.translation_ok(
                "もしかして",
                "难道你以为我会吻你吗？ 笨蛋——",
            ),
            "five-mora もしかして must not keep a longer balloon's translation",
        )
        self.assertTrue(local_qwen.translation_ok("もしかして", "难道是"))
        self.assertFalse(
            local_qwen.translation_ok("はい", "是…学长，这样可以吗？"),
            "two-mora はい must not keep a longer balloon's translation",
        )
        self.assertTrue(local_qwen.translation_ok("はい", "是…"))

    def test_store_translation_drops_japanese_echo(self) -> None:
        data = {"おはよう": ""}
        local_qwen.store_translation(data, "おはよう", "おはよう")
        self.assertEqual(data["おはよう"], "")
        local_qwen.store_translation(data, "おはよう", "早上好")
        self.assertEqual(data["おはよう"], "早上好")

    def test_store_translation_keeps_chinese_after_trailing_copula(self) -> None:
        data = {"キスもこんなに気持ちいいのかよ！": ""}
        local_qwen.store_translation(
            data, "キスもこんなに気持ちいいのかよ！", "接吻竟然这么舒服なのかよ！"
        )
        self.assertEqual(data["キスもこんなに気持ちいいのかよ！"], "接吻竟然这么舒服")
        self.assertTrue(
            local_qwen.translation_ok(
                "キスもこんなに気持ちいいのかよ！",
                data["キスもこんなに気持ちいいのかよ！"],
            )
        )
        self.assertFalse(
            local_qwen.translation_ok("かみや君のおち○ちん", "神谷君的おち○ちん")
        )

    def test_store_translation_keeps_previous_chinese_when_reply_rejected(self) -> None:
        data = {"ねぇ！！": "呐！！"}
        local_qwen.store_translation(data, "ねぇ！！", "")
        self.assertEqual(data["ねぇ！！"], "呐！！")
        local_qwen.store_translation(data, "ねぇ！！", "ねぇ！！")
        self.assertEqual(data["ねぇ！！"], "呐！！")
        local_qwen.store_translation(data, "ねぇ！！", "喂！！")
        self.assertEqual(data["ねぇ！！"], "喂！！")

    def test_mapped_translation_aliases_repaired_bang(self) -> None:
        mapping = {"えっ！": "诶！"}
        self.assertEqual(local_qwen.mapped_translation(mapping, "えっ1!"), "诶！")
        self.assertEqual(local_qwen.mapped_translation(mapping, "missing"), "")

    def test_repair_haa_and_kana_colon_ocr(self) -> None:
        self.assertEqual(local_qwen.repair_ocr_bang("４０："), "はぁ…")
        self.assertEqual(local_qwen.repair_ocr_bang("40…"), "はぁ…")
        self.assertEqual(local_qwen.repair_ocr_bang("セ：ンパイっ♥"), "センパイっ♥")
        self.assertFalse(local_qwen.translation_ok("４０：", "４０："))
        mapping = {"はぁ…": "哈啊…"}
        self.assertEqual(local_qwen.mapped_translation(mapping, "４０："), "哈啊…")

    def test_split_merged_toc_titles_keeps_chapter_two(self) -> None:
        merged = (
            "43P私たちに精子を蒔いて孕ませて♥〈第２話〉"
            "83P私たちに精子を蒔いて孕ませて♥〈第３話〉"
        )
        parts = local_qwen.split_merged_titles(merged)
        self.assertEqual(
            parts,
            [
                "43P私たちに精子を蒔いて孕ませて♥〈第２話〉",
                "83P私たちに精子を蒔いて孕ませて♥〈第３話〉",
            ],
        )
        self.assertEqual(
            local_qwen.split_merged_titles(
                "3P私たちに精子を蒔いて孕ませて♥〈第１話〉"
            ),
            ["3P私たちに精子を蒔いて孕ませて♥〈第１話〉"],
        )
        import bubble_recall_local

        boxes = [
            [304, 564, 415, 610],
            [307, 615, 718, 655],
            [308, 660, 670, 701],
            [305, 742, 414, 789],
            [307, 796, 718, 835],
            [309, 841, 671, 880],
        ]
        split = bubble_recall_local.split_title_boxes(merged, boxes)
        self.assertEqual(len(split), 2)
        self.assertIn("第２話", split[0][0])
        self.assertIn("第３話", split[1][0])
        self.assertLess(split[0][1][3], split[1][1][1])

    def test_contents_crop_boxes_splits_merged_sidecar(self) -> None:
        import tempfile
        import crop_mit_local
        import bubble_recall_local

        merged = (
            "43P私たちに精子を蒔いて孕ませて♥〈第２話〉"
            "83P私たちに精子を蒔いて孕ませて♥〈第３話〉"
        )
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            sidecars = root / "ocr_sidecars"
            sidecars.mkdir()
            (sidecars / "page_translations.txt").write_text(
                "\n".join(
                    [
                        "text: " + merged,
                        "coords: [304, 564, 415, 564, 415, 610, 304, 610]",
                        "coords: [307, 615, 718, 615, 718, 655, 307, 655]",
                        "coords: [308, 660, 670, 660, 670, 701, 308, 701]",
                        "coords: [305, 742, 414, 742, 414, 789, 305, 789]",
                        "coords: [307, 796, 718, 796, 718, 835, 307, 835]",
                        "coords: [309, 841, 671, 841, 671, 880, 309, 880]",
                    ]
                ),
                encoding="utf-8",
            )
            boxes = crop_mit_local.contents_crop_boxes(root, "page")
            self.assertEqual(len(boxes), 2)
            self.assertIn("第２話", boxes[0][0])
            self.assertIn("第３話", boxes[1][0])
            self.assertLess(boxes[0][1][1], boxes[1][1][1])
            padded = crop_mit_local.padded_box(boxes[0][1], 800, 1400, pad=16)
            self.assertEqual(padded[0], boxes[0][1][0] - 16)
            self.assertLessEqual(padded[3], 1400)
            separated = crop_mit_local.padded_box_away_from_neighbors(
                boxes[0][1],
                [boxes[0][1], boxes[1][1]],
                800,
                1400,
                pad=16,
            )
            self.assertLessEqual(separated[3], boxes[1][1][1])

    def test_uncovered_translated_boxes_skips_sfx_and_covered(self) -> None:
        import json
        import tempfile
        import crop_mit_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "translations.json").write_text(
                json.dumps(
                    {
                        "どうですか先生？この日のためにネットとバナナで勉強してたんです♡": "怎么样啊，老师？",
                        "はぁ": "",
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "page": {
                            "regions": [
                                {
                                    "xyxy": [380, 56, 473, 334],
                                    "text": "どうですか先生？この日のためにネットとバナナで勉強してたんです♡",
                                    "covered": False,
                                },
                                {
                                    "xyxy": [380, 56, 473, 334],
                                    "text": "どうですか先生？この日のためにネットとバナナで勉強してたんです♡",
                                    "covered": False,
                                },
                                {
                                    "xyxy": [10, 10, 40, 40],
                                    "text": "はぁ",
                                    "covered": False,
                                },
                                {
                                    "xyxy": [50, 50, 80, 80],
                                    "text": "どうですか先生？この日のためにネットとバナナで勉強してたんです♡",
                                    "covered": True,
                                },
                            ]
                        }
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            boxes = crop_mit_local.uncovered_translated_boxes(root, "page")
            self.assertEqual(len(boxes), 1)
            self.assertEqual(boxes[0][1], [380, 56, 473, 334])

    def test_uncovered_translated_boxes_skips_already_replaced_ink(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import crop_mit_local

        banana = "どうですか先生？この日のためにネットとバナナで勉強してたんです♡"
        left = "こういう勉強は…いけないんだぞくるみ君…あせぎる…"
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            source = Image.new("RGB", (500, 420), (240, 240, 240))
            draw = ImageDraw.Draw(source)
            for y in range(60, 330, 8):
                draw.line((385, y, 468, y), fill=(20, 20, 20), width=2)
            for y in range(224, 396, 8):
                draw.line((30, y, 128, y), fill=(20, 20, 20), width=2)
            source.save(root / "typeset_in" / "page.jpg", quality=100)
            dest = source.copy()
            wipe = ImageDraw.Draw(dest)
            wipe.rectangle((26, 220, 133, 400), fill=(240, 240, 240))
            wipe.rectangle((380, 56, 473, 180), fill=(240, 240, 240))
            dest.save(root / "out" / "page.jpg", quality=100)
            (root / "translations.json").write_text(
                json.dumps({banana: "怎么样啊，老师？", left: "这种学习方式"}, ensure_ascii=False),
                encoding="utf-8",
            )
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "page": {
                            "regions": [
                                {
                                    "xyxy": [380, 56, 473, 334],
                                    "text": banana,
                                    "covered": False,
                                },
                                {
                                    "xyxy": [26, 220, 133, 400],
                                    "text": left,
                                    "covered": False,
                                },
                            ]
                        }
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            boxes = crop_mit_local.uncovered_translated_boxes(root, "page")
            self.assertEqual(len(boxes), 1)
            self.assertEqual(boxes[0][1], [380, 56, 473, 334])

    def test_uncovered_translated_boxes_skips_untouched_art(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import crop_mit_local

        src = "わっっじゃないのよ"
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            page = Image.new("RGB", (400, 220), (245, 245, 245))
            draw = ImageDraw.Draw(page)
            for y in range(40, 180, 8):
                draw.line((40, y, 160, y), fill=(20, 20, 20), width=2)
            page.save(root / "typeset_in" / "page.jpg", quality=100)
            page.save(root / "out" / "page.jpg", quality=100)
            (root / "translations.json").write_text(
                json.dumps({src: "哇什么哇啊"}, ensure_ascii=False),
                encoding="utf-8",
            )
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "page": {
                            "regions": [
                                {
                                    "xyxy": [36, 36, 168, 184],
                                    "text": src,
                                    "covered": False,
                                }
                            ]
                        }
                    }
                ),
                encoding="utf-8",
            )
            boxes = crop_mit_local.uncovered_translated_boxes(root, "page")
            self.assertEqual(boxes, [])

    def test_uncovered_translated_boxes_skips_colophon_dump(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import crop_mit_local

        line = "格ゲーの中では私の方が上なんじゃ～！"
        dest = "在格斗游戏里我可是更强的哦～！"
        regions = []
        page = Image.new("RGB", (700, 900), (245, 245, 245))
        draw = ImageDraw.Draw(page)
        for i in range(5):
            box = [40, 40 + i * 160, 280, 150 + i * 160]
            for y in range(box[1] + 8, box[3] - 8, 8):
                draw.line((box[0] + 6, y, box[2] - 6, y), fill=(20, 20, 20), width=2)
            regions.append({"xyxy": box, "text": line, "covered": False})
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            page.save(root / "typeset_in" / "page.jpg", quality=100)
            dest_img = page.copy()
            erased = ImageDraw.Draw(dest_img)
            for i in range(5):
                box = [40, 40 + i * 160, 280, 150 + i * 160]
                erased.rectangle((box[0], box[1], box[2], box[1] + 40), fill=(245, 245, 245))
            dest_img.save(root / "out" / "page.jpg", quality=100)
            (root / "translations.json").write_text(
                json.dumps({line: dest}, ensure_ascii=False),
                encoding="utf-8",
            )
            (root / "bubble_recall.json").write_text(
                json.dumps({"page": {"regions": regions}}),
                encoding="utf-8",
            )
            boxes = crop_mit_local.uncovered_translated_boxes(root, "page")
            self.assertEqual(boxes, [])

    def test_save_pasted_page_jpeg_uses_mit_quality(self) -> None:
        import tempfile
        from PIL import Image
        import crop_mit_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            page = Image.new("RGB", (120, 80))
            pixels = page.load()
            for x in range(120):
                for y in range(80):
                    pixels[x, y] = ((x * 13) % 256, (y * 17) % 256, 90)
            dest = root / "page.jpg"
            crop_mit_local.save_pasted_page(page, dest)
            low = root / "q75.jpg"
            page.save(low, quality=75, format="JPEG")
            self.assertGreater(dest.stat().st_size, low.stat().st_size)

    def test_crop_overlay_map_keeps_episode_aliases_unique(self) -> None:
        import crop_mit_local

        overlay = crop_mit_local.crop_overlay_map(
            [
                (
                    "43P私たちに精子を蒔いて孕ませて♥〈第２話〉",
                    "43P\n第2话",
                ),
                (
                    "83P私たちに精子を蒔いて孕ませて♥〈第３話〉",
                    "83P\n第3话",
                ),
            ]
        )
        self.assertEqual(
            overlay["43P私たちに精子を蒔いて孕ませて♥〈第２話〉"],
            "43P\n第2话",
        )
        self.assertEqual(overlay["〈第２話〉"], "43P\n第2话")
        self.assertEqual(overlay["第３話"], "83P\n第3话")
        shared = "私たちに精子を蒔いて孕ませて♥"
        self.assertNotIn(shared, overlay)
        self.assertNotIn("43P", overlay)
        self.assertNotIn("83P", overlay)

    def test_title_bind_dst_strips_leading_page_number(self) -> None:
        import crop_mit_local

        self.assertEqual(
            crop_mit_local.title_bind_dst(
                "43P\n把精子播在我们身上\n让我们怀孕吧♥〈第2话〉"
            ),
            "把精子播在我们身上\n让我们怀孕吧♥〈第2话〉",
        )
        self.assertEqual(crop_mit_local.title_bind_dst("43P 第2话"), "第2话")
        self.assertEqual(crop_mit_local.title_bind_dst("第2话"), "第2话")

    def test_translated_crop_jobs_skips_empty_dst(self) -> None:
        import tempfile
        import crop_mit_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "translations.json").write_text(
                json.dumps(
                    {
                        "43P私たちに精子を蒔いて孕ませて♥〈第２話〉": "43P\n第2话",
                        "はぁ": "",
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            jobs = crop_mit_local.translated_crop_jobs(
                root,
                [
                    ("43P私たちに精子を蒔いて孕ませて♥〈第２話〉", [1, 2, 3, 4]),
                    ("はぁ", [5, 6, 7, 8]),
                ],
            )
            self.assertEqual(len(jobs), 1)
            self.assertEqual(jobs[0][1], "第2话")

    def test_contents_crop_boxes_drops_page_number_and_includes_single(self) -> None:
        import tempfile
        import crop_mit_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            sidecars = root / "ocr_sidecars"
            sidecars.mkdir()
            (sidecars / "page_translations.txt").write_text(
                "\n".join(
                    [
                        "text: 3P私たちに精子を蒔いて孕ませて♥〈第１話〉",
                        "coords: [304, 382, 380, 382, 380, 430, 304, 430]",
                        "coords: [308, 436, 718, 436, 718, 474, 308, 474]",
                        "coords: [309, 480, 671, 480, 671, 519, 309, 519]",
                        "text: "
                        "43P私たちに精子を蒔いて孕ませて♥〈第２話〉"
                        "83P私たちに精子を蒔いて孕ませて♥〈第３話〉",
                        "coords: [304, 564, 415, 564, 415, 610, 304, 610]",
                        "coords: [307, 615, 718, 615, 718, 655, 307, 655]",
                        "coords: [308, 660, 670, 660, 670, 701, 308, 701]",
                        "coords: [305, 742, 414, 742, 414, 789, 305, 789]",
                        "coords: [307, 796, 718, 796, 718, 835, 307, 835]",
                        "coords: [309, 841, 671, 841, 671, 880, 309, 880]",
                    ]
                ),
                encoding="utf-8",
            )
            boxes = crop_mit_local.contents_crop_boxes(root, "page")
            self.assertEqual(len(boxes), 3)
            self.assertIn("第１話", boxes[0][0])
            self.assertGreaterEqual(boxes[0][1][1], 430)
            self.assertIn("第２話", boxes[1][0])
            self.assertGreaterEqual(boxes[1][1][1], 610)
            self.assertGreaterEqual(boxes[1][1][2] - boxes[1][1][0], 300)
            keepouts = crop_mit_local.contents_keepout_boxes(root, "page")
            self.assertTrue(any(box[1] < 430 for box in keepouts))

    def test_expand_box_to_cover_out_ink(self) -> None:
        from PIL import Image, ImageDraw
        import crop_mit_local

        source = Image.new("RGB", (200, 80), (180, 40, 40))
        dest = source.copy()
        draw = ImageDraw.Draw(dest)
        draw.rectangle((20, 20, 160, 50), fill=(245, 245, 245))
        grown = crop_mit_local.expand_box_to_cover_diff(
            [40, 22, 90, 48],
            source,
            dest,
            others=[],
        )
        self.assertLessEqual(grown[0], 20)
        self.assertGreaterEqual(grown[2], 160)

    def test_mit_crop_queue_assigns_only_longest_query(self) -> None:
        import tempfile

        mit_root = Path(r"D:\grok\tools\manga-image-translator")
        mit_py = mit_root / ".venv" / "Scripts" / "python.exe"
        if not mit_py.is_file():
            self.skipTest("MIT venv missing")
        with tempfile.TemporaryDirectory() as tmp:
            trans = Path(tmp) / "translations.json"
            trans.write_text("{}", encoding="utf-8")
            script = (
                "import asyncio, os, json\n"
                f"os.environ['MIT_TRANSLATIONS_JSON'] = r'{trans}'\n"
                "os.environ['MIT_CROP_QUEUE'] = json.dumps(['整句中文'], ensure_ascii=False)\n"
                "from manga_translator.translators.none import NoneTranslator\n"
                "tr = NoneTranslator()\n"
                "out = asyncio.run(tr._translate('JPN', 'CHS', "
                "['こういう勉強はいけないんだぞくるみ君', 'まじめすぎる']))\n"
                "print(json.dumps(out, ensure_ascii=False))\n"
            )
            proc = subprocess.run(
                [str(mit_py), "-c", script],
                cwd=str(mit_root),
                capture_output=True,
                text=True,
                encoding="utf-8",
            )
            self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
            dsts = json.loads(proc.stdout.strip().splitlines()[-1])
            self.assertEqual(dsts[0], "整句中文")
            self.assertEqual(dsts[1], "")

    def test_vertical_ink_columns_splits_three_bars(self) -> None:
        from PIL import Image, ImageDraw
        import crop_mit_local

        img = Image.new("RGB", (120, 200), (250, 250, 250))
        draw = ImageDraw.Draw(img)
        draw.rectangle((20, 20, 36, 180), fill=(10, 10, 10))
        draw.rectangle((52, 20, 68, 180), fill=(10, 10, 10))
        draw.rectangle((84, 20, 100, 180), fill=(10, 10, 10))
        cols = crop_mit_local.vertical_ink_columns(img, [10, 10, 110, 190])
        self.assertEqual(len(cols), 3)
        self.assertLess(cols[0][0], cols[1][0])
        self.assertLess(cols[1][0], cols[2][0])
        self.assertLess(cols[0][2], cols[1][0])

    def test_split_dest_for_columns_is_rtl(self) -> None:
        import crop_mit_local

        parts = crop_mit_local.split_dest_for_columns(
            "说是体育特长生，简直让人无语！！给我剃掉一边眉毛，闭门思过吧！！",
            3,
        )
        self.assertEqual(len(parts), 3)
        self.assertTrue(parts[0].startswith("说是体育特长生"))
        self.assertIn("无语", parts[1])
        self.assertNotIn("给我剃掉", parts[1])
        self.assertIn("闭门思过", parts[2])
        hearts = crop_mit_local.split_dest_for_columns("快点♥快点♥快点把鸡巴…", 2)
        self.assertEqual(hearts, ["快点♥快点♥", "快点把鸡巴…"])
        scoop = crop_mit_local.split_dest_for_columns(
            "地狱猫的真面目居然是三年A班的山田惠子！！这可是大独家啊！",
            3,
        )
        self.assertEqual(len(scoop), 3)
        self.assertIn("地狱猫", scoop[0])
        self.assertIn("山田惠子", scoop[1])
        self.assertIn("大独家", scoop[2])

    def test_tiny_font_balloons_need_vertical_leftover_and_long_dest(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import crop_mit_local

        src_text = "スポーツ特待生が聞いて呆れる！！片眉剃って山に籠れ！！"
        dest_text = "说是体育特长生，简直让人无语！！给我剃掉一边眉毛，闭门思过吧！！"
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            source = Image.new("RGB", (200, 320), (245, 245, 245))
            draw = ImageDraw.Draw(source)
            for x in range(48, 82, 6):
                draw.line((x, 44, x, 276), fill=(15, 15, 15), width=2)
            source.save(root / "typeset_in" / "page.jpg", quality=100)
            dest = source.copy()
            ImageDraw.Draw(dest).rectangle((50, 48, 80, 110), fill=(245, 245, 245))
            dest.save(root / "out" / "page.jpg", quality=100)
            (root / "translations.json").write_text(
                json.dumps({src_text: dest_text, "はぁ": "哈啊"}, ensure_ascii=False),
                encoding="utf-8",
            )
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "page": {
                            "regions": [
                                {
                                    "xyxy": [40, 40, 90, 280],
                                    "text": src_text,
                                    "covered": False,
                                },
                                {
                                    "xyxy": [10, 10, 30, 28],
                                    "text": "はぁ",
                                    "covered": False,
                                },
                            ]
                        }
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            found = crop_mit_local.tiny_font_balloons(root, "page")
            self.assertEqual([row[0] for row in found], [src_text])
            self.assertEqual(found[0][1], [40, 40, 90, 280])

    def test_padded_box_away_from_neighbors_clips_side_by_side(self) -> None:
        import crop_mit_local

        left = [10, 10, 40, 200]
        right = [50, 10, 80, 200]
        padded = crop_mit_local.padded_box_away_from_neighbors(
            left, [left, right], 200, 240, pad=16
        )
        self.assertLessEqual(padded[2], 50)
        self.assertGreater(padded[2], left[2])

    def test_prepare_crop_canvas_white_pads_narrow_column(self) -> None:
        from PIL import Image
        import crop_mit_local

        crop = Image.new("RGB", (20, 80), (10, 10, 10))
        canvas, content, scale = crop_mit_local.prepare_crop_canvas(crop)
        self.assertEqual(scale, 4)
        self.assertEqual(content, (80, 320))
        self.assertGreaterEqual(min(canvas.size), 400)
        self.assertEqual(canvas.getpixel((399, 399)), (255, 255, 255))
        self.assertEqual(canvas.getpixel((0, 0)), (10, 10, 10))

    def test_prepare_crop_canvas_leaves_large_crop(self) -> None:
        from PIL import Image
        import crop_mit_local

        crop = Image.new("RGB", (500, 600), (12, 34, 56))
        canvas, content, scale = crop_mit_local.prepare_crop_canvas(crop)
        self.assertEqual(scale, 1)
        self.assertEqual(content, (500, 600))
        self.assertEqual(canvas.size, (500, 600))

    def test_crop_content_from_canvas_drops_white_pad(self) -> None:
        from PIL import Image
        import crop_mit_local

        canvas = Image.new("RGB", (400, 400), (255, 255, 255))
        canvas.paste(Image.new("RGB", (80, 320), (3, 4, 5)), (0, 0))
        cut = crop_mit_local.crop_content_from_canvas(canvas, (80, 320))
        self.assertEqual(cut.size, (80, 320))
        self.assertEqual(cut.getpixel((0, 0)), (3, 4, 5))

    def test_crop_mit_config_uses_official_small_image_knobs(self) -> None:
        import crop_mit_local

        cfg = crop_mit_local.crop_mit_config()
        self.assertEqual(cfg["detector"]["detection_size"], 1024)
        self.assertEqual(cfg["ocr"]["prob"], 0.05)
        self.assertEqual(cfg["translator"]["translator"], "none")
        self.assertEqual(cfg["inpainter"]["inpainter"], "lama_large")

    def test_crop_row_pastes_inpaint_even_if_dest_empty(self) -> None:
        import crop_mit_local

        self.assertTrue(
            crop_mit_local.crop_row_should_paste({"queries": ["けいこ"], "dsts": [""]})
        )
        self.assertTrue(
            crop_mit_local.crop_row_should_paste({"queries": ["三年A組"], "dsts": ["三年A班"]})
        )
        self.assertFalse(crop_mit_local.crop_row_should_paste({"queries": [], "dsts": []}))
        self.assertFalse(crop_mit_local.crop_row_should_paste({"queries": [""], "dsts": [""]}))

    def test_after_mit_passes_runs_tiny_font_only_on_success(self) -> None:
        import tempfile
        import typeset_ocr_local
        import crop_mit_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            with patch.object(crop_mit_local, "crop_mit_pages", return_value=2) as tiny:
                self.assertEqual(typeset_ocr_local.after_mit_passes(root, ["page"], 0), 2)
                tiny.assert_called_once_with(
                    root,
                    ["page"],
                    contents=False,
                    bubbles=False,
                    tiny_font=True,
                )
                tiny.reset_mock()
                self.assertEqual(typeset_ocr_local.after_mit_passes(root, ["page"], 1), 0)
                tiny.assert_not_called()

    def test_overlay_pages_keeps_sidecar_page_number_on_contents(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import bubble_recall_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            (root / "ocr_sidecars").mkdir()
            src = Image.new("RGB", (400, 200), (180, 40, 40))
            draw = ImageDraw.Draw(src)
            draw.rectangle([40, 40, 90, 70], fill=(250, 250, 250))
            draw.rectangle([40, 80, 300, 110], fill=(250, 250, 250))
            draw.rectangle([40, 120, 260, 150], fill=(250, 250, 250))
            src.save(root / "typeset_in" / "page.jpg")
            src.save(root / "out" / "page.jpg")
            (root / "ocr_sidecars" / "page_translations.txt").write_text(
                "\n".join(
                    [
                        "text:  3P私たちに精子を蒔いて孕ませて♥〈第１話〉",
                        "coords: [40, 40, 90, 40, 90, 70, 40, 70]",
                        "coords: [40, 80, 300, 80, 300, 110, 40, 110]",
                        "coords: [40, 120, 260, 120, 260, 150, 40, 150]",
                    ]
                ),
                encoding="utf-8",
            )
            (root / "translations.json").write_text(
                json.dumps(
                    {
                        "3P私たちに精子を蒔いて孕ませて♥〈第１話〉": "3P\n把精子播在我们身上\n让我们怀孕吧♥〈第1话〉",
                        "私たちに精子を蒔いて孕ませて♥〈第１話〉": "把精子播在我们身上\n让我们怀孕吧♥〈第1话〉",
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "page": {
                            "mtime": 1,
                            "regions": [
                                {
                                    "xyxy": [40, 40, 300, 150],
                                    "text": "私たちに精子を蒔いて孕ませて♥〈第１話〉",
                                    "score": 0.9,
                                    "covered": True,
                                }
                            ],
                        }
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            painted: list[str] = []
            real_render = bubble_recall_local.render_translation

            def capture(image, box, text, source=None):
                painted.append(text)
                return real_render(image, box, text, source)

            with patch.object(bubble_recall_local, "render_translation", side_effect=capture):
                bubble_recall_local.overlay_pages(root, ["page"])
            self.assertTrue(painted, "contents titles must be overlaid")
            self.assertTrue(
                any(str(item).lstrip().startswith("3P") for item in painted),
                f"sidecar dest keeps 3P, recall dest without 3P must not win: {painted}",
            )

    def test_repair_ocr_garbled_tame_ni_and_banana(self) -> None:
        self.assertEqual(
            local_qwen.repair_ocr_bang("この日のだめにネタトと"),
            "この日のためにネットと",
        )
        self.assertEqual(
            local_qwen.repair_ocr_bang(
                "どうですか先生？この日のためにネットとパナナで勉強してたんです♡"
            ),
            "どうですか先生？この日のためにネットとバナナで勉強してたんです♡",
        )

    def test_repair_ocr_strips_moan_yen_hallucination(self) -> None:
        src = "あの…先生もう大丈夫ですから…強めに動いても…♡１００万円♡"
        cleaned = local_qwen.repair_ocr_bang(src)
        self.assertNotIn("万", cleaned)
        self.assertNotIn("円", cleaned)
        self.assertIn("先生もう大丈夫", cleaned)
        self.assertFalse(
            local_qwen.translation_ok(src, "那个…老师已经没事了…♡１００万円♡")
        )

    def test_prepare_ocr_mapping_aliases_partial_and_placeholder(self) -> None:
        data = {
            "どうですか先生？この日のだめにネタトと": "",
            "どうですか先生？この日のためにネットとバナナで勉強してたんです♡": "",
            "皆さん紹介しますね今年からこの夢仁○○○○学校の先生を務めて下さる大津々耕作先生です": "",
            "皆さん紹介しますね今年からこの夢仁小中高等学校の先生を務めて下さる大津々耕作先生です": "",
            "43P私たちに精子を蒔いて孕ませて♥〈第２話〉83P私たちに精子を蒔いて孕ませて♥〈第３話〉": "",
        }
        local_qwen.prepare_ocr_mapping(data)
        self.assertIn(
            "43P私たちに精子を蒔いて孕ませて♥〈第２話〉", data
        )
        self.assertIn(
            "83P私たちに精子を蒔いて孕ませて♥〈第３話〉", data
        )
        self.assertEqual(
            local_qwen.preferred_ocr_key(
                data, "どうですか先生？この日のだめにネタトと"
            ),
            "どうですか先生？この日のためにネットとバナナで勉強してたんです♡",
        )
        self.assertEqual(
            local_qwen.preferred_ocr_key(
                data,
                "皆さん紹介しますね今年からこの夢仁○○○○学校の先生を務めて下さる大津々耕作先生です",
            ),
            "皆さん紹介しますね今年からこの夢仁小中高等学校の先生を務めて下さる大津々耕作先生です",
        )

    def test_galtransl_glossary_pins_male_senpai_when_kun_present(self) -> None:
        gloss = local_qwen.galtransl_glossary(
            {"奏汰くん": "奏汰君", "おはよう": "早上好"}
        )
        self.assertIn("センパイ->学长", gloss)
        self.assertIn("先輩->学长", gloss)
        self.assertNotIn(
            "センパイ->学长",
            local_qwen.galtransl_glossary({"おはよう": "早上好"}),
        )
        self.assertIn("夢って->", local_qwen.galtransl_glossary({"おはよう": "早上好"}))
        kurumi = local_qwen.galtransl_glossary({"くるみ君": "来海君"})
        self.assertIn("くるみ->胡桃", kurumi)
        gloss = local_qwen.galtransl_glossary({"わだかまり": "", "プライベートでは": ""})
        self.assertIn("わだかまり->", gloss)
        self.assertIn("プライベート->", gloss)
        moshi = local_qwen.galtransl_glossary({"もしかして": "", "おはよう": "早上好"})
        self.assertIn("もしかして->难道是", moshi)
        self.assertNotIn(
            "もしかして->",
            local_qwen.galtransl_glossary({"おはよう": "早上好"}),
        )

    def test_image_system_pins_kurumi_name(self) -> None:
        self.assertIn("くるみ", local_qwen.IMAGE_SYSTEM)
        self.assertIn("胡桃", local_qwen.IMAGE_SYSTEM)
        self.assertIn("来海", local_qwen.IMAGE_SYSTEM)

    def test_repair_locked_names_kurumi_to_hutao(self) -> None:
        self.assertEqual(
            local_qwen.repair_locked_names("うっっ…くるみ君ッッッ！", "呜…来海君！"),
            "呜…胡桃君！",
        )
        self.assertEqual(
            local_qwen.repair_locked_names(
                "こういう勉強は…いけないんだぞくるみ君…",
                "这种学习方式…可不行哦九琉璃君…",
            ),
            "这种学习方式…可不行哦胡桃君…",
        )
        self.assertEqual(
            local_qwen.repair_locked_names("おはよう", "早上好"),
            "早上好",
        )
        data = {"くるみ君⁉": "来海君！？", "おはよう": "早上好"}
        local_qwen.apply_locked_names(data)
        self.assertEqual(data["くるみ君⁉"], "胡桃君！？")
        self.assertEqual(data["おはよう"], "早上好")

    def test_keep_source_art_skips_pen_name_not_dialogue(self) -> None:
        import bubble_recall_local

        self.assertTrue(local_qwen.keep_source_art("たらかん"))
        self.assertTrue(local_qwen.keep_source_art("-たらかん"))
        self.assertFalse(local_qwen.keep_source_art("くるみ君"))
        self.assertFalse(local_qwen.keep_source_art("だから"))
        self.assertFalse(local_qwen.keep_source_art("あとで"))
        self.assertFalse(local_qwen.keep_source_art("あとがき"))
        self.assertFalse(local_qwen.keep_source_art("とりあえず"))
        self.assertFalse(local_qwen.keep_source_art("おはよう"))
        self.assertFalse(local_qwen.keep_source_art("村おこしは"))
        self.assertFalse(
            local_qwen.keep_source_art("もしかして"),
            "5-kana dialogue is not a pen-name signature",
        )
        self.assertFalse(
            bubble_recall_local.skip_as_art("もしかして", overlay=False)
        )
        self.assertTrue(
            bubble_recall_local.skip_as_art("Bunny Time", overlay=False),
            "English contents titles must not go through JP→ZH fill",
        )

    def test_apply_latin_identity_keeps_english_titles(self) -> None:
        import bubble_recall_local

        data = {
            "Sweet Amber": "",
            "Sad But True": "Sad But True漫画热牛奶2022年9月号",
            "もしかして": "",
            "Bunny Time": "Bunny Time",
        }
        changed = bubble_recall_local.apply_latin_identity(data)
        self.assertEqual(data["Sweet Amber"], "Sweet Amber")
        self.assertEqual(data["Sad But True"], "Sad But True")
        self.assertEqual(data["もしかして"], "")
        self.assertEqual(data["Bunny Time"], "Bunny Time")
        self.assertIn("Sweet Amber", changed)
        self.assertIn("Sad But True", changed)
        self.assertNotIn("Bunny Time", changed)

    def test_short_vocal_dest_maps_moans_not_dialogue(self) -> None:
        self.assertEqual(local_qwen.short_vocal_dest("ふあああつ♥"), "呼啊啊♥")
        self.assertEqual(local_qwen.short_vocal_dest("えぇっ♥"), "欸♥")
        self.assertEqual(local_qwen.short_vocal_dest("あぁっ♡"), "啊啊♡")
        self.assertEqual(local_qwen.short_vocal_dest("あ♥"), "啊♥")
        self.assertEqual(local_qwen.short_vocal_dest("ふあぁあっ♥♥"), "呼啊啊♥♥")
        self.assertEqual(local_qwen.short_vocal_dest("…ハァ"), "…哈啊")
        self.assertEqual(local_qwen.short_vocal_dest("．．．ハァ"), "…哈啊")
        self.assertEqual(local_qwen.short_vocal_dest("もしかして"), "")
        self.assertEqual(local_qwen.short_vocal_dest("はい"), "")
        self.assertEqual(local_qwen.short_vocal_dest("おい"), "")
        self.assertEqual(local_qwen.short_vocal_dest("だ…"), "")
        self.assertTrue(
            local_qwen.translation_ok("ふあああつ♥", local_qwen.short_vocal_dest("ふあああつ♥"))
        )
        self.assertTrue(local_qwen.translation_ok("えぇっ♥", "欸♥"))
        self.assertTrue(local_qwen.translation_ok("あ♥", "啊♥"))

    def test_apply_short_vocal_dest_fills_empty_not_good_or_dialogue(self) -> None:
        import bubble_recall_local

        data = {
            "えぇっ♥": "",
            "ふあぁあっ♥♥": "呼啊啊♥♥",
            "もしかして": "",
            "あ♥": "而且还是个连个一起抽烟聊天的朋友都没有的孤僻处男，身上满满的臭味呢～♡",
            "ふふふっ": "",
            "．．．ハァ": "",
            "だ…": "",
        }
        changed = bubble_recall_local.apply_short_vocal_dest(data)
        self.assertEqual(data["えぇっ♥"], "欸♥")
        self.assertEqual(data["ふあぁあっ♥♥"], "呼啊啊♥♥")
        self.assertEqual(data["もしかして"], "")
        self.assertEqual(data["あ♥"], "啊♥")
        self.assertEqual(data["ふふふっ"], "")
        self.assertEqual(data["．．．ハァ"], "…哈啊")
        self.assertEqual(data["だ…"], "")
        self.assertIn("えぇっ♥", changed)
        self.assertIn("あ♥", changed)
        self.assertIn("．．．ハァ", changed)
        self.assertNotIn("ふあぁあっ♥♥", changed)
        self.assertNotIn("もしかして", changed)
        self.assertNotIn("ふふふっ", changed)
        self.assertNotIn("だ…", changed)

    def test_image_system_forbids_returning_japanese(self) -> None:
        self.assertIn("禁止把日文原样返回", local_qwen.IMAGE_SYSTEM)
        self.assertIn("禁止在译文里留平假名", local_qwen.IMAGE_RETRY_SYSTEM)

    def test_image_system_asks_for_spoken_chinese(self) -> None:
        self.assertIn("说出口", local_qwen.IMAGE_SYSTEM)
        self.assertIn("不要逐字", local_qwen.IMAGE_SYSTEM)
        self.assertIn("下面", local_qwen.IMAGE_SYSTEM)
        self.assertIn("梦里", local_qwen.IMAGE_SYSTEM)
        self.assertIn("夢って", local_qwen.IMAGE_SYSTEM)
        payload = local_qwen.chat_payload(
            local_qwen.LOCAL_MODEL, ["もっとちょうだい"], local_qwen.IMAGE_SYSTEM
        )
        user = payload["messages"][1]["content"]
        self.assertIn("不要逐字", user)
        self.assertNotIn("将以下文本数组逐条翻译成简体中文", user)

    def test_galtransl_payload_follows_official_sakura_prompt(self) -> None:
        self.assertTrue(local_qwen.is_galtransl_model("sakura-galtransl-7b-v3-7"))
        self.assertFalse(local_qwen.is_galtransl_model(local_qwen.LOCAL_MODEL))
        payload = local_qwen.chat_payload(
            "sakura-galtransl-7b-v3-7",
            ["もっとおち○ちんちょうだい♥"],
            local_qwen.IMAGE_SYSTEM,
            glossary="センパイ->前辈",
        )
        self.assertEqual(payload["messages"][0]["content"], local_qwen.GALTRANSL_SYSTEM)
        user = payload["messages"][1]["content"]
        self.assertIn("参考以下术语表", user)
        self.assertIn(
            "根据以上术语表的对应关系和备注，结合历史剧情和上下文，将下面的文本从日文翻译成简体中文：",
            user,
        )
        self.assertIn("センパイ->前辈", user)
        self.assertIn("もっとおち○ちんちょうだい♥", user)
        self.assertNotIn("json_schema", payload)
        self.assertEqual(payload["top_p"], 0.8)
        self.assertEqual(
            local_qwen.parse_galtransl_reply("再多给我♥", 1),
            ["再多给我♥"],
        )

    def test_galtransl_leftover_payload_asks_short_line(self) -> None:
        default = local_qwen.chat_payload(
            "sakura-galtransl-7b-v3-7",
            ["ワタシ．．．"],
            local_qwen.IMAGE_SYSTEM,
        )
        leftover = local_qwen.chat_payload(
            "sakura-galtransl-7b-v3-7",
            ["ワタシ．．．"],
            local_qwen.IMAGE_SYSTEM,
            leftover=True,
        )
        self.assertNotIn("只译这一句", default["messages"][1]["content"])
        user = leftover["messages"][1]["content"]
        self.assertIn("只译这一句", user)
        self.assertIn("ワタシ．．．", user)
        self.assertEqual(leftover["messages"][0]["content"], local_qwen.GALTRANSL_SYSTEM)

    def test_galtransl_retry_batch_uses_leftover_prompt(self) -> None:
        import fill_ocr_local

        captured: dict = {}

        def fake_texts(batch, model=None, glossary="", leftover=False, **kwargs):
            captured["leftover"] = leftover
            captured["batch"] = list(batch)
            return ["我……"]

        with (
            patch.object(
                local_qwen, "fill_local_model", return_value="sakura-galtransl-7b-v3-7"
            ),
            patch.object(local_qwen, "translate_texts", side_effect=fake_texts),
        ):
            out = fill_ocr_local._translate_batch(
                ["ワタシ．．．"], engine="local", retry=True
            )
        self.assertEqual(out, ["我……"])
        self.assertEqual(captured["batch"], ["ワタシ．．．"])
        self.assertTrue(captured["leftover"])

    def test_fill_leftover_isolated_retry_keeps_short_dest(self) -> None:
        import tempfile
        import fill_ocr_local

        calls: list[list[str]] = []

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            if len(batch) == 1 and "どうした" in batch[0]:
                return ["怎么了？"]
            return ["诶…诶诶，可是为什么会变成这样呢我完全不知道"] * len(batch)

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            data = {
                "どうした？": "",
                "長い対白ですよね本当に長い対白": "",
            }
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake
            )
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(saved["どうした？"], "怎么了？")
            self.assertTrue(
                any(len(batch) == 1 and "どうした" in batch[0] for batch in calls)
            )

    def test_fill_leftover_only_skips_dest_ok(self) -> None:
        import tempfile
        import fill_ocr_local

        calls: list[list[str]] = []

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            return ["怎么了？"] * len(batch)

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            data = {"おはよう": "早上好", "どうした？": ""}
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake, leftover_only=True
            )
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(saved["おはよう"], "早上好")
            self.assertEqual(saved["どうした？"], "怎么了？")
            sent = [item for batch in calls for item in batch]
            self.assertNotIn("おはよう", sent)
            self.assertTrue(any("どう了" in item or "どうした" in item for item in sent))
            self.assertTrue(all(len(batch) == 1 for batch in calls))

    def test_fill_leftover_only_does_not_expand_to_longer_key(self) -> None:
        import tempfile
        import fill_ocr_local

        calls: list[list[str]] = []
        long_key = "どうした？長い対白ですよね本当に長い対白"
        long_dest = "怎么了？这是一句很长的对白啊真的很长"

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            if any("長い対白" in item for item in batch):
                return ["诶…诶诶，可是为什么会变成这样呢我完全不知道"] * len(batch)
            return ["怎么了？"] * len(batch)

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            data = {"どうした？": "", long_key: long_dest}
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake, leftover_only=True
            )
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(saved["どうした？"], "怎么了？")
            self.assertEqual(saved[long_key], long_dest)
            sent = [item for batch in calls for item in batch]
            self.assertTrue(any(item == "どうした？" for item in sent))
            self.assertFalse(any("長い対白" in item for item in sent))

    def test_active_local_model_prefers_env(self) -> None:
        previous = os.environ.get("HANHUA_LOCAL_MODEL")
        os.environ["HANHUA_LOCAL_MODEL"] = "sakura-galtransl-7b-v3-7"
        try:
            self.assertEqual(
                local_qwen.active_local_model(), "sakura-galtransl-7b-v3-7"
            )
        finally:
            if previous is None:
                os.environ.pop("HANHUA_LOCAL_MODEL", None)
            else:
                os.environ["HANHUA_LOCAL_MODEL"] = previous

    def test_load_local_honors_hanhua_model_env(self) -> None:
        previous = os.environ.get("HANHUA_LOCAL_MODEL")
        try:
            os.environ.pop("HANHUA_LOCAL_MODEL", None)
            _url, _key, model = translate_direct.load_local()
            self.assertEqual(model, local_qwen.LOCAL_MODEL)
            os.environ["HANHUA_LOCAL_MODEL"] = local_qwen.HANHUA_FILL_MODEL
            _url, _key, model = translate_direct.load_local()
            self.assertEqual(model, local_qwen.HANHUA_FILL_MODEL)
        finally:
            if previous is None:
                os.environ.pop("HANHUA_LOCAL_MODEL", None)
            else:
                os.environ["HANHUA_LOCAL_MODEL"] = previous

    def test_mit_root_prefers_env_then_layout(self) -> None:
        previous = os.environ.get("HANHUA_MIT_ROOT")
        try:
            os.environ.pop("HANHUA_MIT_ROOT", None)
            layout = Path(local_qwen.__file__).resolve().parents[2] / "tools" / "manga-image-translator"
            self.assertEqual(local_qwen.mit_root(), layout)
            os.environ["HANHUA_MIT_ROOT"] = r"D:\custom-mit"
            self.assertEqual(local_qwen.mit_root(), Path(r"D:\custom-mit"))
        finally:
            if previous is None:
                os.environ.pop("HANHUA_MIT_ROOT", None)
            else:
                os.environ["HANHUA_MIT_ROOT"] = previous

    def test_fill_local_model_defaults_to_galtransl(self) -> None:
        previous = os.environ.get("HANHUA_LOCAL_MODEL")
        os.environ.pop("HANHUA_LOCAL_MODEL", None)
        try:
            self.assertEqual(
                local_qwen.fill_local_model(), local_qwen.HANHUA_FILL_MODEL
            )
            self.assertTrue(local_qwen.is_galtransl_model(local_qwen.fill_local_model()))
        finally:
            if previous is None:
                os.environ.pop("HANHUA_LOCAL_MODEL", None)
            else:
                os.environ["HANHUA_LOCAL_MODEL"] = previous

    def test_empty_ocr_json_counts_as_missing_skip_counts_done(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "in"
            ocr = Path(tmp) / "ocr"
            src.mkdir()
            ocr.mkdir()
            empty = src / "empty.png"
            skip = src / "skip.png"
            filled = src / "filled.png"
            missing = src / "missing.png"
            for page in (empty, skip, filled, missing):
                page.write_bytes(b"p")
            (ocr / "empty_ocr.json").write_text("[]", encoding="utf-8")
            (ocr / "skip_ocr.json").write_text(
                '[{"skip": true}]', encoding="utf-8"
            )
            (ocr / "filled_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            names = [
                p.name
                for p in local_qwen.missing_ocr_pages(
                    [empty, skip, filled, missing], [ocr]
                )
            ]
            self.assertEqual(names, ["empty.png", "missing.png"])

    def test_missing_ocr_pages_lists_stems_without_sidecar(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "in"
            ocr = Path(tmp) / "ocr"
            src.mkdir()
            ocr.mkdir()
            a = src / "a.png"
            b = src / "b.png"
            a.write_bytes(b"a")
            b.write_bytes(b"b")
            (ocr / "a_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            missing = local_qwen.missing_ocr_pages([a, b], [ocr])
            self.assertEqual([p.name for p in missing], ["b.png"])

    def test_typeset_finds_jpg_only_folder(self) -> None:
        import tempfile
        import typeset_ocr_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            folder = root / "typeset_in"
            folder.mkdir()
            (folder / "001_Cover.jpg").write_bytes(b"jpg")
            self.assertEqual(typeset_ocr_local.find_input(root), folder)

    def test_fill_retries_echo_repairs_bang_and_aliases_lookup_key(self) -> None:
        import tempfile
        import fill_ocr_local

        calls: list[list[str]] = []

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            out = []
            for text in batch:
                if text == "えっ！":
                    out.append("诶！")
                elif "自由奔放" in text:
                    if len(calls) == 1:
                        out.append(text)
                    else:
                        out.append("性格自由奔放的软萌辣妹")
                else:
                    out.append("中文")
            return out

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            data = {
                "えっ1!": "",
                "自由奔放なゆるふわ系ギャルで姉達同様スタイル抜群で": "",
            }
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake
            )
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(saved["えっ1!"], "诶！")
            self.assertEqual(saved["えっ！"], "诶！")
            self.assertEqual(
                saved["自由奔放なゆるふわ系ギャルで姉達同様スタイル抜群で"],
                "性格自由奔放的软萌辣妹",
            )
            self.assertGreaterEqual(len(calls), 2)
            self.assertIn("えっ！", calls[0])
            filled_keys = json.loads(
                (dest.parent / "filled_keys.json").read_text(encoding="utf-8")
            )
            self.assertIn("えっ1!", filled_keys)
            self.assertIn(
                "自由奔放なゆるふわ系ギャルで姉達同様スタイル抜群で", filled_keys
            )

    def test_fill_normalizes_digit_bang_and_marks_changed_for_typeset(self) -> None:
        import tempfile
        import fill_ocr_local

        calls: list[list[str]] = []

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            return ["中文"] * len(batch)

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            data = {"えっ1!": "诶 1!", "おはよう": "早上好"}
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake
            )
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(saved["えっ1!"], "诶！")
            self.assertEqual(saved["おはよう"], "早上好")
            filled_keys = json.loads(
                (dest.parent / "filled_keys.json").read_text(encoding="utf-8")
            )
            self.assertIn("えっ1!", filled_keys)
            self.assertFalse(any("おはよう" in batch for batch in calls))

    def test_fill_rewrite_resends_ok_dialogue_skips_sfx(self) -> None:
        import tempfile
        import fill_ocr_local

        calls: list[list[str]] = []

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            out = []
            for text in batch:
                if "ちょうだい" in text:
                    out.append("再多给我♥")
                elif text == "おはよう":
                    out.append("早上好")
                else:
                    out.append("中文")
            return out

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            data = {
                "もっとおち○ちんちょうだい♥": "再让我下面一点♥",
                "ドスン！": "ドスン！",
                "おはよう": "",
            }
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake, rewrite=True
            )
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(saved["もっとおち○ちんちょうだい♥"], "再多给我♥")
            self.assertEqual(saved["ドスン！"], "ドスン！")
            self.assertEqual(saved["おはよう"], "早上好")
            sent = [item for batch in calls for item in batch]
            self.assertIn("もっとおち○ちんちょうだい♥", sent)
            self.assertIn("おはよう", sent)
            self.assertNotIn("ドスン！", sent)

    def test_fill_skips_sfx_art_on_first_pass(self) -> None:
        import tempfile
        import fill_ocr_local

        calls: list[list[str]] = []

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            return ["早上好"] * len(batch)

        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            data = {
                "で、で、": "",
                "そして、": "",
                "にぎにぎ": "",
                "おはよう": "",
                "ね♥": "",
            }
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake
            )
            sent = [item for batch in calls for item in batch]
            self.assertEqual(sent, ["おはよう", "ね♥"])
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(saved["で、で、"], "")
            self.assertEqual(saved["そして、"], "")
            self.assertEqual(saved["にぎにぎ"], "")
            self.assertEqual(saved["おはよう"], "早上好")
            self.assertEqual(saved["ね♥"], "早上好")

    def test_ocr_missing_only_merges_and_skips_complete_pages(self) -> None:
        import tempfile
        import ocr_extract_local

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            work = Path(tmp) / "work"
            typeset_in = work / "typeset_in"
            sidecars = work / "ocr_sidecars"
            src.mkdir()
            typeset_in.mkdir(parents=True)
            sidecars.mkdir()
            (src / "002.png").write_bytes(b"png")
            (typeset_in / "002.png").write_bytes(b"png")
            (sidecars / "002_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            trans = work / "translations.json"
            trans.write_text(
                json.dumps({"おはよう": "早上好"}, ensure_ascii=False), encoding="utf-8"
            )
            old = sys.argv
            import bubble_recall_local

            try:
                sys.argv = [
                    "ocr_extract_local.py",
                    "--missing-only",
                    str(src),
                    str(work),
                ]
                with patch.object(
                    bubble_recall_local, "launch_with_mit", return_value=0
                ):
                    rc = ocr_extract_local.main()
            finally:
                sys.argv = old
            self.assertEqual(rc, 0)
            saved = json.loads(trans.read_text(encoding="utf-8"))
            self.assertEqual(saved["おはよう"], "早上好")

    def test_ocr_full_skips_complete_pages_without_mit(self) -> None:
        import tempfile
        import ocr_extract_local

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            work = Path(tmp) / "work"
            typeset_in = work / "typeset_in"
            sidecars = work / "ocr_sidecars"
            src.mkdir()
            typeset_in.mkdir(parents=True)
            sidecars.mkdir()
            (src / "002.png").write_bytes(b"png")
            (typeset_in / "002.png").write_bytes(b"png")
            (sidecars / "002_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            trans = work / "translations.json"
            trans.write_text(
                json.dumps({"おはよう": "早上好"}, ensure_ascii=False), encoding="utf-8"
            )
            old = sys.argv
            import bubble_recall_local

            try:
                sys.argv = ["ocr_extract_local.py", str(src), str(work)]
                with (
                    patch.object(
                        bubble_recall_local, "launch_with_mit", return_value=0
                    ),
                    patch.object(ocr_extract_local, "retry_missing_ocr") as retry,
                    patch.object(local_qwen, "probe", return_value=(True, "up")),
                ):
                    rc = ocr_extract_local.main()
            finally:
                sys.argv = old
            self.assertEqual(rc, 0)
            retry.assert_not_called()
            saved = json.loads(trans.read_text(encoding="utf-8"))
            self.assertEqual(saved["おはよう"], "早上好")

    def test_ocr_skips_aggressive_when_mit_output_has_no_text(self) -> None:
        import tempfile
        import ocr_extract_local

        calls: list[str] = []

        def fake_retry(pages, **kwargs):
            calls.append(str(kwargs.get("label") or "ocr-retry"))
            out = kwargs["work"] / "ocr_retry_out"
            out.mkdir(parents=True, exist_ok=True)
            for img in pages:
                (out / img.name).write_bytes(b"out")

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            work = Path(tmp) / "work"
            src.mkdir()
            (src / "cover.png").write_bytes(b"png")
            (src / "002.png").write_bytes(b"png")
            dump = work / "ocr_dump"
            dump.mkdir(parents=True)
            (dump / "002_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            old = sys.argv
            import bubble_recall_local

            try:
                sys.argv = ["ocr_extract_local.py", str(src), str(work)]
                with (
                    patch.object(
                        bubble_recall_local, "launch_with_mit", return_value=0
                    ),
                    patch.object(
                        ocr_extract_local, "retry_missing_ocr", side_effect=fake_retry
                    ),
                    patch.object(local_qwen, "probe", return_value=(False, "down")),
                    patch.object(
                        ocr_extract_local,
                        "extract_ocr_config",
                        return_value=Path("cfg.json"),
                    ),
                    patch.object(
                        ocr_extract_local.typeset_ocr_local,
                        "mit_paths",
                        return_value=(
                            Path(tmp),
                            Path(tmp) / "python.exe",
                            Path(tmp) / "font.ttc",
                        ),
                    ),
                ):
                    (Path(tmp) / "python.exe").write_bytes(b"py")
                    rc = ocr_extract_local.main()
            finally:
                sys.argv = old
            self.assertEqual(rc, 0)
            self.assertEqual(calls, ["ocr"])
            skip = json.loads(
                (work / "ocr_dump" / "cover_ocr.json").read_text(encoding="utf-8")
            )
            self.assertTrue(skip[0].get("skip"))

    def test_merge_mapping_keeps_filled_and_adds_new(self) -> None:
        import ocr_extract_local

        merged = ocr_extract_local.merge_mapping(
            {"おはよう": "早上好", "空": ""},
            {"おはよう": "", "新しい": "", "空": "已有"},
        )
        self.assertEqual(merged["おはよう"], "早上好")
        self.assertEqual(merged["新しい"], "")
        self.assertEqual(merged["空"], "已有")

    def test_mit_config_follows_original_direction(self) -> None:
        import typeset_ocr_local

        cfg = next(p for p in typeset_ocr_local.CONFIG_CANDIDATES if p.is_file())
        data = json.loads(cfg.read_text(encoding="utf-8"))
        self.assertEqual(data["render"]["direction"], "auto")
        self.assertEqual(
            typeset_ocr_local.CONFIG_CANDIDATES[0].name, "mit_config.json"
        )
        self.assertEqual(data["inpainter"]["inpainter"], "lama_large")

    def test_extract_config_skips_inpaint(self) -> None:
        import ocr_extract_local
        import typeset_ocr_local

        cfg = ocr_extract_local.extract_ocr_config()
        self.assertIsNotNone(cfg)
        data = json.loads(cfg.read_text(encoding="utf-8"))
        self.assertEqual(cfg.name, "mit_ocr_extract.json")
        self.assertEqual(data["inpainter"]["inpainter"], "none")
        self.assertEqual(data["translator"]["translator"], "none")
        typeset = json.loads(
            typeset_ocr_local.CONFIG_CANDIDATES[0].read_text(encoding="utf-8")
        )
        self.assertEqual(typeset["inpainter"]["inpainter"], "lama_large")
        self.assertEqual(typeset["kernel_size"], 3)
        self.assertEqual(typeset["mask_dilation_offset"], 30)

    def test_typeset_restores_art_without_overlay(self) -> None:
        import tempfile
        import typeset_ocr_local
        import bubble_recall_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src = root / "typeset_in"
            src.mkdir()
            (src / "page.png").write_bytes(b"png")
            (root / "translations.json").write_text("{}", encoding="utf-8")
            old = sys.argv
            try:
                sys.argv = ["typeset_ocr_local.py", str(root)]
                with (
                    patch.object(local_qwen, "probe", return_value=(False, "down")),
                    patch.object(
                        typeset_ocr_local,
                        "mit_paths",
                        return_value=(root, root / "python.exe", root / "font.ttc"),
                    ),
                    patch.object(local_qwen, "isolate_image_folder"),
                    patch.object(
                        local_qwen, "run_mit_with_page_progress", return_value=0
                    ) as mit,
                    patch.object(
                        bubble_recall_local, "restore_source_art"
                    ) as restore,
                    patch.object(bubble_recall_local, "overlay_pages") as overlay,
                    patch.object(bubble_recall_local, "launch_with_mit") as launch,
                ):
                    (root / "python.exe").write_bytes(b"py")
                    rc = typeset_ocr_local.main()
            finally:
                sys.argv = old
            self.assertEqual(rc, 0)
            mit.assert_called_once()
            restore.assert_called_once()
            overlay.assert_not_called()
            launch.assert_not_called()

    def test_typeset_changed_only_without_pages_does_not_overlay(self) -> None:
        import tempfile
        import typeset_ocr_local
        import bubble_recall_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src = root / "typeset_in"
            out = root / "out"
            src.mkdir()
            out.mkdir()
            (src / "keep.png").write_bytes(b"k")
            (out / "keep.png").write_bytes(b"done")
            (root / "translations.json").write_text("{}", encoding="utf-8")
            (root / "filled_keys.json").write_text("[]", encoding="utf-8")
            old = sys.argv
            try:
                sys.argv = ["typeset_ocr_local.py", "--changed-only", str(root)]
                with patch.object(
                    bubble_recall_local, "launch_with_mit"
                ) as launch:
                    rc = typeset_ocr_local.main()
            finally:
                sys.argv = old
            self.assertEqual(rc, 0)
            launch.assert_not_called()

    def test_mit_keeps_original_box_and_font_size(self) -> None:
        mit_root = Path(r"D:\grok\tools\manga-image-translator")
        mit_py = mit_root / ".venv" / "Scripts" / "python.exe"
        if not mit_py.is_file():
            self.skipTest("MIT venv missing")
        script = (
            "import numpy as np\n"
            "from manga_translator.utils.textblock import TextBlock\n"
            "from manga_translator.rendering import resize_regions_to_font_size\n"
            "lines = [np.array([[10,10],[40,10],[40,80],[10,80]], dtype=np.int32)]\n"
            "region = TextBlock(lines, texts=['短い対白'], font_size=24)\n"
            "region.translation = '这一段中文对白比原文长很多所以必须换行'\n"
            "region.target_lang = 'CHS'\n"
            "region._direction = 'auto'\n"
            "img = np.zeros((120, 80, 3), dtype=np.uint8)\n"
            "boxes = resize_regions_to_font_size(img, [region], None, 0, -1)\n"
            "np.testing.assert_array_equal(boxes[0], region.min_rect)\n"
            "assert region.font_size <= 24, region.font_size\n"
            "assert region.font_size >= 1, region.font_size\n"
            "whisper = [np.array([[1649,1320],[1691,1320],[1691,1567],[1649,1567]], dtype=np.int32)]\n"
            "w = TextBlock(whisper, texts=['小間宮の隣で'], font_size=42)\n"
            "w.translation = '就在小间宫的旁边'\n"
            "w.target_lang = 'CHS'\n"
            "w._direction = 'v'\n"
            "page = np.zeros((3000, 2100, 3), dtype=np.uint8)\n"
            "wb = resize_regions_to_font_size(page, [w], None, 0, -1)\n"
            "np.testing.assert_array_equal(wb[0], w.min_rect)\n"
            "assert 28 <= w.font_size <= 34, w.font_size\n"
            "same = TextBlock(whisper, texts=['小間宮の隣で'], font_size=42)\n"
            "same.translation = '小间宫旁边'\n"
            "same.target_lang = 'CHS'\n"
            "same._direction = 'v'\n"
            "resize_regions_to_font_size(page, [same], None, 0, -1)\n"
            "assert same.font_size == 42, same.font_size\n"
            "print('ok', region.font_size, w.font_size, same.font_size)\n"
        )
        proc = subprocess.run(
            [str(mit_py), "-c", script],
            cwd=str(mit_root),
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        self.assertIn("ok", proc.stdout)

    def test_mit_none_translator_looks_up_repaired_bang(self) -> None:
        import tempfile
        import asyncio

        mit_root = Path(r"D:\grok\tools\manga-image-translator")
        mit_py = mit_root / ".venv" / "Scripts" / "python.exe"
        if not mit_py.is_file():
            self.skipTest("MIT venv missing")
        with tempfile.TemporaryDirectory() as tmp:
            trans = Path(tmp) / "translations.json"
            trans.write_text(
                json.dumps(
                    {"えっ！": "诶！", "短い": "……那个……"},
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            script = (
                "import asyncio, os, json, sys\n"
                f"os.environ['MIT_TRANSLATIONS_JSON'] = r'{trans}'\n"
                "from manga_translator.translators.none import NoneTranslator\n"
                "tr = NoneTranslator()\n"
                "out = asyncio.run(tr._translate('JPN', 'CHS', ['えっ1!', '短い', 'missing']))\n"
                "print(json.dumps(out, ensure_ascii=False))\n"
            )
            proc = subprocess.run(
                [str(mit_py), "-c", script],
                cwd=str(mit_root),
                capture_output=True,
                text=True,
                encoding="utf-8",
            )
            self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
            self.assertIn("诶！", proc.stdout)
            self.assertIn("…那个…", proc.stdout)
            self.assertNotIn("……那个……", proc.stdout)
            self.assertIn('""', proc.stdout.replace(" ", ""))

    def test_empty_dest_skips_inpaint_none_only_when_flag_set(self) -> None:
        mit_root = Path(r"D:\grok\tools\manga-image-translator")
        mit_py = mit_root / ".venv" / "Scripts" / "python.exe"
        if not mit_py.is_file():
            self.skipTest("MIT venv missing")
        script = (
            "import os, json\n"
            "from manga_translator.translators.none import empty_dest_skips_inpaint\n"
            "os.environ.pop('MIT_SKIP_EMPTY_DEST', None)\n"
            "print(json.dumps([\n"
            "  empty_dest_skips_inpaint('', translator_is_none=False),\n"
            "  empty_dest_skips_inpaint('', translator_is_none=True),\n"
            "  empty_dest_skips_inpaint('我', translator_is_none=True),\n"
            "]))\n"
            "os.environ['MIT_SKIP_EMPTY_DEST'] = '1'\n"
            "print(json.dumps([\n"
            "  empty_dest_skips_inpaint('', translator_is_none=True),\n"
            "  empty_dest_skips_inpaint('我', translator_is_none=True),\n"
            "  empty_dest_skips_inpaint('', translator_is_none=False),\n"
            "]))\n"
        )
        proc = subprocess.run(
            [str(mit_py), "-c", script],
            cwd=str(mit_root),
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        lines = [line for line in proc.stdout.splitlines() if line.strip()]
        self.assertEqual(json.loads(lines[-2]), [True, False, False])
        self.assertEqual(json.loads(lines[-1]), [True, False, True])

    def test_run_mit_folder_sets_skip_empty_dest_except_crop_queue(self) -> None:
        import tempfile
        import typeset_ocr_local

        captured: dict = {}

        def fake_run(cmd, cwd=None, env=None, **kwargs):
            captured["env"] = env
            return 0

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            trans = root / "translations.json"
            trans.write_text("{}", encoding="utf-8")
            src = root / "in"
            dest = root / "out"
            src.mkdir()
            dest.mkdir()
            (src / "001.jpg").write_bytes(b"x")
            cfg = root / "cfg.json"
            cfg.write_text("{}", encoding="utf-8")
            with (
                patch.object(local_qwen, "probe", return_value=(False, "")),
                patch.object(
                    local_qwen, "run_mit_with_page_progress", side_effect=fake_run
                ),
            ):
                rc = typeset_ocr_local.run_mit_folder(
                    root, src, dest, trans=trans, config_file=cfg
                )
            self.assertEqual(rc, 0)
            self.assertEqual(captured["env"].get("MIT_SKIP_EMPTY_DEST"), "1")
            captured.clear()
            with (
                patch.object(local_qwen, "probe", return_value=(False, "")),
                patch.object(
                    local_qwen, "run_mit_with_page_progress", side_effect=fake_run
                ),
            ):
                rc = typeset_ocr_local.run_mit_folder(
                    root,
                    src,
                    dest,
                    trans=trans,
                    config_file=cfg,
                    crop_queue=[""],
                )
            self.assertEqual(rc, 0)
            self.assertEqual(captured["env"].get("MIT_SKIP_EMPTY_DEST"), "0")
            self.assertIn("MIT_CROP_QUEUE", captured["env"])

    def test_changed_pages_selects_filled_keys_and_missing_output(self) -> None:
        import tempfile
        import typeset_ocr_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            src = root / "typeset_in"
            sidecars = root / "ocr_sidecars"
            out = root / "out"
            src.mkdir()
            sidecars.mkdir()
            out.mkdir()
            (src / "keep.png").write_bytes(b"k")
            (src / "change.png").write_bytes(b"c")
            (src / "cover.jpg").write_bytes(b"j")
            (out / "keep.png").write_bytes(b"done")
            (out / "change.png").write_bytes(b"old")
            (sidecars / "keep_ocr.json").write_text(
                '[{"text":"ドスン！"}]', encoding="utf-8"
            )
            (sidecars / "change_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            selected = [
                p.name
                for p in typeset_ocr_local.select_changed_images(
                    root, {"おはよう"}
                )
            ]
            self.assertEqual(selected, ["change.png", "cover.jpg"])

    def test_completed_image_pages_ignores_stale_output_against_baseline(self) -> None:
        import tempfile
        import time

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "in"
            out = Path(tmp) / "out"
            src.mkdir()
            out.mkdir()
            page = src / "a.png"
            page.write_bytes(b"in")
            stale = out / "a.png"
            stale.write_bytes(b"old")
            baseline = local_qwen.snapshot_output_mtimes([page], [out])
            self.assertEqual(
                local_qwen.completed_image_pages(
                    [page], output_dirs=[out], baseline=baseline
                ),
                0,
            )
            time.sleep(0.05)
            stale.write_bytes(b"new")
            self.assertEqual(
                local_qwen.completed_image_pages(
                    [page], output_dirs=[out], baseline=baseline
                ),
                1,
            )

    def test_completed_image_pages_ignores_empty_ocr_json(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "in"
            ocr = Path(tmp) / "ocr"
            src.mkdir()
            ocr.mkdir()
            page = src / "a.png"
            page.write_bytes(b"p")
            (ocr / "a_ocr.json").write_text("[]", encoding="utf-8")
            self.assertEqual(
                local_qwen.completed_image_pages([page], ocr_dirs=[ocr]),
                0,
            )
            (ocr / "a_ocr.json").write_text(
                '[{"text":"おはよう"}]', encoding="utf-8"
            )
            self.assertEqual(
                local_qwen.completed_image_pages([page], ocr_dirs=[ocr]),
                1,
            )

    def test_mit_watch_overwrite_starts_at_zero(self) -> None:
        import contextlib
        import os
        import tempfile
        import time
        from io import StringIO

        with tempfile.TemporaryDirectory() as tmp:
            tmp_path = Path(tmp)
            pngs = [tmp_path / "a.png", tmp_path / "b.png"]
            for png in pngs:
                png.write_bytes(b"x")
            out = tmp_path / "out"
            out.mkdir()
            (out / "a.png").write_bytes(b"old-a")
            (out / "b.png").write_bytes(b"old-b")
            baseline = local_qwen.snapshot_output_mtimes(pngs, [out])
            time.sleep(0.05)
            writer = tmp_path / "writer.py"
            writer.write_text(
                "import pathlib, time\n"
                f"out = pathlib.Path(r'{out}')\n"
                "time.sleep(0.2)\n"
                "(out / 'a.png').write_bytes(b'1')\n"
                "time.sleep(0.25)\n"
                "(out / 'b.png').write_bytes(b'2')\n",
                encoding="utf-8",
            )
            local_qwen.enable_progress(True)
            try:
                buf = StringIO()
                with contextlib.redirect_stdout(buf):
                    rc = local_qwen.run_mit_with_page_progress(
                        [sys.executable, str(writer)],
                        cwd=str(tmp_path),
                        env=os.environ.copy(),
                        phase="typeset",
                        total=2,
                        count=lambda: local_qwen.completed_image_pages(
                            pngs, output_dirs=[out], baseline=baseline
                        ),
                        interval=0.05,
                    )
            finally:
                local_qwen.enable_progress(False)
            self.assertEqual(rc, 0)
            dones = [
                json.loads(line)["done"]
                for line in buf.getvalue().splitlines()
                if line.strip()
            ]
            self.assertTrue(dones, "watcher must emit jsonl")
            self.assertEqual(dones[0], 0, "old output must not count as already typeset")
            self.assertIn(1, dones)
            self.assertEqual(dones[-1], 2)

    def test_sidecar_covers_full_match_but_not_short_fragment(self) -> None:
        import bubble_recall_local

        sidecar = ["ちょー", "うっそ！マジで1!", "あれ？先生いない…", "おわっ⁉"]
        self.assertFalse(
            bubble_recall_local.sidecar_covers("ちょーデカいんだけど♥", sidecar)
        )
        self.assertFalse(
            bubble_recall_local.sidecar_covers(
                "昨日マジヤバかったよねー", ["ヤバかったよねー"]
            )
        )
        self.assertFalse(
            bubble_recall_local.already_typeset(
                "昨日マジヤバかったよねー",
                [1448, 50, 1560, 517],
                ["ヤバかったよねー"],
                [[1452, 235, 1554, 542]],
                [("ヤバかったよねー", [1452, 235, 1554, 542])],
            )
        )
        self.assertFalse(bubble_recall_local.sidecar_covers("センパイ♥", sidecar))
        self.assertTrue(
            bubble_recall_local.sidecar_covers("うっそ！マジで！", sidecar)
        )
        self.assertTrue(bubble_recall_local.sidecar_covers("ちょー", sidecar))
        self.assertTrue(
            bubble_recall_local.sidecar_covers("あれ？先生いない．．．", sidecar)
        )
        self.assertTrue(bubble_recall_local.sidecar_covers("おわっ！？", sidecar))
        self.assertEqual(
            bubble_recall_local.lookup_translation(
                {"センパイ♥": "前辈♥"}, "センパ～イ♥"
            ),
            "前辈♥",
        )
        self.assertTrue(
            bubble_recall_local.sidecar_covers("どうして僕：", ["どうして僕…"])
        )
        self.assertTrue(
            bubble_recall_local.sidecar_covers(
                "さすが＂小間宮三姉妹＂だよなぁ",
                ["さすが「小間宮三姉妹」だよなぁ"],
            )
        )
        self.assertFalse(
            bubble_recall_local.already_typeset(
                "センパイ♥",
                [10, 10, 40, 40],
                ["どうして僕…"],
                [[200, 200, 280, 400]],
            )
        )
        self.assertTrue(
            bubble_recall_local.already_typeset(
                "どうして僕：",
                [145, 2548, 338, 3005],
                ["どうして僕…"],
                [[204, 2577, 282, 2975]],
            )
        )
        self.assertTrue(
            bubble_recall_local.already_typeset(
                "別の読み",
                [100, 100, 400, 500],
                ["短い対白"],
                [[160, 180, 220, 420]],
            )
        )
        self.assertTrue(
            bubble_recall_local.already_typeset("！？", [10, 10, 40, 40], [], [])
        )
        self.assertFalse(
            bubble_recall_local.already_typeset("ね♥", [10, 10, 40, 40], [], [])
        )
        self.assertTrue(
            bubble_recall_local.already_typeset("しっ！！", [236, 1091, 631, 1611], [], [])
        )
        banana = "どうですか先生？この日のためにネットとバナナで勉強してたんです♡"
        garbled = "どうですか先生？この日のだめにネタトと"
        self.assertFalse(
            bubble_recall_local.already_typeset(
                banana,
                [400, 40, 560, 280],
                [garbled],
                [[410, 50, 540, 180]],
                [(garbled, [410, 50, 540, 180])],
            ),
            "garbled MIT fragment must not block overlay of the full balloon",
        )
        moan = "あの…先生もう大丈夫ですから…強めに動いても……♡"
        hallucinated = moan + "１００万円♡"
        self.assertTrue(
            bubble_recall_local.sidecar_covers(hallucinated, [moan]),
            "manga-ocr 100万 tail is not missed dialogue",
        )
        self.assertTrue(
            bubble_recall_local.skip_as_art(hallucinated, overlay=True)
            or bubble_recall_local.already_typeset(
                hallucinated,
                [386, 499, 507, 722],
                [moan],
                [[428, 516, 495, 704]],
                [(moan, [428, 516, 495, 704])],
            ),
            "do not overlay 100万 onto a balloon MIT already typeset",
        )
        self.assertTrue(
            local_qwen.suspicious_ocr(
                "３月２７日に東京を開いて学校中学校に関すると、子どもが学ませて"
            )
        )
        self.assertTrue(local_qwen.suspicious_ocr("1994年1 1979年"))
        self.assertTrue(local_qwen.suspicious_ocr("１９９４年１１９７９年"))
        # MIT only captured the 超 ruby inside the balloon — still a miss.
        self.assertFalse(
            bubble_recall_local.already_typeset(
                "ちょーデカいんだけど♥",
                [79, 1493, 465, 2123],
                ["ちょー", "おち○ちん♥"],
                [[278, 1545, 336, 1738], [265, 906, 306, 1147]],
                [("ちょー", [278, 1545, 336, 1738]), ("おち○ちん♥", [265, 906, 306, 1147])],
            )
        )
        # MIT kept セ…ンパイ / バカみたい…♡ (heart mismatch) and dropped おっぱい吸いすぎ.
        self.assertFalse(
            bubble_recall_local.already_typeset(
                "セ：ンパイっ♥おっぱい吸いすぎっ♥バカみたい．．．♥",
                [341, 1536, 617, 2084],
                ["セ…ンパイっ♥", "バカみたい…♡"],
                [[517, 1575, 569, 1904], [399, 1580, 444, 1897]],
                [
                    ("セ…ンパイっ♥", [517, 1575, 569, 1904]),
                    ("バカみたい…♡", [399, 1580, 444, 1897]),
                ],
            )
        )
        # MIT already nested the same balloon (quotes stripped) — do not overlay again.
        self.assertTrue(
            bubble_recall_local.already_typeset(
                "〝お礼〟してあげる",
                [286, 1980, 583, 2449],
                ["お礼してあげる"],
                [[369, 2051, 506, 2345]],
                [("お礼してあげる", [369, 2051, 506, 2345])],
            )
        )

    def test_leftover_reason_flags_missed_dialogue_not_sfx(self) -> None:
        import bubble_recall_local

        mapping = {
            "ね♥": "呐♥",
            "ちょーデカいんだけど♥": "超大的啦♥",
            "〝お礼〟してあげる": "我会谢你的",
        }
        ruby = ("ちょー", [278, 1545, 336, 1738])
        self.assertEqual(
            bubble_recall_local.leftover_reason(
                "ね♥",
                [1716, 2848, 1972, 3202],
                ["あとで", "え？"],
                [[10, 10, 40, 40]],
                [("あとで", [10, 10, 40, 40])],
                [],
                mapping,
            ),
            "not_painted",
        )
        self.assertIsNone(
            bubble_recall_local.leftover_reason(
                "ね♥",
                [1716, 2848, 1972, 3202],
                ["あとで", "え？"],
                [[10, 10, 40, 40]],
                [("あとで", [10, 10, 40, 40])],
                [[1716, 2848, 1972, 3202]],
                mapping,
            )
        )
        self.assertIsNone(
            bubble_recall_local.leftover_reason(
                "しっ！！", [236, 1091, 631, 1611], [], [], [], [], mapping
            )
        )
        self.assertEqual(
            bubble_recall_local.leftover_reason(
                "ちょーデカいんだけど♥",
                [79, 1493, 465, 2123],
                ["ちょー"],
                [[278, 1545, 336, 1738]],
                [ruby],
                [],
                mapping,
            ),
            "not_painted",
        )
        self.assertIsNone(
            bubble_recall_local.leftover_reason(
                "〝お礼〟してあげる",
                [286, 1980, 583, 2449],
                ["お礼してあげる"],
                [[369, 2051, 506, 2345]],
                [("お礼してあげる", [369, 2051, 506, 2345])],
                [],
                mapping,
            )
        )
        self.assertEqual(
            bubble_recall_local.leftover_reason(
                "センパイ♥",
                [10, 10, 80, 120],
                [],
                [],
                [],
                [],
                {},
            ),
            "untranslated",
        )
        self.assertTrue(bubble_recall_local.skip_as_art("しっ！！"))
        self.assertFalse(bubble_recall_local.skip_as_art("えっ！", overlay=False))
        self.assertFalse(bubble_recall_local.skip_as_art("ね♥"))
        self.assertTrue(bubble_recall_local.skip_as_art("で、で、"))
        self.assertTrue(bubble_recall_local.skip_as_art("そして、"))
        self.assertTrue(bubble_recall_local.skip_as_art("にぎにぎ"))
        self.assertTrue(bubble_recall_local.skip_as_art("で、で、", overlay=False))
        self.assertFalse(bubble_recall_local.skip_as_art("だ、だめだって．．．っ"))
        self.assertFalse(bubble_recall_local.skip_as_art("あとで"))
        self.assertFalse(bubble_recall_local.skip_as_art("いい"))
        self.assertTrue(
            bubble_recall_local.skip_as_art(
                "通学の後、居候まる家にには美人三成旅十彼が遠のママが住んでいた、すぐに長居時ハーレム連載、単行風化！！！定価は本来１０００１８年１２月１８日午後"
            ),
            "essay-length OCR dumps are art/garbage, not leftover dialogue",
        )

    def test_lookup_translation_strips_trailing_sfx_not_other_lines(self) -> None:
        import bubble_recall_local

        mapping = {
            "覚悟を決めろ": "做好觉悟吧",
            "キスもこんなに気持ちいいのかよ": "接吻竟然这么舒服",
            "さくら先輩のさっきの言葉…どういう意味なんだ？": "樱学姐刚才说的话到底是什么意思？",
            "さくら先輩の夢を支えるって決めて教師になってこの村に来たんだ！": "我决定要支持樱学姐的梦想所以才当老师",
            "お出口は右側です": "出口在右侧",
        }
        self.assertEqual(
            bubble_recall_local.lookup_translation(mapping, "覚悟を決めろーー！！"),
            "做好觉悟吧",
        )
        self.assertEqual(
            bubble_recall_local.lookup_translation(
                mapping, "キスもこんなに気持ちいいのかよ！"
            ),
            "接吻竟然这么舒服",
        )
        self.assertEqual(
            bubble_recall_local.lookup_translation(mapping, "―――お出口は右側です"),
            "出口在右侧",
        )
        self.assertEqual(
            bubble_recall_local.lookup_translation(
                mapping, "さくら先輩の夢を支えるって決めて教師になってこの村に来たんだ！！"
            ),
            "我决定要支持樱学姐的梦想所以才当老师",
        )
        self.assertNotEqual(
            bubble_recall_local.lookup_translation(
                mapping, "さくら先輩の夢を支えるって決めて教師になってこの村に来たんだ！！"
            ),
            "樱学姐刚才说的话到底是什么意思？",
        )
        self.assertFalse(bubble_recall_local.skip_as_art("ダメ♥"))
        self.assertFalse(bubble_recall_local.skip_as_art("昨日マジ"))
        self.assertFalse(bubble_recall_local.skip_as_art("ヤバかったよねー"))
        self.assertTrue(
            bubble_recall_local.already_typeset(
                "で、で、", [76, 246, 473, 889], [], []
            )
        )
        self.assertIsNone(
            bubble_recall_local.leftover_reason(
                "そして、",
                [1853, 153, 2243, 756],
                [],
                [],
                [],
                [],
                {"そして、": "然后，"},
            )
        )

    def test_dialogue_boxes_keeps_unboxed_narration(self) -> None:
        import bubble_recall_local

        hits = [
            {"cls_id": 1, "xyxy": [10, 10, 40, 80], "score": 0.9},
            {"cls_id": 2, "xyxy": [100, 20, 160, 90], "score": 0.8},
            {"cls_id": 0, "xyxy": [200, 20, 280, 100], "score": 0.7},
        ]
        boxes = bubble_recall_local.dialogue_boxes(hits)
        cls_ids = {row["cls_id"] for row in boxes}
        self.assertIn(1, cls_ids)
        self.assertIn(2, cls_ids)
        self.assertTrue(any(row.get("from_text_free") for row in boxes))

    def test_repair_collapses_double_ellipsis(self) -> None:
        self.assertEqual(
            local_qwen.repair_ocr_bang("……那个……"),
            "…那个…",
        )
        self.assertEqual(
            local_qwen.repair_ocr_bang("为什么我……"),
            "为什么我…",
        )

    def _assert_cjk_wrap_keeps(self, text: str, max_chars: int, *words: str) -> list[str]:
        import cjk_wrap

        cols = cjk_wrap.wrap_cjk_columns(text, max_chars)
        joined = "".join(cols)
        self.assertEqual(joined, "".join(text.split()), cols)
        self.assertTrue(cols, text)
        for col in cols[:-1]:
            self.assertGreater(len(col), 1, cols)
        if len("".join(text.split())) > 1:
            self.assertFalse(any(len(col) == 1 for col in cols), cols)
        for word in words:
            self.assertIn(word, joined)
            for i in range(len(cols) - 1):
                for k in range(1, len(word)):
                    self.assertFalse(
                        cols[i].endswith(word[:k]) and cols[i + 1].startswith(word[k:]),
                        f"{word} split in {cols}",
                    )
        return cols

    def test_cjk_wrap_page005_does_not_split_words_or_leave_one_char(self) -> None:
        for width in (4, 5, 6, 7):
            self._assert_cjk_wrap_keeps("那修长而丰满的大腿", width, "大腿")
            self._assert_cjk_wrap_keeps(
                "丰满到让衬衫都撑得鼓鼓的巨乳", width, "衬衫", "巨乳"
            )
            self._assert_cjk_wrap_keeps("水灵灵的大眼睛", width, "眼睛")
            self._assert_cjk_wrap_keeps(
                "班上最可爱的那位小间宫怜奈", width, "间宫", "小间宫"
            )

    def test_split_source_by_columns_matches_page005_jp_layout(self) -> None:
        import cjk_wrap

        name = cjk_wrap.split_source_by_columns(
            "クラスで一番かわいい小間宮怜奈の",
            [[1321, 330, 1383, 546], [1254, 333, 1318, 649], [1182, 334, 1238, 646]],
        )
        self.assertEqual(name, "クラスで\n一番かわいい\n小間宮怜奈の")
        shirt = cjk_wrap.split_source_by_columns(
            "ブラウスがパツパツになるほどの巨乳",
            [[838, 1408, 891, 1643], [783, 1408, 837, 1643], [727, 1410, 776, 1723]],
        )
        self.assertEqual(shirt, "ブラウスが\nパツパツに\nなるほどの巨乳")
        leg = cjk_wrap.split_source_by_columns(
            "美脚でむちむちな太もも",
            [[396, 2753, 454, 2906], [333, 2754, 390, 3002], [275, 2754, 333, 2909]],
        )
        self.assertEqual(leg, "美脚で\nむちむちな\n太もも")
        self.assertEqual(
            cjk_wrap.split_source_by_columns("ていうか！", [[0, 0, 40, 80]]),
            "ていうか！",
        )

    def test_sidecar_line_sources_keep_merged_lookup_keys(self) -> None:
        import tempfile
        import cjk_wrap

        sample = """
-- 4 --
text:  クラスで一番かわいい小間宮怜奈の
trans:
coords: [1321, 330, 1383, 330, 1384, 546, 1322, 546]
coords: [1254, 333, 1318, 333, 1316, 649, 1251, 649]
coords: [1182, 334, 1238, 334, 1238, 646, 1182, 646]
"""
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            folder = root / "ocr_sidecars"
            folder.mkdir()
            (folder / "005_translations.txt").write_text(sample, encoding="utf-8")
            sources = cjk_wrap.load_line_sources(root)
            self.assertEqual(
                sources["クラスで一番かわいい小間宮怜奈の"],
                "クラスで\n一番かわいい\n小間宮怜奈の",
            )

    def test_fill_sends_column_newlines_and_stores_merged_key(self) -> None:
        import tempfile
        import cjk_wrap
        import fill_ocr_local

        calls: list[list[str]] = []

        def fake(batch: list[str]) -> list[str]:
            calls.append(list(batch))
            return ["班上最可爱的\n那位小间宫怜奈"] * len(batch)

        sample = """
-- 4 --
text:  クラスで一番かわいい小間宮怜奈の
trans:
coords: [1321, 330, 1383, 330, 1384, 546, 1322, 546]
coords: [1254, 333, 1318, 333, 1316, 649, 1251, 649]
coords: [1182, 334, 1238, 334, 1238, 646, 1182, 646]
"""
        with tempfile.TemporaryDirectory() as tmp:
            dest = Path(tmp) / "translations.json"
            sidecar = Path(tmp) / "ocr_sidecars"
            sidecar.mkdir()
            (sidecar / "005_translations.txt").write_text(sample, encoding="utf-8")
            data = {"クラスで一番かわいい小間宮怜奈の": "班上最可爱的那位小间宫怜奈"}
            fill_ocr_local.translate_mapping(
                data, dest, engine="mt", translate_fn=fake
            )
            self.assertEqual(calls[0], ["クラスで\n一番かわいい\n小間宮怜奈の"])
            saved = json.loads(dest.read_text(encoding="utf-8"))
            self.assertEqual(
                saved["クラスで一番かわいい小間宮怜奈の"],
                "班上最可爱的\n那位小间宫怜奈",
            )
            self.assertNotIn("\n", "".join(saved.keys()))

    def test_repair_ocr_bang_keeps_newlines(self) -> None:
        self.assertEqual(
            local_qwen.repair_ocr_bang("クラスで\n一番かわいい"),
            "クラスで\n一番かわいい",
        )
        self.assertEqual(
            local_qwen.parse_galtransl_reply("班上最可爱的\n那位小间宫怜奈", 1),
            ["班上最可爱的\n那位小间宫怜奈"],
        )

    def test_cjk_wrap_fits_short_text_in_one_column(self) -> None:
        import cjk_wrap

        self.assertEqual(cjk_wrap.wrap_cjk_columns("话说！", 6), ["话说！"])
        self.assertEqual(cjk_wrap.wrap_cjk_columns("因为坐到了邻座", 8), ["因为坐到了邻座"])

    def test_mit_wrap_cjk_columns_matches_hanhua(self) -> None:
        import cjk_wrap

        mit_root = Path(r"D:\grok\tools\manga-image-translator")
        mit_py = mit_root / ".venv" / "Scripts" / "python.exe"
        if not mit_py.is_file():
            self.skipTest("MIT venv missing")
        samples = [
            ("那修长而丰满的大腿", 4),
            ("丰满到让衬衫都撑得鼓鼓的巨乳", 5),
            ("水灵灵的大眼睛", 6),
            ("班上最可爱的那位小间宫怜奈", 5),
            ("话说！", 6),
        ]
        script = (
            "from manga_translator.rendering.text_render import wrap_cjk_columns\n"
            "import json,sys\n"
            "samples=json.loads(sys.argv[1])\n"
            "print(json.dumps([wrap_cjk_columns(t,n) for t,n in samples], ensure_ascii=False))\n"
        )
        proc = subprocess.run(
            [str(mit_py), "-c", script, json.dumps(samples)],
            cwd=str(mit_root),
            capture_output=True,
            text=True,
            encoding="utf-8",
        )
        self.assertEqual(proc.returncode, 0, proc.stderr + proc.stdout)
        mit_cols = json.loads(proc.stdout.strip().splitlines()[-1])
        expected = [cjk_wrap.wrap_cjk_columns(text, width) for text, width in samples]
        self.assertEqual(mit_cols, expected)

    def test_overlay_font_starts_from_column_width(self) -> None:
        import bubble_recall_local

        long = "她可是学校的三大美女之一啊"
        old_tiny = max(14, 266 // max(1, len(long)))
        size = bubble_recall_local.choose_vertical_overlay_size(180, 266, long)
        self.assertGreaterEqual(size, 36)
        self.assertGreater(size, old_tiny)
        whisper = bubble_recall_local.choose_vertical_overlay_size(80, 247, "就在小间宫的旁边")
        self.assertGreaterEqual(whisper, 28)

    def _assert_vertical_columns_have_gutter(
        self, inner_w: int, inner_h: int, body: str, *, min_em: float = 1.3
    ) -> list[str]:
        import bubble_recall_local

        size, columns = bubble_recall_local.choose_vertical_overlay_layout(
            inner_w, inner_h, body
        )
        self.assertTrue(columns, body)
        joined = "".join(columns)
        self.assertEqual(joined, "".join(str(body).split()), columns)
        # CJK glyphs occupy ~1em; columns packed at 1.12em collide (边边 / 上上).
        self.assertGreaterEqual(
            inner_w,
            len(columns) * int(size * min_em),
            f"cols={columns!r} size={size} inner={inner_w}x{inner_h}",
        )
        self.assertGreaterEqual(size, 16, columns)
        return columns

    def test_vertical_overlay_does_not_pack_flush_newline_columns(self) -> None:
        # Any vertical balloon: translation newlines are hints, not a license
        # to sit CJK columns on top of each other. Dimensions are typical
        # manga detector boxes, text is unrelated to a specific title.
        generic = self._assert_vertical_columns_have_gutter(
            96, 202, "今天先\n把这件事说完\n然后再走吗？", min_em=1.5
        )
        self.assertLessEqual(len(generic), 3)
        # Page 0092 rotor balloon: 3 JP columns, GalTransl kept 3 newlines.
        rotor = self._assert_vertical_columns_have_gutter(
            96, 202, "今天是\n一边放着跳蛋\n一边上课吗？", min_em=1.5
        )
        self.assertLessEqual(len(rotor), 3)
        self.assertIn("一边", "".join(rotor))
        # Page 0092 confession: 4 JP columns in a 129px balloon.
        confession = self._assert_vertical_columns_have_gutter(
            115, 172, "好的…\n为了告白\n我鼓足了\n干劲呢…♡", min_em=1.5
        )
        self.assertLessEqual(len(confession), 3)
        # Page 0089 small balloon: one JP column, wrap must not flush 姑且 against 确认.
        confirm = self._assert_vertical_columns_have_gutter(44, 119, "姑且确认一下…")
        self.assertEqual(len(confirm), 1, confirm)

    def test_extra_newline_columns_merge_phrases_not_reflow(self) -> None:
        import bubble_recall_local

        # 3 GalTransl newlines in a 2-column balloon must keep 对不起 / 还溅到了
        # as phrases, not wrap into 对不起还.
        _size, columns = bubble_recall_local.choose_vertical_overlay_layout(
            91, 150, "对不起啊\n还溅到了\n你脸上"
        )
        self.assertEqual(len(columns), 2, columns)
        self.assertTrue(
            any(col.startswith("对不起") and "还" not in col for col in columns),
            columns,
        )
        self.assertTrue(any("还溅到了" in col for col in columns), columns)

    def test_overlay_render_leaves_gap_between_vertical_columns(self) -> None:
        from PIL import Image
        import bubble_recall_local

        img = Image.new("RGB", (200, 260), (250, 250, 250))
        box = [20, 20, 126, 232]
        bubble_recall_local.render_translation(
            img, box, "今天是\n一边放着跳蛋\n一边上课吗？", img
        )

        def clusters_at(y: int) -> list[tuple[int, int]]:
            found: list[tuple[int, int]] = []
            start = None
            for x in range(box[0], box[2]):
                dark = img.getpixel((x, y))[0] < 80
                if dark and start is None:
                    start = x
                elif not dark and start is not None:
                    found.append((start, x))
                    start = None
            if start is not None:
                found.append((start, box[2]))
            merged: list[tuple[int, int]] = []
            for a, b in found:
                if merged and a - merged[-1][1] <= 3:
                    merged[-1] = (merged[-1][0], b)
                else:
                    merged.append((a, b))
            return [item for item in merged if item[1] - item[0] >= 6]

        mid_y = (box[1] + box[3]) // 2
        peaks = clusters_at(mid_y)
        if len(peaks) < 2:
            peaks = max(
                (clusters_at(y) for y in range(box[1] + 20, box[3] - 20, 8)),
                key=len,
            )
        self.assertGreaterEqual(len(peaks), 2, f"expected separate columns, got {peaks}")
        for (_a1, a2), (b1, _b2) in zip(peaks, peaks[1:]):
            self.assertGreaterEqual(
                b1 - a2,
                12,
                f"vertical columns collide at y: {peaks}",
            )

    def test_two_column_speech_balloon_keeps_column_gutter(self) -> None:
        from PIL import Image
        import bubble_recall_local

        # Typical 101x160 balloon, 12 CJK in two columns. Dense hanzi need more
        # than 1.12–1.38em or 溅 sits on 对.
        img = Image.new("RGB", (200, 220), (250, 250, 250))
        box = [20, 20, 121, 180]
        bubble_recall_local.render_translation(
            img, box, "对…对不起还\n溅到了你脸上", img
        )

        def clusters_at(y: int) -> list[tuple[int, int]]:
            found: list[tuple[int, int]] = []
            start = None
            for x in range(box[0], box[2]):
                dark = img.getpixel((x, y))[0] < 80
                if dark and start is None:
                    start = x
                elif not dark and start is not None:
                    found.append((start, x))
                    start = None
            if start is not None:
                found.append((start, box[2]))
            merged: list[tuple[int, int]] = []
            for a, b in found:
                if merged and a - merged[-1][1] <= 3:
                    merged[-1] = (merged[-1][0], b)
                else:
                    merged.append((a, b))
            return [item for item in merged if item[1] - item[0] >= 6]

        gaps = []
        for y in range(box[1] + 16, box[3] - 16, 6):
            peaks = clusters_at(y)
            if len(peaks) < 2:
                continue
            peaks = sorted(peaks, key=lambda item: item[1] - item[0], reverse=True)[:2]
            peaks.sort()
            gaps.append(peaks[1][0] - peaks[0][1])
        self.assertTrue(gaps, "two-column balloon must draw two ink columns")
        self.assertGreaterEqual(min(gaps), 12, f"column gutters too tight: {gaps}")

    def test_needs_overlay_restyles_flush_newline_columns(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 400), (80, 80, 80))
        draw = ImageDraw.Draw(src)
        draw.ellipse([80, 40, 320, 360], fill=(168, 168, 168))
        draw.rectangle([230, 90, 242, 280], fill=(20, 20, 20))
        draw.rectangle([200, 90, 212, 280], fill=(20, 20, 20))
        draw.rectangle([170, 90, 182, 240], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.ellipse([80, 40, 320, 360], fill=(168, 168, 168))
        od.rectangle([226, 90, 250, 160], fill=(20, 20, 20))
        od.rectangle([196, 90, 220, 250], fill=(20, 20, 20))
        od.rectangle([166, 90, 190, 220], fill=(20, 20, 20))
        box = [110, 70, 216, 282]
        dest = "今天是\n一边放着跳蛋\n一边上课吗？"
        self.assertFalse(
            bubble_recall_local.needs_overlay(
                "今日はローターを入れながら授業をしていたの？",
                box,
                out,
                src,
                dest=dest,
            ),
            "MIT already replaced the Japanese; overlay must not restore and restyle it",
        )

    def test_overlay_heart_is_filled_not_msyh_tofu(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("L", (120, 120), 255)
        draw = ImageDraw.Draw(img)
        font = bubble_recall_local._font(80, "♥")
        draw.text((20, 10), "♥", font=font, fill=0)
        bbox = img.getbbox()
        self.assertIsNotNone(bbox)
        x1, y1, x2, y2 = bbox
        cx, cy = (x1 + x2) // 2, (y1 + y2) // 2
        # msyh .notdef is a hollow rectangle; a real heart is filled at the center.
        self.assertEqual(img.getpixel((cx, cy)), 0)

    def test_overlay_horizontal_heart_is_filled_not_msyh_tofu(self) -> None:
        from PIL import Image
        import bubble_recall_local

        img = Image.new("RGB", (240, 80), (128, 128, 128))
        bubble_recall_local.render_translation(img, [10, 10, 230, 70], "♥")
        gray = img.convert("L")
        dark = [
            (x, y)
            for y in range(10, 70)
            for x in range(10, 230)
            if gray.getpixel((x, y)) < 40
        ]
        self.assertTrue(dark, "horizontal overlay must draw the heart")
        xs = [p[0] for p in dark]
        ys = [p[1] for p in dark]
        cx, cy = (min(xs) + max(xs)) // 2, (min(ys) + max(ys)) // 2
        # msyh tofu is a hollow rectangle; a real heart is filled.
        self.assertLess(gray.getpixel((cx, cy)), 40)

    def test_overlay_short_heart_stays_inside_balloon(self) -> None:
        from PIL import Image
        import bubble_recall_local

        img = Image.new("RGB", (400, 420), (128, 128, 128))
        box = [50, 20, 280, 360]
        bubble_recall_local.render_translation(img, box, "呐♥")
        def dark(px: tuple[int, int, int]) -> bool:
            return px[0] < 80 and px[1] < 80 and px[2] < 80

        below = [img.getpixel((x, y)) for y in range(361, 400) for x in range(50, 280)]
        self.assertFalse(any(dark(px) for px in below), "heart must not spill below the balloon")
        outside = [img.getpixel((x, y)) for y in range(20, 360) for x in range(281, 320)]
        self.assertFalse(any(dark(px) for px in outside), "heart must not spill past the balloon")

    def test_overlay_short_text_does_not_whiteout_whole_balloon(self) -> None:
        from PIL import Image
        import bubble_recall_local

        img = Image.new("RGB", (400, 420), (128, 128, 128))
        box = [50, 20, 280, 360]
        bubble_recall_local.render_translation(img, box, "这个…")
        def white(px: tuple[int, int, int]) -> bool:
            return px[0] > 240 and px[1] > 240 and px[2] > 240

        for pt in ((52, 22), (277, 22), (52, 357), (277, 357)):
            self.assertFalse(
                white(img.getpixel(pt)),
                f"balloon corner {pt} must not be a giant white card",
            )
        x1, y1, x2, y2 = box
        whites = sum(
            1
            for y in range(y1, y2)
            for x in range(x1, x2)
            if white(img.getpixel((x, y)))
        )
        self.assertLess(
            whites / max(1, (x2 - x1) * (y2 - y1)),
            0.45,
            "white fill must hug the text, not the detector balloon",
        )

    def test_overlay_fill_hugs_remaining_ink(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (400, 420), (128, 128, 128))
        box = [40, 40, 300, 380]
        draw = ImageDraw.Draw(img)
        draw.rectangle([150, 80, 185, 220], fill=(20, 20, 20))
        tight = bubble_recall_local.tighten_overlay_box(img, box)
        self.assertGreater(tight[0], box[0] + 40)
        self.assertLess(tight[2], box[2] - 40)
        self.assertGreater(tight[1], box[1] + 10)
        self.assertLess(tight[3], box[3] - 40)

    def test_overlay_covers_light_glyphs_on_dark_background(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (500, 180), (180, 40, 40))
        draw = ImageDraw.Draw(img)
        draw.ellipse([420, 70, 458, 108], fill=(250, 250, 180))
        box = [40, 40, 470, 150]
        ink = bubble_recall_local.overlay_ink_bbox(img, box)
        self.assertIsNotNone(ink)
        self.assertGreater(ink[0], 350)
        self.assertLess(ink[2], 470)
        bubble_recall_local.render_translation(img, box, "振兴乡村♥")

        def white(px: tuple[int, int, int]) -> bool:
            return px[0] > 240 and px[1] > 240 and px[2] > 240

        leftover = img.getpixel((439, 89))
        self.assertNotEqual(
            leftover,
            (250, 250, 180),
            "white-on-red leftover heart must be covered, not left on the page",
        )
        self.assertFalse(
            white(img.getpixel((42, 42))),
            "dark title background must not become a giant white card",
        )

    def test_overlay_dark_title_does_not_paint_a_flat_plate(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (520, 220), (180, 40, 40))
        draw = ImageDraw.Draw(img)
        draw.ellipse([390, 44, 478, 132], fill=(220, 140, 120))
        draw.rectangle([50, 80, 210, 112], fill=(250, 250, 250))
        src = img.copy()
        box = [40, 40, 480, 180]
        self.assertEqual(src.getpixel((460, 60)), (220, 140, 120))
        draw.rectangle([220, 86, 300, 108], fill=(250, 250, 250))
        bubble_recall_local.render_translation(
            img,
            box,
            "3P\n把精子播在我们身上\n让我们怀孕吧♥〈第1话〉",
            src,
        )
        skin = img.getpixel((460, 60))
        self.assertGreater(
            skin[1],
            100,
            f"red TOC overlay must not flatten the illustration under a plate: {skin}",
        )
        self.assertNotEqual(
            img.getpixel((80, 96)),
            (250, 250, 250),
            "original white Japanese on the red title must be replaced",
        )
        self.assertNotEqual(
            img.getpixel((250, 97)),
            (250, 250, 250),
            "MIT leftover white glyphs outside the original ink box must be restored",
        )

    def test_low_score_skip_keeps_real_dialogue(self) -> None:
        import bubble_recall_local

        self.assertTrue(
            bubble_recall_local.skip_low_score_overlay(0.489, "この．．．")
        )
        self.assertFalse(
            bubble_recall_local.skip_low_score_overlay(0.489, "痛くていう？")
        )
        self.assertFalse(
            bubble_recall_local.skip_low_score_overlay(0.489, "やっぱりわかった")
        )
        self.assertFalse(
            bubble_recall_local.skip_low_score_overlay(0.8, "この．．．")
        )
        self.assertTrue(bubble_recall_local.skip_as_art("１２．１２"))
        self.assertTrue(bubble_recall_local.skip_as_art("イキ!?"))
        self.assertTrue(bubble_recall_local.skip_as_art("たらかん"))
        self.assertTrue(bubble_recall_local.skip_as_art("contents"))
        self.assertTrue(bubble_recall_local.skip_as_art("とりあえず、"))
        self.assertTrue(bubble_recall_local.skip_as_art("時々特に特に"))
        self.assertTrue(bubble_recall_local.skip_as_art("大津２群年"))
        self.assertFalse(bubble_recall_local.skip_as_art("くるみ君"))
        self.assertFalse(bubble_recall_local.skip_as_art("あとがき"))
        self.assertFalse(
            bubble_recall_local.skip_as_art("3P私たちに精子を蒔いて孕ませて♥〈第１話〉")
        )
        self.assertTrue(
            bubble_recall_local.skip_as_art("お任務", [14, 800, 333, 1432])
        )

    def test_overlay_eight_char_text_does_not_whiteout_balloon(self) -> None:
        from PIL import Image
        import bubble_recall_local

        img = Image.new("RGB", (400, 420), (128, 128, 128))
        box = [50, 20, 160, 360]
        bubble_recall_local.render_translation(img, box, "我好喜欢这样…")

        def white(px: tuple[int, int, int]) -> bool:
            return px[0] > 240 and px[1] > 240 and px[2] > 240

        for pt in ((52, 22), (158, 22), (52, 357), (158, 357)):
            self.assertFalse(
                white(img.getpixel(pt)),
                f"balloon corner {pt} must not be a giant white card",
            )
        x1, y1, x2, y2 = box
        whites = sum(
            1
            for y in range(y1, y2)
            for x in range(x1, x2)
            if white(img.getpixel((x, y)))
        )
        self.assertLess(
            whites / max(1, (x2 - x1) * (y2 - y1)),
            0.55,
            "8-char overlay must hug the text, not the detector balloon",
        )

    def test_overlay_screentone_balloon_does_not_paint_white_card(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (400, 400), (40, 40, 40))
        draw = ImageDraw.Draw(img)
        draw.ellipse([80, 40, 320, 360], fill=(168, 168, 168))
        draw.rectangle([188, 90, 200, 290], fill=(20, 20, 20))
        box = [110, 70, 290, 330]
        bubble_recall_local.render_translation(img, box, "这…这样啊…", img)

        def white(px: tuple[int, int, int]) -> bool:
            return px[0] > 240 and px[1] > 240 and px[2] > 240

        whites = sum(
            1
            for y in range(box[1], box[3])
            for x in range(box[0], box[2])
            if white(img.getpixel((x, y)))
        )
        self.assertLess(
            whites / max(1, (box[2] - box[0]) * (box[3] - box[1])),
            0.02,
            "gray screentone balloons must not get a white typeset plate",
        )
        self.assertFalse(white(img.getpixel((200, 200))))
        self.assertGreater(img.getpixel((120, 80))[0], 140)
        darks = sum(
            1
            for y in range(box[1], box[3])
            for x in range(box[0], box[2])
            if img.getpixel((x, y))[0] < 80
        )
        self.assertGreater(darks, 80, "Chinese glyphs must still be drawn")

    def test_overlay_white_balloon_still_covers_leftover_ink(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (400, 400), (40, 40, 40))
        draw = ImageDraw.Draw(img)
        draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        draw.rectangle([188, 90, 200, 290], fill=(20, 20, 20))
        box = [110, 70, 290, 330]
        bubble_recall_local.render_translation(img, box, "我羞耻的地方…全都想拍下来…♡", img)
        self.assertGreater(
            img.getpixel((160, 120))[0],
            200,
            "white balloon interior stays white",
        )
        bar = [img.getpixel((194, y))[0] for y in range(100, 280)]
        self.assertTrue(
            any(v > 200 for v in bar),
            "leftover Japanese on a white balloon may be covered with white",
        )

    def test_overlay_grows_thin_column_inside_white_balloon(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (400, 400), (80, 80, 80))
        draw = ImageDraw.Draw(img)
        draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        draw.rectangle([185, 90, 215, 300], fill=(20, 20, 20))
        box = [185, 90, 215, 300]
        grown = bubble_recall_local.grow_overlay_box(img, box)
        self.assertGreater(grown[2] - grown[0], (box[2] - box[0]) * 2)
        self.assertLess(grown[0], box[0])
        self.assertGreater(grown[2], box[2])
        self.assertGreater(grown[0], 80)
        self.assertLess(grown[2], 320)
        squat = [150, 140, 250, 260]
        self.assertEqual(
            bubble_recall_local.grow_overlay_box(img, squat),
            squat,
            "short round balloons must not expand into skin/screentone",
        )
        self.assertFalse(
            bubble_recall_local.already_typeset(
                "あ…♡慣れてきたかも…♡",
                box,
                ["あ…♡慣れてきたかも…♡"],
                [box],
                [("あ…♡慣れてきたかも…♡", box)],
                img,
            ),
            "tiny MIT glyph columns inside a large balloon must be re-overlaid",
        )

    def test_needs_overlay_restyles_gray_plate_on_art(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (200, 360), (40, 40, 40))
        draw = ImageDraw.Draw(src)
        for y in range(0, 360, 4):
            for x in range(0, 200, 4):
                if (x + y) % 8 == 0:
                    src.putpixel((x, y), (210, 210, 210))
        draw.rectangle([20, 40, 70, 280], fill=(30, 30, 30))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([8, 30, 88, 330], fill=(200, 200, 200))
        box = [8, 30, 88, 330]
        self.assertFalse(
            bubble_recall_local.balloon_is_white(src, box),
            "SFX on screentone is not a white balloon",
        )
        self.assertTrue(
            bubble_recall_local.needs_overlay("覚悟を決めろ", box, out, src, dest="做好觉悟吧"),
            "MIT gray plate on art must be restyled as glyph-only, not kept",
        )

    def test_overlay_on_screentone_does_not_paint_flat_plate(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (200, 360), (40, 40, 40))
        for y in range(360):
            for x in range(200):
                src.putpixel(
                    (x, y),
                    (210, 210, 210) if ((x // 3) + (y // 3)) % 2 == 0 else (150, 150, 150),
                )
        draw = ImageDraw.Draw(src)
        draw.rectangle([28, 50, 52, 260], fill=(25, 25, 25))
        img = src.copy()
        box = [8, 30, 88, 330]
        bubble_recall_local.render_translation(img, box, "做好觉悟吧", src)

        def flat(px: tuple[int, int, int]) -> bool:
            return max(px) - min(px) < 8 and 170 <= px[0] <= 230

        samples = [
            img.getpixel((12, 40)),
            img.getpixel((80, 40)),
            img.getpixel((12, 310)),
            img.getpixel((80, 310)),
        ]
        self.assertFalse(
            all(flat(px) for px in samples),
            f"SFX overlay must not cover art with a gray card: {samples}",
        )

    def test_needs_overlay_restyles_white_card_on_screentone(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 400), (40, 40, 40))
        draw = ImageDraw.Draw(src)
        draw.ellipse([80, 40, 320, 360], fill=(168, 168, 168))
        draw.rectangle([188, 90, 200, 290], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([160, 80, 240, 310], fill=(255, 255, 255))
        box = [110, 70, 290, 330]
        self.assertTrue(
            bubble_recall_local.needs_overlay("そ．．．そうなんだ．．．", box, out, src),
            "white plate on a gray balloon must be restyled, not kept",
        )

    def test_already_typeset_restyles_white_cards_on_dark_source(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (500, 180), (180, 40, 40))
        out = src.copy()
        draw = ImageDraw.Draw(out)
        draw.rectangle([40, 40, 280, 140], fill=(255, 255, 255))
        box = [40, 40, 280, 140]
        self.assertFalse(
            bubble_recall_local.already_typeset(
                "村おこしは仔作りから♥",
                box,
                ["村おこしは", "仔作りから"],
                [box],
                [("村おこしは", box)],
                out,
                src,
            ),
            "MIT white cards on a red TOC must be restyled, not kept",
        )

    def test_needs_overlay_restyles_undersized_typeset_in_large_balloon(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 400), (80, 80, 80))
        draw = ImageDraw.Draw(src)
        draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        draw.rectangle([188, 90, 200, 290], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        od.rectangle([188, 90, 200, 290], fill=(20, 20, 20))
        box = [110, 70, 290, 330]
        dest = "就在小间宫的旁边说了一整句"
        self.assertTrue(
            bubble_recall_local.needs_overlay(
                "小間宮の隣で",
                box,
                out,
                src,
                dest=dest,
            ),
            "MIT kept a thin glyph column; overlay must reuse the balloon at a larger size",
        )

    def test_needs_overlay_when_outlined_source_remains(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (220, 300), (80, 80, 80))
        draw = ImageDraw.Draw(src)
        draw.ellipse([20, 20, 200, 280], fill=(250, 250, 250), outline=(20, 20, 20), width=4)
        for top in (50, 110, 170):
            draw.rectangle([80, top, 140, top + 40], outline=(90, 90, 90), width=3)
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.text((95, 120), "妈", fill=(20, 20, 20))
        box = [40, 30, 180, 270]
        self.assertTrue(
            bubble_recall_local.needs_overlay(
                "おっかって、可愛いです",
                box,
                out,
                src,
                dest="妈妈，好可爱",
            ),
            "outlined Japanese still in a white balloon must be re-overlaid",
        )

    def test_needs_overlay_skips_when_mit_already_typeset_balloon(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 400), (80, 80, 80))
        draw = ImageDraw.Draw(src)
        draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        draw.rectangle([188, 90, 200, 290], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        od.rectangle([130, 90, 142, 290], fill=(20, 20, 20))
        box = [110, 70, 290, 330]
        self.assertFalse(
            bubble_recall_local.needs_overlay(
                "どうですか先生？この日のためにネットとバナナで勉強してたんです♡",
                box,
                out,
                src,
            ),
            "MIT already filled the balloon; overlay must not stack a white card",
        )
        self.assertTrue(
            bubble_recall_local.needs_overlay(
                "どうですか先生？この日のためにネットとバナナで勉強してたんです♡",
                box,
                src,
                src,
            ),
            "untouched Japanese leftover must still overlay",
        )

    def test_needs_overlay_skips_unchanged_chalkboard_sign(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (200, 320), (170, 170, 170))
        draw = ImageDraw.Draw(src)
        draw.rectangle([80, 40, 110, 260], fill=(30, 30, 30))
        box = [70, 30, 120, 270]
        self.assertFalse(
            bubble_recall_local.balloon_is_white(src, box),
            "chalkboard sign is gray, not a white balloon",
        )
        self.assertFalse(
            bubble_recall_local.needs_overlay(
                "大津２群年",
                box,
                src,
                src,
                dest="大津2群年",
            ),
            "MIT left original chalkboard art; overlay must not paint OCR garbage",
        )

    def test_needs_overlay_skips_unchanged_outlined_sfx(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (420, 220), (210, 210, 210))
        draw = ImageDraw.Draw(src)
        for left in range(40, 360, 48):
            draw.rectangle([left, 70, left + 36, 160], outline=(20, 20, 20), width=4)
        box = [30, 50, 400, 180]
        self.assertFalse(
            bubble_recall_local.needs_overlay(
                "時々特に特に",
                box,
                src,
                src,
                dest="有时特别特别",
            ),
            "MIT left decorative SFX; overlay must not stamp a translation on it",
        )

    def test_render_keeps_mit_white_when_source_still_has_glyphs(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 400), (80, 80, 80))
        draw = ImageDraw.Draw(src)
        draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        draw.rectangle([118, 78, 130, 108], fill=(20, 20, 20))
        draw.rectangle([250, 90, 262, 280], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([118, 78, 130, 108], fill=(200, 240, 200))
        box = [110, 70, 290, 330]
        bubble_recall_local.render_translation(out, box, "请多多指教", src)
        cleaned = out.getpixel((124, 90))
        self.assertEqual(
            cleaned,
            (200, 240, 200),
            f"MIT inpaint color must stay; restoring source Japanese then CPU-wiping it is the regression: {cleaned}",
        )

    def test_restore_source_art_restores_stacked_chinese_on_chalkboard(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import bubble_recall_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            src = Image.new("RGB", (240, 360), (170, 170, 170))
            draw = ImageDraw.Draw(src)
            draw.rectangle([90, 40, 130, 300], fill=(25, 25, 25))
            src.save(root / "typeset_in" / "p0001.png")
            out = src.copy()
            od = ImageDraw.Draw(out)
            od.rectangle([90, 40, 130, 300], fill=(200, 20, 20))
            out.save(root / "out" / "p0001.png")
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "p0001": {
                            "regions": [
                                {
                                    "xyxy": [80, 30, 140, 310],
                                    "text": "大津２群年",
                                    "score": 0.7,
                                    "covered": False,
                                }
                            ]
                        }
                    }
                ),
                encoding="utf-8",
            )
            (root / "translations.json").write_text(
                json.dumps({"大津２群年": "大津2群年"}, ensure_ascii=False),
                encoding="utf-8",
            )
            painted = Image.open(root / "out" / "p0001.png").convert("RGB")
            self.assertEqual(painted.getpixel((110, 80)), (200, 20, 20))
            bubble_recall_local.restore_source_art(root, ["p0001"])
            result = Image.open(root / "out" / "p0001.png").convert("RGB")
            self.assertEqual(
                result.getpixel((110, 80)),
                (25, 25, 25),
                "MIT-painted OCR garbage on chalkboard art must be restored to source",
            )
            self.assertEqual(result.getpixel((40, 40)), (170, 170, 170))

    def test_restore_source_art_skips_mit_white_balloon(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import bubble_recall_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            src = Image.new("RGB", (400, 400), (80, 80, 80))
            draw = ImageDraw.Draw(src)
            draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
            draw.rectangle([190, 90, 210, 280], fill=(20, 20, 20))
            src.save(root / "typeset_in" / "p0002.png")
            out = src.copy()
            od = ImageDraw.Draw(out)
            od.rectangle([190, 90, 210, 280], fill=(200, 240, 200))
            out.save(root / "out" / "p0002.png")
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "p0002": {
                            "regions": [
                                {
                                    "xyxy": [110, 70, 290, 330],
                                    "text": "皆さんこれからよろしくお願いします！",
                                    "score": 0.9,
                                    "covered": True,
                                }
                            ]
                        }
                    }
                ),
                encoding="utf-8",
            )
            (root / "translations.json").write_text(
                json.dumps(
                    {"皆さんこれからよろしくお願いします！": "各位，今后请多多指教！"},
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            bubble_recall_local.restore_source_art(root, ["p0002"])
            result = Image.open(root / "out" / "p0002.png").convert("RGB")
            self.assertEqual(
                result.getpixel((200, 120)),
                (200, 240, 200),
                "MIT Lama on a white balloon must not be restored to source Japanese",
            )

    def test_overlay_pages_restores_stacked_chinese_on_art(self) -> None:
        import json
        import tempfile
        from PIL import Image, ImageDraw
        import bubble_recall_local

        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "typeset_in").mkdir()
            (root / "out").mkdir()
            src = Image.new("RGB", (240, 360), (170, 170, 170))
            draw = ImageDraw.Draw(src)
            draw.rectangle([90, 40, 130, 300], fill=(25, 25, 25))
            src.save(root / "typeset_in" / "p0001.jpg")
            out = src.copy()
            od = ImageDraw.Draw(out)
            od.text((96, 160), "2群", fill=(10, 10, 10))
            out.save(root / "out" / "p0001.jpg")
            (root / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "p0001": {
                            "regions": [
                                {
                                    "xyxy": [80, 30, 140, 310],
                                    "text": "大津２群年",
                                    "score": 0.7,
                                    "covered": False,
                                }
                            ]
                        }
                    }
                ),
                encoding="utf-8",
            )
            (root / "translations.json").write_text(
                json.dumps({"大津２群年": "大津2群年"}, ensure_ascii=False),
                encoding="utf-8",
            )
            bubble_recall_local.overlay_pages(root, ["p0001"])
            result = Image.open(root / "out" / "p0001.jpg").convert("RGB")
            self.assertEqual(
                result.getpixel((110, 80)),
                (25, 25, 25),
                "stacked OCR-garbage overlay on chalkboard art must be restored to source",
            )
            self.assertEqual(result.getpixel((40, 40)), (170, 170, 170))

    def test_needs_overlay_when_mit_left_a_source_column(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 400), (80, 80, 80))
        draw = ImageDraw.Draw(src)
        draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        draw.rectangle([250, 90, 262, 290], fill=(20, 20, 20))
        draw.rectangle([200, 90, 212, 290], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([200, 90, 212, 290], fill=(250, 250, 250))
        od.rectangle([140, 90, 152, 290], fill=(30, 30, 30))
        box = [110, 70, 290, 330]
        self.assertTrue(
            bubble_recall_local.needs_overlay(
                "これからは先生の事…プライベートでは耕作さんと呼んでもよろしいですか？",
                box,
                out,
                src,
            ),
            "MIT typeset 2 of 3 columns; leftover Japanese must still overlay",
        )
        self.assertEqual(
            bubble_recall_local.uncovered_src(
                "これからは先生の事…プライベートでは耕作さんと呼んでもよろしいですか？",
                [
                    "これからは先生の事…",
                    "耕作さんと呼んでもよろしいですか？",
                ],
            ),
            "プライベートでは",
        )
        self.assertIn(
            "それじゃあ私のわだかまりも",
            bubble_recall_local.uncovered_src(
                "それじゃあ私のわだかまりも解消されたこだし耕作者のチ○ポのわたかまリも解油しないとね♡",
                ["解消されたことだし耕作君のチ○ポのわだかまりも解消しないとね♡"],
            ),
        )

    def test_restore_untranslated_art_undoes_logo_hard_translate(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 120), (40, 40, 40))
        draw = ImageDraw.Draw(src)
        draw.rectangle([20, 40, 300, 90], fill=(30, 30, 30))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([20, 40, 300, 90], fill=(255, 255, 255))
        box = [20, 40, 300, 90]
        self.assertTrue(
            bubble_recall_local.restore_untranslated_art(
                out,
                src,
                box,
                "推済時、子たち",
                [],
                {"推済時、子たち": "推济时，孩子们"},
            )
        )
        self.assertEqual(out.getpixel((40, 60)), src.getpixel((40, 60)))

    def test_overlay_wipes_mit_columns_outside_source_ink(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (400, 400), (80, 80, 80))
        draw = ImageDraw.Draw(src)
        draw.ellipse([80, 40, 320, 360], fill=(250, 250, 250))
        draw.rectangle([220, 90, 232, 280], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([220, 90, 232, 280], fill=(250, 250, 250))
        od.rectangle([112, 100, 120, 200], fill=(20, 20, 20))
        od.rectangle([170, 90, 182, 280], fill=(20, 20, 20))
        box = [110, 70, 290, 330]
        bubble_recall_local.render_translation(out, box, "今天是一边上课吗？", src)
        self.assertGreater(
            out.getpixel((116, 150))[0],
            200,
            "MIT leftover at the balloon edge must be restored before redraw",
        )

    def test_overlay_huge_detector_stays_inside_balloon(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (400, 400), (90, 90, 90))
        draw = ImageDraw.Draw(img)
        draw.ellipse([140, 240, 250, 370], fill=(250, 250, 250))
        draw.rectangle([175, 270, 205, 340], fill=(20, 20, 20))
        bubble_recall_local.render_translation(img, [0, 0, 400, 400], "没…没什么♡")

        def white(px: tuple[int, int, int]) -> bool:
            return px[0] > 240 and px[1] > 240 and px[2] > 240

        self.assertFalse(white(img.getpixel((10, 10))), "page corner must not be a white card")
        self.assertFalse(white(img.getpixel((390, 10))))
        self.assertFalse(white(img.getpixel((10, 390))))
        whites = sum(
            1
            for y in range(400)
            for x in range(400)
            if white(img.getpixel((x, y)))
        )
        self.assertLess(whites / 160000, 0.12, "overlay must hug the balloon, not the detector")

    def test_overlay_clears_antialiased_glyphs_and_keeps_balloon_stroke(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (240, 280), (40, 40, 40))
        draw = ImageDraw.Draw(src)
        draw.ellipse([30, 20, 210, 260], fill=(250, 250, 250), outline=(15, 15, 15), width=4)
        # Anti-aliased solid column: dark core with gray halo MIT inpaint leaves behind.
        for x in range(112, 128):
            for y in range(60, 210):
                dist = min(abs(x - 119), abs(x - 120))
                luma = 20 if dist == 0 else (70 if dist == 1 else 150)
                src.putpixel((x, y), (luma, luma, luma))
        img = src.copy()
        box = [40, 30, 200, 250]
        bubble_recall_local.render_translation(img, box, "还要更多", src)
        # Balloon stroke at the ellipse edge stays dark.
        self.assertLess(img.getpixel((32, 140))[0], 40, "oval balloon outline must remain")
        # The luma-150 halo neighbor-inpaint would copy; hole-fill must wipe it.
        ghosts = sum(
            1
            for y in range(70, 200)
            for x in range(112, 128)
            if 145 <= img.getpixel((x, y))[0] <= 155
        )
        self.assertLess(ghosts, 40, f"anti-aliased Japanese ghosts remain: {ghosts}")
        chinese = sum(
            1
            for y in range(box[1], box[3])
            for x in range(box[0], box[2])
            if img.getpixel((x, y))[0] < 40
        )
        self.assertGreater(chinese, 80, "Chinese must be drawn after the wipe")

    def test_overlay_clears_outlined_glyphs_inside_white_balloon(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (260, 320), (160, 160, 160))
        for y in range(320):
            for x in range(260):
                if (x // 3 + y // 3) % 2 == 0:
                    src.putpixel((x, y), (200, 200, 200))
        draw = ImageDraw.Draw(src)
        draw.ellipse([30, 20, 230, 300], fill=(250, 250, 250), outline=(20, 20, 20), width=5)
        # Hollow outlined letters with a gray halo — neighbor inpaint copies the halo, not white.
        for top in (70, 130, 190):
            draw.rectangle([108, top - 2, 154, top + 44], outline=(150, 150, 150), width=2)
            draw.rectangle([110, top, 150, top + 40], outline=(15, 15, 15), width=3)
        img = src.copy()
        box = [40, 30, 220, 290]
        bubble_recall_local.render_translation(img, box, "妈妈好可爱", src)
        self.assertLess(img.getpixel((32, 160))[0], 50, "oval outline must not be inpainted away")
        stroke_left = sum(
            1
            for y in range(70, 110)
            for x in range(108, 116)
            if img.getpixel((x, y))[0] < 160
        )
        self.assertLess(stroke_left, 20, f"outlined Japanese stroke remains: {stroke_left}")
        self.assertGreater(img.getpixel((50, 40))[0], 140, "screentone outside the balloon stays")

    def test_overlay_short_text_keeps_vertical_margin_in_tall_balloon(self) -> None:
        from PIL import Image
        import bubble_recall_local

        img = Image.new("RGB", (220, 300), (250, 250, 250))
        box = [20, 20, 189, 262]
        dest = "妈妈，好可爱"
        bubble_recall_local.render_translation(img, box, dest, img)
        size, columns = bubble_recall_local.choose_vertical_overlay_layout(
            161, 234, dest
        )
        self.assertEqual(len(columns), 1, columns)
        dark = [
            (x, y)
            for y in range(box[1], box[3])
            for x in range(box[0], box[2])
            if img.getpixel((x, y))[0] < 40
        ]
        self.assertTrue(dark, "short translation must still be drawn")
        ys = [p[1] for p in dark]
        top_gap = min(ys) - box[1]
        bot_gap = box[3] - 1 - max(ys)
        self.assertGreaterEqual(top_gap, 16, f"flush to top: top_gap={top_gap}")
        self.assertGreaterEqual(bot_gap, 16, f"flush to bottom: bot_gap={bot_gap}")
        fitted = bubble_recall_local.fit_overlay_box_to_text(box, dest)
        self.assertLess(fitted[3] - fitted[1], int((box[3] - box[1]) * 0.85))

    def test_needs_overlay_restyles_white_plate_on_oval_balloon(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (260, 320), (160, 160, 160))
        draw = ImageDraw.Draw(src)
        draw.ellipse([30, 20, 230, 300], fill=(250, 250, 250), outline=(20, 20, 20), width=4)
        for top in range(55, 250, 16):
            draw.rectangle([96, top, 160, top + 12], fill=(20, 20, 20))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rounded_rectangle([40, 30, 220, 290], radius=8, fill=(255, 255, 255))
        for i, y in enumerate((55, 90, 125, 160, 195, 230)):
            od.rectangle([168, y, 188, y + 22], fill=(20, 20, 20))
        box = [40, 30, 220, 290]
        self.assertFalse(
            bubble_recall_local.typeset_cramped(out, box),
            "wide plate is not the thin-column cramped case",
        )
        self.assertTrue(
            bubble_recall_local.needs_overlay(
                "おっかって、可愛いです",
                box,
                out,
                src,
                dest="妈妈，好可爱",
            ),
            "a rectangular white plate filling an oval balloon must be re-laid-out",
        )

    def test_skip_as_art_keeps_long_dialogue_in_normal_balloon(self) -> None:
        import bubble_recall_local

        long_line = (
            "それじゃあ私のわだかまりも解消されたことだし"
            "耕作君のチ○ポのわだかまりも解消しないとね♡"
        )
        balloon = [874, 33, 1027, 289]
        self.assertFalse(
            bubble_recall_local.skip_as_art(long_line, balloon),
            "long dialogue inside a balloon is not page-sized OCR garbage",
        )
        self.assertTrue(
            bubble_recall_local.skip_as_art(long_line),
            "essay-length OCR without a balloon is still garbage",
        )
        self.assertTrue(
            bubble_recall_local.skip_as_art(
                "３月２７日に東京を開いて学校中学校に関すると、子どもが学ませて、"
                "人余で話を始めるために、最大を読んで学年ぜく一年",
                [277, 370, 755, 1337],
            ),
            "page-sized essay OCR over the TOC must stay skipped",
        )

    def test_restore_untranslated_art_skips_page_sized_garbage(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (800, 1200), (180, 40, 40))
        draw = ImageDraw.Draw(src)
        draw.rectangle([300, 400, 700, 520], fill=(250, 250, 250))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([300, 400, 700, 520], fill=(40, 40, 40))
        huge = [277, 370, 755, 1337]
        self.assertFalse(
            bubble_recall_local.restore_untranslated_art(
                out,
                src,
                huge,
                "３月２７日に東京を開いて学校中学校に関すると、子どもが学ませて",
                [],
                {},
            ),
            "restoring a page-sized garbage box would wipe already-painted titles",
        )
        self.assertEqual(out.getpixel((320, 450)), (40, 40, 40))

    def test_overlay_clears_white_glyphs_on_red_title(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        img = Image.new("RGB", (500, 200), (180, 40, 40))
        draw = ImageDraw.Draw(img)
        for left in (40, 90, 140, 190, 240):
            draw.rectangle([left, 70, left + 28, 130], fill=(250, 250, 250))
        src = img.copy()
        box = [30, 50, 470, 150]
        bubble_recall_local.render_translation(
            img, box, "把精子播在我们身上", src
        )
        for left in (40, 90, 140):
            px = img.getpixel((left + 6, 78))
            self.assertLess(
                px[0] + px[1] + px[2],
                520,
                f"white Japanese block at x={left} remains: {px}",
            )
        self.assertFalse(
            img.getpixel((40, 55))[0] > 240
            and img.getpixel((40, 55))[1] > 240
            and img.getpixel((40, 55))[2] > 240,
            "red TOC must not become a white card",
        )

    def test_overlay_grows_wipe_to_outlined_rim_outside_detector(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (260, 340), (160, 160, 160))
        draw = ImageDraw.Draw(src)
        draw.ellipse([30, 20, 230, 320], fill=(250, 250, 250), outline=(20, 20, 20), width=5)
        box = [50, 40, 210, 270]
        for top in range(48, 250, 44):
            draw.rectangle([58, top, 202, top + 32], outline=(15, 15, 15), width=3)
        draw.rectangle([90, 272, 170, 304], outline=(15, 15, 15), width=3)
        img = src.copy()
        bubble_recall_local.render_translation(img, box, "妈妈，好可爱", src)
        stroke_left = sum(
            1
            for y in range(274, 302)
            for x in range(92, 168)
            if img.getpixel((x, y))[0] < 80
        )
        self.assertLess(stroke_left, 30, f"rim outlined leftover remains: {stroke_left}")

    def test_overlay_restores_mit_plate_on_sfx_before_redraw(self) -> None:
        from PIL import Image, ImageDraw
        import bubble_recall_local

        src = Image.new("RGB", (200, 360), (40, 40, 40))
        for y in range(360):
            for x in range(200):
                src.putpixel(
                    (x, y),
                    (210, 210, 210) if ((x // 3) + (y // 3)) % 2 == 0 else (150, 150, 150),
                )
        draw = ImageDraw.Draw(src)
        draw.rectangle([28, 50, 52, 260], fill=(25, 25, 25))
        out = src.copy()
        od = ImageDraw.Draw(out)
        od.rectangle([8, 30, 88, 330], fill=(190, 190, 190))
        box = [8, 30, 88, 330]
        bubble_recall_local.render_translation(out, box, "做好觉悟吧", src)

        def flat(px: tuple[int, int, int]) -> bool:
            return max(px) - min(px) < 8 and 170 <= px[0] <= 230

        samples = [
            out.getpixel((12, 40)),
            out.getpixel((80, 40)),
            out.getpixel((12, 310)),
            out.getpixel((80, 310)),
        ]
        self.assertFalse(
            all(flat(px) for px in samples),
            f"MIT gray SFX plate must be restored before redraw: {samples}",
        )

    def test_dialogue_boxes_keep_text_bubble_and_empty_balloon(self) -> None:
        import bubble_recall_local

        hits = [
            {"cls_id": 0, "xyxy": [0, 0, 100, 100]},
            {"cls_id": 1, "xyxy": [10, 10, 80, 80]},
            {"cls_id": 0, "xyxy": [200, 200, 300, 300]},
            {"cls_id": 2, "xyxy": [400, 400, 450, 450]},
        ]
        boxes = bubble_recall_local.dialogue_boxes(hits)
        self.assertEqual(len(boxes), 3)
        self.assertEqual(boxes[0]["xyxy"], [10, 10, 80, 80])
        self.assertTrue(boxes[1].get("from_empty_bubble"))
        self.assertEqual(boxes[1]["xyxy"], [200, 200, 300, 300])
        self.assertTrue(boxes[2].get("from_text_free"))
        self.assertEqual(boxes[2]["xyxy"], [400, 400, 450, 450])

    def test_merge_recall_keys_copies_stem_and_queues_true_misses(self) -> None:
        import tempfile
        import bubble_recall_local
        import fill_ocr_local

        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            (work / "translations.json").write_text(
                json.dumps({"ねぇ": "喂", "おはよう": ""}, ensure_ascii=False),
                encoding="utf-8",
            )
            (work / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "p1": {
                            "mtime": 1,
                            "regions": [
                                {
                                    "text": "ねぇ！！",
                                    "covered": True,
                                    "xyxy": [1, 1, 2, 2],
                                },
                                {
                                    "text": "イクのかよ♡",
                                    "covered": True,
                                    "xyxy": [3, 3, 4, 4],
                                },
                            ],
                        }
                    }
                ),
                encoding="utf-8",
            )
            (work / "uncovered_dialogue.json").write_text(
                json.dumps(
                    {
                        "count": 1,
                        "items": [
                            {
                                "stem": "p2",
                                "text": "見てて♡",
                                "reason": "untranslated",
                            }
                        ],
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )
            added = bubble_recall_local.merge_cached_recall_keys(work)
            self.assertGreater(added, 0)
            mapping = json.loads(
                (work / "translations.json").read_text(encoding="utf-8")
            )
            self.assertEqual(mapping["ねぇ！！"], "喂")
            self.assertEqual(mapping.get("イクのかよ♡", ""), "")
            self.assertEqual(mapping.get("見てて♡", ""), "")
            pending = fill_ocr_local.pending_keys(
                mapping, [key for key in mapping if key not in fill_ocr_local.SKIP]
            )
            self.assertIn("イクのかよ♡", pending)
            self.assertIn("見てて♡", pending)
            self.assertNotIn("ねぇ！！", pending)

    def test_fill_skips_fresh_bubble_recall(self) -> None:
        import tempfile
        import fill_ocr_local
        import bubble_recall_local

        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp)
            typeset_in = work / "typeset_in"
            typeset_in.mkdir()
            img = typeset_in / "002.png"
            img.write_bytes(b"png")
            (work / "translations.json").write_text(
                json.dumps({"おはよう": ""}, ensure_ascii=False), encoding="utf-8"
            )
            (work / "bubble_recall.json").write_text(
                json.dumps(
                    {
                        "002": {
                            "mtime": img.stat().st_mtime,
                            "regions": [
                                {
                                    "text": "おはよう",
                                    "covered": False,
                                    "xyxy": [1, 1, 2, 2],
                                }
                            ],
                        }
                    }
                ),
                encoding="utf-8",
            )
            old = sys.argv
            try:
                sys.argv = ["fill_ocr_local.py", str(work)]
                with (
                    patch.object(
                        bubble_recall_local, "launch_with_mit", return_value=0
                    ) as launch,
                    patch.object(
                        fill_ocr_local, "translate_mapping", return_value={}
                    ),
                ):
                    rc = fill_ocr_local.main()
            finally:
                sys.argv = old
            self.assertEqual(rc, 0)
            launch.assert_not_called()

    def test_copy_game_junctions_media_dirs(self) -> None:
        import tempfile
        import one_click_rm

        with tempfile.TemporaryDirectory() as tmp:
            src = Path(tmp) / "src"
            dest = Path(tmp) / "dest"
            (src / "data").mkdir(parents=True)
            (src / "data" / "System.json").write_text("{}", encoding="utf-8")
            (src / "img").mkdir()
            (src / "img" / "a.png").write_bytes(b"x")
            (src / "www" / "audio").mkdir(parents=True)
            (src / "www" / "audio" / "a.ogg").write_bytes(b"y")
            (src / "Game.exe").write_bytes(b"exe")
            one_click_rm.copy_game(src, dest)
            self.assertTrue((dest / "Game.exe").is_file())
            self.assertTrue((dest / "data" / "System.json").is_file())
            self.assertTrue((dest / "img").is_junction())
            self.assertTrue((dest / "www" / "audio").is_junction())
            self.assertFalse((dest / "data").is_junction())
            self.assertEqual((dest / "img" / "a.png").read_bytes(), b"x")
            self.assertEqual((dest / "www" / "audio" / "a.ogg").read_bytes(), b"y")


if __name__ == "__main__":
    unittest.main()
