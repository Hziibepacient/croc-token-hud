import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from app_follow import (Lifecycle, WatcherLock, configure, desktop_exec, hud_command,
                        process_alive, process_kind, supported_processes, systemd_exec, watcher_command)
from metrics import atomic_json, monitor
from test_history import response, write_log


class FollowTests(unittest.TestCase):
    def test_desktop_cli_ide_backends_without_editor_false_positives(self):
        self.assertEqual(process_kind(r'C:\Program Files\WindowsApps\OpenAI.Codex_26_x64\app\ChatGPT.exe'), 'desktop')
        self.assertEqual(process_kind('/Applications/Codex.app/Contents/MacOS/Codex'), 'desktop')
        self.assertEqual(process_kind('/home/me/.vscode/extensions/openai/vendor/bin/codex'), 'backend')
        self.assertEqual(process_kind(r'C:\Cursor\extensions\vendor\codex.exe'), 'backend')
        self.assertEqual(process_kind('/opt/bin/codex'), 'backend')
        for other in ('/opt/Cursor', '/usr/bin/code', '/usr/bin/node', 'ChatGPT.exe', 'CodexTokenHud.exe', '/usr/bin/ssh'):
            self.assertIsNone(process_kind(other))

    def test_exact_custom_executable_and_no_basename_guess(self):
        self.assertEqual(process_kind('/opt/custom/client', ['/opt/custom/client']), 'custom')
        self.assertIsNone(process_kind('/other/client', ['/opt/custom/client']))

    def test_idle_does_not_start_then_debounces_and_starts_once(self):
        state = Lifecycle()
        self.assertIsNone(state.step([], False, False, 0))
        self.assertIsNone(state.step([1], False, False, 1))
        self.assertEqual(state.step([1], False, False, 2), 'start')
        self.assertIsNone(state.step([1, 2], True, True, 3))
        self.assertIsNone(state.step([2], True, True, 4))

    def test_short_version_probe_does_not_open_hud(self):
        state = Lifecycle()
        self.assertIsNone(state.step([1], False, False, 0))
        self.assertIsNone(state.step([], False, False, 0.3))

    def test_last_client_closes_owned_hud_after_grace(self):
        state = Lifecycle()
        state.step([1], True, True, 0)
        self.assertIsNone(state.step([], True, True, 1))
        self.assertIsNone(state.step([], True, True, 6))
        self.assertEqual(state.step([], True, True, 7), 'close')
        self.assertIsNone(state.step([], True, True, 8))

    def test_manually_opened_hud_is_never_closed(self):
        state = Lifecycle()
        state.step([1], True, False, 0)
        state.step([], True, False, 1)
        self.assertIsNone(state.step([], True, False, 20))

    def test_manual_exit_suppressed_until_clients_close_and_reopen(self):
        state = Lifecycle()
        state.step([1], True, True, 0)
        self.assertIsNone(state.step([1], False, True, 2))
        self.assertIsNone(state.step([1, 2], False, True, 20))
        state.step([], False, True, 21)
        state.step([], False, True, 28)
        state.step([3], False, False, 30)
        self.assertEqual(state.step([3], False, False, 31), 'start')

    def test_reopen_during_automatic_shutdown_can_start_again(self):
        state = Lifecycle()
        state.step([1], True, True, 0)
        state.step([], True, True, 1)
        self.assertEqual(state.step([], True, True, 7), 'close')
        state.step([2], True, True, 8)
        self.assertEqual(state.step([2], False, True, 9), 'start')

    def test_client_returns_before_grace_does_not_close(self):
        state = Lifecycle()
        state.step([1], True, True, 0)
        state.step([], True, True, 1)
        self.assertIsNone(state.step([2], True, True, 6))

    def test_lock_closes_simultaneous_watcher_race(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = WatcherLock(Path(directory)), WatcherLock(Path(directory))
            try:
                self.assertTrue(first.acquire())
                self.assertFalse(second.acquire())
                first.close()
                self.assertTrue(second.acquire())
            finally:
                first.close()
                second.close()

    def test_failed_registration_preserves_old_config(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            old = {'enabled': False, 'installation': 'old'}
            atomic_json(path, old)
            with patch('app_follow.register', side_effect=RuntimeError('denied')):
                with self.assertRaisesRegex(RuntimeError, 'denied'):
                    configure([str(Path(directory) / 'hud.exe')], directory, directory, True, path)
            self.assertEqual(path.read_text(), __import__('json').dumps(old, ensure_ascii=False))

    def test_other_copy_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            atomic_json(path, {'enabled': True, 'installation': 'another copy'})
            with patch('app_follow.register') as register:
                with self.assertRaisesRegex(RuntimeError, 'another copy'):
                    configure(['/own/hud'], directory, directory, True, path)
                register.assert_not_called()

    def test_watch_command_and_desktop_quoting(self):
        command = watcher_command(Path('/a directory/config.json'))
        self.assertIn(str(Path('/a directory/config.json')), command)
        self.assertIn('--watch', command)
        escaped = desktop_exec(['/a b/py', '/a/$x`q"\\100%'])
        self.assertIn('100%%', escaped)
        self.assertIn('\\\\$', escaped)
        self.assertIn('\\\\\\\\', escaped)
        self.assertEqual(systemd_exec(['/a b/py', '$x%']), '"/a b/py" "$$x%%"')

    def test_headless_can_use_dashboard_without_importing_qt(self):
        self.assertIn('--no-browser', hud_command('dashboard'))
        root = str(Path(__file__).resolve().parents[1])
        result = subprocess.run([sys.executable, '-I', '-c',
                                 'import sys; sys.path.insert(0, ' + repr(root) + '); import app_follow; assert "PySide6" not in sys.modules'],
                                capture_output=True, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_generic_launcher_passes_arguments_and_returns_client_exit(self):
        from app_follow import main
        from unittest.mock import Mock
        child = Mock(pid=os.getpid())
        child.wait.return_value = 7
        with tempfile.TemporaryDirectory() as directory, patch('app_follow.subprocess.Popen', side_effect=[child, Mock()]) as popen, \
             patch('app_follow.signal.signal'):
            result = main(['--config', str(Path(directory) / 'config.json'), '--interface', 'dashboard',
                           '--run', '/unknown-client', 'argument with spaces', '--flag', ';'])
            self.assertEqual(result, 7)
            self.assertEqual(popen.call_args_list[0].args[0], ['/unknown-client', 'argument with spaces', '--flag', ';'])
            self.assertEqual(len(list((Path(directory) / 'launches').glob('*.json'))), 0)

    def test_direct_launcher_watcher_shares_installed_watcher_lock(self):
        from app_follow import watch
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            lock = WatcherLock(root)
            try:
                self.assertTrue(lock.acquire())
                config = root / 'launch-fixture/config.json'
                atomic_json(config, {'enabled': True, 'until_idle': True, 'lock_folder': str(root)})
                with patch('app_follow.supported_processes') as processes:
                    self.assertEqual(watch(config), 0)
                    processes.assert_not_called()
            finally:
                lock.close()

    def test_watcher_starts_and_stops_real_managed_dashboard(self):
        import time
        from app_follow import watch
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = root / 'watcher/config.json'
            atomic_json(config, {'enabled': True, 'command': hud_command('dashboard'),
                                'runtime': str(root / 'runtime'), 'codex_home': str(root / 'empty')})
            started, sleep = time.monotonic(), time.sleep
            processes = []
            popen = subprocess.Popen
            def launch(*args, **kwargs):
                process = popen(*args, **kwargs)
                processes.append(process)
                return process
            with patch('app_follow.supported_processes', side_effect=lambda extra: [(1, 'backend')] if time.monotonic() - started < 1 else []), \
                 patch('app_follow.Lifecycle', side_effect=lambda: Lifecycle(settle=0.01, grace=0.2)), \
                 patch('app_follow.time.sleep', side_effect=lambda seconds: sleep(0.05)), \
                 patch('app_follow.subprocess.Popen', side_effect=launch):
                watch(config, duration=2)
            try:
                self.assertEqual(len(processes), 1)
                self.assertEqual(processes[0].wait(timeout=3), 0)
            finally:
                for process in processes:
                    if process.poll() is None:
                        process.kill()
                        process.wait()

    def test_process_enumeration_returns_only_supported_targets(self):
        found = supported_processes()
        self.assertTrue(all(isinstance(pid, int) and pid > 0 and kind in ('desktop', 'backend') for pid, kind in found))

    def test_stale_status_of_exited_hud_is_not_an_existing_instance(self):
        self.assertTrue(process_alive(os.getpid()))
        child = subprocess.Popen([sys.executable, '-c', 'pass'])
        child.wait()
        self.assertFalse(process_alive(child.pid))
        self.assertFalse(process_alive(None))

    def test_activity_binding_uses_latest_log_and_does_not_guess_ties(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_log(root / 'sessions/a.jsonl', response(0, 2, 'a'), thread='a')
            write_log(root / 'sessions/b.jsonl', response(0, 3, 'b'), thread='b')
            atomic_json(root / 'runtime/selection.json', {'binding_mode': 'activity'})
            result = monitor(root, root / 'runtime', once=True)
            self.assertEqual(result['thread_id'], 'b')
            self.assertEqual(result['binding_mode'], 'activity')
            write_log(root / 'sessions/c.jsonl', response(0, 3, 'c'), thread='c')
            result = monitor(root, root / 'runtime', once=True)
            self.assertEqual(result['binding'], 'ambiguous')
            self.assertIsNone(result['thread_id'])

    def test_managed_dashboard_closes_only_for_its_owner(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            process = subprocess.Popen([sys.executable, '-I', '-X', 'utf8', str(Path(__file__).resolve().parents[1] / 'dashboard.py'),
                                        '--no-browser', '--codex-home', str(root / 'empty'), '--runtime', str(root / 'runtime'), '--follow-owner', 'fixture-owner'],
                                       stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            try:
                import time
                deadline = time.monotonic() + 8
                while not (root / 'runtime/status.json').exists() and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertTrue((root / 'runtime/status.json').exists())
                atomic_json(root / 'runtime/app-close.request.json', {'owner': 'other'})
                time.sleep(0.6)
                self.assertIsNone(process.poll())
                atomic_json(root / 'runtime/app-close.request.json', {'owner': 'fixture-owner'})
                self.assertEqual(process.wait(timeout=5), 0)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
                process.stderr.close()
