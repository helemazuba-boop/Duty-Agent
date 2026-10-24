# -*- coding: utf-8 -*-
"""API Key 落盘加密（P0 隐私项）与配置版本号的回归测试。

钉住三件事：
1. `save_config` / `patch_config` 写进 config.json 的 api_key 是 DPAPI 密文，
   文件里查不到明文；
2. `load_config` 投影出来的仍是明文（执行器只认明文，解密集中在 state_ops 一处）；
3. 密文每次加密都不同，所以"配置没变"的判定必须屏蔽 api_key——否则什么都不改
   保存一次也会平白 bump 版本号，前端乐观锁会一直 409。
"""
import json
import os
import tempfile
import unittest
from pathlib import Path

import dpapi_compat
import state_ops
from state_ops import Context, create_default_config, load_config, patch_config, save_config

PLAIN_KEY = "sk-unit-test-should-never-hit-disk"


def _with_key(config, key=PLAIN_KEY):
    presets = config["plan_presets"]
    presets[0] = dict(presets[0])
    presets[0]["api_key"] = key
    config["plan_presets"] = presets
    return config


@unittest.skipUnless(os.name == "nt", "DPAPI 只在 Windows 可用")
class TestApiKeyAtRest(unittest.TestCase):
    def test_save_writes_ciphertext_and_load_returns_plaintext(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            ctx = Context(Path(temp_dir))
            save_config(ctx, _with_key(create_default_config()))

            raw = (Path(temp_dir) / "config.json").read_text(encoding="utf-8")
            self.assertNotIn(PLAIN_KEY, raw, "明文 API Key 出现在 config.json 里")
            self.assertIn(dpapi_compat.DPAPI_PREFIX, raw)

            loaded = load_config(ctx)
            self.assertEqual(loaded["api_key"], PLAIN_KEY)
            # plan_presets 是运行期投影，同样应该是明文（设置页要能回显）。
            self.assertEqual(
                loaded["plan_presets"][0]["api_key"],
                PLAIN_KEY,
            )

    def test_save_is_idempotent_and_does_not_nest(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            ctx = Context(Path(temp_dir))
            config = _with_key(create_default_config())
            save_config(ctx, config)

            reloaded = load_config(ctx)
            save_config(ctx, reloaded)
            raw = (Path(temp_dir) / "config.json").read_text(encoding="utf-8")
            self.assertEqual(raw.count(dpapi_compat.DPAPI_PREFIX), 1)
            self.assertEqual(load_config(ctx)["api_key"], PLAIN_KEY)

    def test_unchanged_patch_does_not_bump_version(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            ctx = Context(Path(temp_dir))
            config = _with_key(create_default_config())
            saved = save_config(ctx, config)
            version = saved["version"]

            # 前端保存时会把整份 plan_presets（含明文 key）原样回传：内容没变，
            # 版本号就不该动，否则乐观锁会自己和自己冲突。
            same = patch_config(
                ctx,
                {
                    "expected_version": version,
                    "selected_plan_id": saved["selected_plan_id"],
                    "plan_presets": saved["plan_presets"],
                },
            )
            self.assertEqual(same["version"], version)

    def test_changing_model_still_bumps_version(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            ctx = Context(Path(temp_dir))
            saved = save_config(ctx, _with_key(create_default_config()))
            version = saved["version"]

            presets = [dict(preset) for preset in saved["plan_presets"]]
            presets[0]["model"] = "another-model"
            changed = patch_config(
                ctx,
                {
                    "expected_version": version,
                    "selected_plan_id": saved["selected_plan_id"],
                    "plan_presets": presets,
                },
            )
            self.assertEqual(changed["version"], version + 1)
            self.assertEqual(changed["model"], "another-model")

    def test_legacy_plaintext_config_is_readable_then_upgraded(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            ctx = Context(Path(temp_dir))
            # 模拟加密上线前的存量文件：磁盘上就是明文。
            base = state_ops._normalize_persisted_config(create_default_config())
            base["plan_presets"][0]["api_key"] = PLAIN_KEY
            (Path(temp_dir) / "config.json").write_text(
                json.dumps(base, ensure_ascii=False, indent=2), encoding="utf-8"
            )

            self.assertEqual(load_config(ctx)["api_key"], PLAIN_KEY)
            save_config(ctx, load_config(ctx))
            raw = (Path(temp_dir) / "config.json").read_text(encoding="utf-8")
            self.assertNotIn(PLAIN_KEY, raw)


if __name__ == "__main__":
    unittest.main()
