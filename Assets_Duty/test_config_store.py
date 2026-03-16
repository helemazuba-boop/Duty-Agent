import json
import tempfile
import unittest
from pathlib import Path

from state_ops import Context, load_config, patch_config, save_config


class TestConfigStore(unittest.TestCase):
    def test_load_config_rewrites_to_current_persisted_shape(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            config_path = data_dir / "config.json"
            config_path.write_text(
                json.dumps(
                    {
                        "selected_plan_id": "standard",
                        "plan_presets": [
                            {
                                "id": "standard",
                                "name": "标准",
                                "mode_id": "standard",
                                "api_key": "",
                                "base_url": "http://127.0.0.1:11434/v1",
                                "model": "llama3",
                                "model_profile": "auto",
                                "provider_hint": "",
                                "multi_agent_execution_mode": "auto",
                            }
                        ],
                        "duty_rule": "rule",
                        "api_key": "legacy",
                        "model_presets": [{"id": "legacy"}],
                    },
                    ensure_ascii=False,
                ),
                encoding="utf-8",
            )

            runtime_config = load_config(Context(data_dir))
            persisted = json.loads(config_path.read_text(encoding="utf-8"))

        self.assertEqual(runtime_config["model"], "llama3")
        self.assertEqual(sorted(persisted.keys()), ["duty_rule", "plan_presets", "selected_plan_id"])
        self.assertNotIn("api_key", persisted)
        self.assertNotIn("model_presets", persisted)

    def test_patch_config_rejects_removed_top_level_fields(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            load_config(context)

            with self.assertRaisesRegex(ValueError, "Unsupported config patch keys: api_key"):
                patch_config(context, {"api_key": "legacy"})

    def test_save_config_persists_only_current_fields(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            context = Context(data_dir)
            runtime_config = save_config(
                context,
                {
                    "selected_plan_id": "standard",
                    "plan_presets": [
                        {
                            "id": "standard",
                            "name": "标准",
                            "mode_id": "standard",
                            "api_key": "",
                            "base_url": "http://127.0.0.1:11434/v1",
                            "model": "llama3",
                            "model_profile": "auto",
                            "provider_hint": "",
                            "multi_agent_execution_mode": "auto",
                        }
                    ],
                    "duty_rule": "rule",
                    "api_key": "should-be-dropped",
                },
            )
            persisted = json.loads((data_dir / "config.json").read_text(encoding="utf-8"))

        self.assertEqual(runtime_config["model"], "llama3")
        self.assertEqual(sorted(persisted.keys()), ["duty_rule", "plan_presets", "selected_plan_id"])
        self.assertEqual(persisted["duty_rule"], "rule")


if __name__ == "__main__":
    unittest.main()
