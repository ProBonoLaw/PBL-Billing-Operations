import os
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import subprocess
import tempfile
from threading import Thread
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "integrate-public-main.sh"


class PrivateIntegrationTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.env = {
            **os.environ,
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_TERMINAL_PROMPT": "0",
            "GIT_AUTHOR_NAME": "Sync test",
            "GIT_AUTHOR_EMAIL": "sync-test@example.invalid",
            "GIT_COMMITTER_NAME": "Sync test",
            "GIT_COMMITTER_EMAIL": "sync-test@example.invalid",
        }
        self.remote = self.root / "private.git"
        self.public_remote = self.root / "public.git"
        self.env["PUBLIC_REPOSITORY_URL"] = str(self.public_remote)
        self.private = self.root / "private"
        self.public = self.root / "public"
        self.worker = self.root / "worker"
        self.git(self.root, "init", "--bare", "--initial-branch=main", str(self.remote))
        self.git(self.root, "init", "--bare", "--initial-branch=main", str(self.public_remote))
        for repo, remote in ((self.private, self.remote), (self.public, self.public_remote)):
            self.git(self.root, "init", "--initial-branch=main", str(repo))
            self.git(repo, "remote", "add", "origin", str(remote))

        self.old_private = self.commit(self.private, "code.txt", "private history\n")
        self.private_snapshot = self.commit(self.private, "code.txt", "baseline\n")
        self.public_snapshot = self.commit(self.public, "code.txt", "baseline\n")
        self.git(self.private, "push", "origin", "HEAD:main")
        self.git(self.public, "push", "origin", "HEAD:main")
        self.git(self.root, "clone", str(self.remote), str(self.worker))

    def git(self, repo, *args, check=True):
        return subprocess.run(
            ["git", "-C", str(repo), *args], env=self.env,
            text=True, capture_output=True, check=check,
        )

    def commit(self, repo, name, contents):
        (repo / name).write_text(contents)
        self.git(repo, "add", name)
        self.git(repo, "commit", "-m", "Update " + name)
        return self.git(repo, "rev-parse", "HEAD").stdout.strip()

    def remote_main(self):
        return self.git(self.remote, "rev-parse", "main").stdout.strip()

    def integrate(self, success=True):
        result = subprocess.run(
            ["bash", str(SCRIPT)], cwd=self.worker, env=self.env,
            text=True, capture_output=True,
        )
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def assert_ancestor(self, commit, branch="main"):
        self.git(self.remote, "merge-base", "--is-ancestor", commit, branch)

    def test_bootstrap_links_equal_trees_and_preserves_both_histories(self):
        self.integrate()
        self.assert_ancestor(self.old_private)
        self.assert_ancestor(self.public_snapshot)
        self.git(self.remote, "diff", "--exit-code", self.private_snapshot, "main")
        self.assertNotEqual(
            self.git(self.public_remote, "cat-file", "-e", self.old_private,
                     check=False).returncode, 0,
        )
        self.assertEqual(self.git(self.public_remote, "rev-parse", "main").stdout.strip(),
                         self.public_snapshot)

    def test_repeated_sync_is_a_no_op(self):
        self.integrate()
        integrated = self.remote_main()
        self.integrate()
        self.assertEqual(self.remote_main(), integrated)

    def test_public_http_fetch_does_not_send_private_authorization(self):
        self.git(self.public_remote, "update-server-info")
        authorization_headers = []

        class PublicGitHandler(SimpleHTTPRequestHandler):
            def do_GET(self):
                authorization_headers.append(self.headers.get("Authorization"))
                super().do_GET()

            def log_message(self, *_args):
                pass

        server = ThreadingHTTPServer(
            ("127.0.0.1", 0), partial(PublicGitHandler, directory=str(self.root)),
        )
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            self.env["PUBLIC_REPOSITORY_URL"] = (
                f"http://127.0.0.1:{server.server_port}/public.git"
            )
            # Simulate authentication configured for the other remote.
            self.git(self.worker, "config", "http.extraHeader",
                     "Authorization: Bearer private-test-token")
            self.git(self.worker, "config",
                     f"http.{self.env['PUBLIC_REPOSITORY_URL']}.extraHeader",
                     "Authorization: Bearer private-test-token")
            self.integrate()
            self.assertTrue(authorization_headers)
            self.assertTrue(all(header is None for header in authorization_headers))
            self.assert_ancestor(self.public_snapshot)
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def test_public_changes_preserve_independent_private_changes(self):
        self.integrate()
        self.git(self.private, "pull", "--ff-only", "origin", "main")
        private_commit = self.commit(self.private, "private.txt", "private configuration\n")
        self.git(self.private, "push", "origin", "HEAD:main")
        self.commit(self.public, "code.txt", "public update one\n")
        latest_public = self.commit(self.public, "code.txt", "public update two\n")
        self.git(self.public, "push", "origin", "HEAD:main")
        self.integrate()
        self.assert_ancestor(private_commit)
        self.assert_ancestor(latest_public)
        self.assertEqual(
            self.git(self.remote, "show", "main:private.txt").stdout,
            "private configuration\n",
        )
        self.assertEqual(
            self.git(self.remote, "show", "main:code.txt").stdout,
            "public update two\n",
        )

    def test_conflict_leaves_private_main_unchanged(self):
        self.integrate()
        self.git(self.private, "pull", "--ff-only", "origin", "main")
        private_commit = self.commit(self.private, "code.txt", "private edit\n")
        self.git(self.private, "push", "origin", "HEAD:main")
        self.commit(self.public, "code.txt", "public edit\n")
        self.git(self.public, "push", "origin", "HEAD:main")
        result = self.integrate(success=False)
        self.assertIn("Resolve the merge conflict", result.stdout)
        self.assertEqual(self.remote_main(), private_commit)
        self.assertEqual(self.git(self.worker, "status", "--porcelain").stdout, "")

    def test_bootstrap_with_different_trees_requires_private_review(self):
        private_commit = self.commit(self.private, "private.txt", "keep me\n")
        self.git(self.private, "push", "origin", "HEAD:main")
        result = self.integrate(success=False)
        self.assertIn("Initial public and private trees differ", result.stdout)
        self.assertEqual(self.remote_main(), private_commit)

    def test_dirty_checkout_is_rejected(self):
        (self.worker / "untracked.txt").write_text("keep me\n")
        result = self.integrate(success=False)
        self.assertIn("clean working tree", result.stdout)
        self.assertEqual(self.remote_main(), self.private_snapshot)


if __name__ == "__main__":
    unittest.main()
