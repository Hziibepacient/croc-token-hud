import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest

from dashboard import Dashboard, LocalServer
from test_history import response, write_log
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

    def test_croc_page_and_model_are_local_but_api_requires_token(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            server = LocalServer(Dashboard(root, root / 'runtime'))
            worker = threading.Thread(target=server.serve_forever, daemon=True)
            worker.start()
            try:
                connection = http.client.HTTPConnection('127.0.0.1', server.server_port)
                for path, expected in [('/croc', 200), ('/croc-model.js', 200), ('/croc.js', 200), ('/api/threads', 403)]:
                    connection.request('GET', path)
                    result = connection.getresponse()
                    self.assertEqual(result.status, expected)
                    result.read()
                connection.close()
            finally:
                server.shutdown()
                server.server_close()
                worker.join()
