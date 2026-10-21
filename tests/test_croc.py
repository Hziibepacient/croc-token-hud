import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest

from dashboard import Dashboard, LocalServer
from croc_scene import SceneFeed, migrate_layout
from test_history import response, write_log
from test_local_quota import record
from test_metrics import event


class CrocIntegrationTests(unittest.TestCase):
    def test_real_log_completion_reaches_live_endpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = root / 'sessions' / 'croc.jsonl'
            records = response(0, 2, 'turn-croc', output=5000, input_tokens=30000)
            write_log(log, records, thread='croc')
            service = Dashboard(root, root / 'runtime')
            live = service.get('live', {'thread': ['croc']})
            self.assertEqual(live['metrics']['stage'], 'generating')
            self.assertEqual(live['metrics']['turn_output_tokens'], 5000)
            with log.open('a', encoding='utf8') as stream:
                stream.write(json.dumps(event(3, 'event_msg', {'type': 'task_complete'})) + '\n')
            live = service.get('live', {'thread': ['croc'], 'force': ['1']})
            self.assertEqual(live['metrics']['stage'], 'idle')

    def test_croc_page_and_assets_are_local_but_api_requires_token(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            server = LocalServer(Dashboard(root, root / 'runtime'))
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            try:
                connection = http.client.HTTPConnection('127.0.0.1', server.server_port)
                for path, expected in [('/croc', 200), ('/croc.js', 200), ('/assets/bottle.png', 200), ('/api/threads', 403), ('/api/croc', 403)]:
                    connection.request('GET', path)
                    result = connection.getresponse()
                    self.assertEqual(result.status, expected)
                    result.read()
                connection.close()
            finally:
                server.shutdown()
                server.server_close()
                worker.join()

    def test_browser_endpoint_shares_desktop_scene_and_quota(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            records = response(0, 2, 'turn-croc', output=5000) + [record(at=event(2, '', {})['timestamp'], used=45)]
            write_log(root / 'sessions' / 'croc.jsonl', records, thread='croc')
            service = Dashboard(root, root / 'runtime')
            value = service.get('croc', {'thread': ['croc']})
            self.assertEqual((value['selected'], value['quota_remaining'], value['bottles']), ('croc', 55, 3))
            self.assertEqual(service.get('croc', {})['selected'], 'croc')  # empty thread follows latest activity
            for key in ('working', 'emitting', 'lanes', 'seconds', 'spacing', 'speed_tier', 'status'):
                self.assertIn(key, value)
            with self.assertRaises(ValueError):
                service.get('live', {'thread': ['missing']})
            self.assertEqual(service.get('croc', {'thread': ['missing']})['selected'], '')

    def test_feed_throttles_thread_listing(self):
        class Service:
            calls = 0
            catalog = type('Catalog', (), {'files': {}})()
            def get(self, endpoint, query):
                if endpoint == 'threads':
                    Service.calls += 1
                    return {'threads': []}
        feed = SceneFeed(Service())
        for now in [0, .1, .5, 1.9]:
            feed.snapshot(now=now)
        self.assertEqual(Service.calls, 1)
        feed.snapshot(now=2.1)
        self.assertEqual(Service.calls, 2)

    def test_layout_migrates_without_overwriting(self):
        with tempfile.TemporaryDirectory() as directory:
            old, new = Path(directory) / 'old', Path(directory) / 'new'
            old.mkdir()
            (old / 'layout.json').write_text('{"x": 1}')
            (old / 'selection.json').write_text('{"thread_id": "a"}')
            new.mkdir()
            (new / 'selection.json').write_text('{"thread_id": "b"}')
            migrate_layout(old, new)
            self.assertEqual((new / 'layout.json').read_text(), '{"x": 1}')
            self.assertEqual((new / 'selection.json').read_text(), '{"thread_id": "b"}')
