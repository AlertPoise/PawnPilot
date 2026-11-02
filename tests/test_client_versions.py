"""CLI writes must use the operator's reviewed wire version, never guess it."""
import contextlib
import io
import unittest
from unittest.mock import patch

from bridge import client


class ClientVersionTests(unittest.TestCase):
    def invoke(self, action, version=None):
        argv = ["client.py", action, "--id", "reviewed-command",
                "--broker-epoch", "broker", "--world-epoch", "world",
                "--revision", "4", "--expected-version", "world:4"]
        if version is not None:
            argv += ["--schema-version", str(version)]
        if action == "reconcile":
            argv += ["--operator-note", "Reviewed closed menu evidence", "--confirm-no-replay"]
        with patch("sys.argv", argv), patch.object(client.Path, "read_text", return_value="{}"), \
                patch.object(client, "request", return_value=(200, {})) as request, \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as result:
                client.main()
        return result.exception.code, request

    def test_close_menu_preserves_reviewed_v2_v3_and_v4(self):
        for version in (2, 3, 4):
            with self.subTest(version=version):
                code, request = self.invoke("close_menu", version)
                self.assertEqual(code, 0)
                self.assertEqual(request.call_args.args[1], "/commands")
                self.assertEqual(request.call_args.args[2]["schema_version"], version)

    def test_reconcile_preserves_reviewed_v2_v3_and_v4(self):
        for version in (2, 3, 4):
            with self.subTest(version=version):
                code, request = self.invoke("reconcile", version)
                self.assertEqual(code, 0)
                self.assertEqual(request.call_args.args[1], "/reconcile")
                self.assertEqual(request.call_args.args[2]["schema_version"], version)

    def test_missing_or_unsupported_version_cannot_send_a_write(self):
        for action in ("close_menu", "reconcile"):
            for version in (None, 1, 5):
                with self.subTest(action=action, version=version):
                    code, request = self.invoke(action, version)
                    self.assertEqual(code, 2)
                    request.assert_not_called()
