from __future__ import annotations

import importlib.util
import subprocess
import unittest
from pathlib import Path
from unittest.mock import patch


SCRIPT = Path(__file__).parents[1] / "scripts" / "fetch_comments.py"
SPEC = importlib.util.spec_from_file_location("fetch_comments", SCRIPT)
assert SPEC and SPEC.loader
fetch_comments = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(fetch_comments)


class GetCurrentPrRefTests(unittest.TestCase):
    @patch.object(fetch_comments, "gh_pr_view_json")
    def test_uses_base_repository_from_fork_pr_url(self, view_pr):
        view_pr.return_value = {
            "number": 482,
            "url": "https://github.com/base-owner/base-repo/pull/482",
        }

        self.assertEqual(
            fetch_comments.get_current_pr_ref(),
            ("base-owner", "base-repo", 482),
        )
        view_pr.assert_called_once_with("number,url")

    @patch.object(fetch_comments, "gh_pr_view_json")
    def test_rejects_unexpected_pr_url(self, view_pr):
        view_pr.return_value = {
            "number": 1,
            "url": "https://github.com/base-owner/base-repo/issues/1",
        }

        with self.assertRaisesRegex(RuntimeError, "Unexpected pull request URL"):
            fetch_comments.get_current_pr_ref()


class CommandFailureTests(unittest.TestCase):
    @patch.object(fetch_comments.subprocess, "run")
    def test_missing_command_has_actionable_error(self, run):
        run.side_effect = FileNotFoundError

        with self.assertRaisesRegex(RuntimeError, "Command not found: gh"):
            fetch_comments._run(["gh", "auth", "status"])


if __name__ == "__main__":
    unittest.main()
