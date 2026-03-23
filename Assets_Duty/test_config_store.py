import json
import tempfile
import unittest
from pathlib import Path

from execution_profiles import resolve_execution_profile
from state_ops import Context, ConfigVersionConflictError, load_config, patch_config, save_config


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
        self.assertEqual(sorted(persisted.keys()), ["duty_rule", "plan_presets", "selected_plan_id", "version"])
        self.assertNotIn("api_key", persisted)
        self.assertNotIn("model_presets", persisted)
        self.assertEqual(persisted["version"], 1)

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
        self.assertEqual(sorted(persisted.keys()), ["duty_rule", "plan_presets", "selected_plan_id", "version"])
        self.assertEqual(persisted["duty_rule"], "rule")
        self.assertEqual(persisted["version"], 1)

    def test_patch_config_updates_profile_used_by_future_runs(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            load_config(context)

            patch_config(
                context,
                {
                    "selected_plan_id": "agents",
                    "plan_presets": [
                        {
                            "id": "standard",
                            "name": "Standard",
                            "mode_id": "standard",
                            "api_key": "",
                            "base_url": "http://127.0.0.1:11434/v1",
                            "model": "llama3",
                            "model_profile": "cloud",
                            "provider_hint": "standard-provider",
                            "multi_agent_execution_mode": "auto",
                        },
                        {
                            "id": "agents",
                            "name": "Agents",
                            "mode_id": "agents",
                            "api_key": "",
                            "base_url": "http://127.0.0.1:11434/v1",
                            "model": "campus-model",
                            "model_profile": "campus_small",
                            "provider_hint": "campus-provider",
                            "multi_agent_execution_mode": "serial",
                        },
                    ],
                },
            )
            updated_config = load_config(context)

        profile = resolve_execution_profile(
            {
                "model_profile": "edge",
                "orchestration_mode": "single_pass",
                "provider_hint": "request-provider",
            },
            updated_config,
        )

        self.assertEqual(updated_config["selected_plan_id"], "agents")
        self.assertEqual(updated_config["orchestration_mode"], "multi_agent")
        self.assertEqual(updated_config["model_profile"], "campus_small")
        self.assertEqual(updated_config["multi_agent_execution_mode"], "serial")
        self.assertEqual(updated_config["version"], 2)
        self.assertEqual(profile.model_profile, "campus_small")
        self.assertEqual(profile.orchestration_mode, "multi_agent")

    def test_patch_config_with_matching_expected_version_increments_version(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            initial = load_config(context)

            updated = patch_config(
                context,
                {
                    "expected_version": initial["version"],
                    "duty_rule": "new rule",
                },
            )

        self.assertEqual(initial["version"], 1)
        self.assertEqual(updated["version"], 2)
        self.assertEqual(updated["duty_rule"], "new rule")

    def test_patch_config_without_effective_changes_keeps_version(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            initial = load_config(context)

            updated = patch_config(
                context,
                {
                    "expected_version": initial["version"],
                    "selected_plan_id": initial["selected_plan_id"],
                },
            )

        self.assertEqual(updated["version"], initial["version"])

    def test_patch_config_rejects_stale_expected_version(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            context = Context(Path(temp_dir))
            load_config(context)
            patch_config(context, {"expected_version": 1, "duty_rule": "rule a"})

            with self.assertRaisesRegex(ConfigVersionConflictError, "expected 1, current 2"):
                patch_config(context, {"expected_version": 1, "duty_rule": "rule b"})


if __name__ == "__main__":
    unittest.main()
