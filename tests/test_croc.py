import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest

from dashboard import Dashboard, LocalServer
from croc_scene import SceneFeed, migrate_layout, current_chat, follow_preference
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

    def test_follow_mode_persists_separately_from_observed_chat(self):
        class Service:
            catalog=type('Catalog',(),{'files':{}})()
            def get(self,endpoint,query):
                if endpoint=='threads':
                    return {'threads':[{'id':'latest','title':'Latest','updated_at_ms':2},
                                       {'id':'pinned','title':'Pinned','updated_at_ms':1}]}
                return {'source':{'state':'ok'},'metrics':{'stage':'idle'}}
        with tempfile.TemporaryDirectory() as directory:
            selection=Path(directory)/'selection.json'
            selection.write_text('{"thread_id":"pinned"}')
            pinned=SceneFeed(Service(),selection).snapshot()
            self.assertEqual((pinned['follow_mode'],pinned['pinned_thread_id'],pinned['selected']),('pinned','pinned','pinned'))
            selection.write_text('{"thread_id":""}')
            for _ in range(2):
                automatic=SceneFeed(Service(),selection).snapshot()
                self.assertEqual((automatic['follow_mode'],automatic['pinned_thread_id'],automatic['selected']),('auto','','latest'))

    def test_current_chat_requires_fresh_unique_title(self):
        rows=[{'id':'a','title':'Visible'},{'id':'b','title':'Background'}]
        self.assertEqual(current_chat(rows,{'title':'Visible','updated_at':100},now=101),('a',''))
        for observed in [{},{'title':'Visible','updated_at':90},{'title':'Visible','updated_at':200},
                         {'title':'Missing','updated_at':100},{'title':'','updated_at':100}]:
            self.assertEqual(current_chat(rows,observed,now=101)[0],'')
        duplicate=rows+[{'id':'c','title':'Visible'}]
        result=current_chat(duplicate,{'title':'Visible','updated_at':100},now=101)
        self.assertEqual(result[0],'')
        self.assertIn('同名',result[1])

    def test_current_mode_does_not_follow_background_activity(self):
        class Service:
            catalog=type('Catalog',(),{'files':{}})()
            def get(self,endpoint,query):
                if endpoint=='threads':
                    return {'threads':[{'id':'background','title':'Background','updated_at_ms':20},
                                       {'id':'visible','title':'Visible','updated_at_ms':1}]}
                return {'source':{'state':'ok'},'metrics':{'stage':'idle'}}
        with tempfile.TemporaryDirectory() as directory:
            selection=Path(directory)/'selection.json'
            selection.write_text('{"thread_id":"","follow_mode":"current"}')
            feed=SceneFeed(Service(),selection)
            self.assertEqual(feed.snapshot()['selected'],'')
            self.assertEqual(feed.snapshot()['follow_mode'],'current')
            observation=Path(directory)/'current-chat.json'
            import time
            observation.write_text(json.dumps({'title':'Visible','updated_at':time.time()}))
            result=feed.snapshot()
            self.assertEqual((result['selected'],result['follow_mode']),('visible','current'))
            self.assertEqual(SceneFeed(Service(),selection).snapshot()['selected'],'visible')
            observation.write_text(json.dumps({'title':'Background','updated_at':time.time()}))
            self.assertEqual(feed.snapshot()['selected'],'background')
            selection.write_text('{"thread_id":"","follow_mode":"auto"}')
            self.assertEqual(feed.snapshot()['selected'],'background')

    def test_legacy_and_explicit_follow_preferences(self):
        self.assertEqual(follow_preference({}),('auto',''))
        self.assertEqual(follow_preference({'thread_id':'fixed'}),('pinned','fixed'))
        self.assertEqual(follow_preference({'thread_id':'','follow_mode':'current'}),('current',''))
        self.assertEqual(follow_preference({'thread_id':'fixed','follow_mode':'current'}),('pinned','fixed'))

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

    def test_quota_choice_updates_without_rescanning_or_changing_thread(self):
        class Service:
            catalog=type('Catalog',(),{'files':{}})()
            def get(self,endpoint,query):
                return {'threads':[]}
        with tempfile.TemporaryDirectory() as directory:
            selection=Path(directory)/'selection.json'
            selection.write_text('{"thread_id":"keep-me"}')
            feed=SceneFeed(Service(),selection)
            feed.quota.feed(record(used=25))
            feed.quota.feed(record(minutes=10080,used=70))
            self.assertEqual(feed.snapshot(now=0)['quota_remaining'],75)
            preference=Path(directory)/'quota-selection.json'
            preference.write_text('{"window_minutes":10080}')
            weekly=feed.snapshot(now=.1)
            self.assertEqual((weekly['quota_remaining'],weekly['bottles']),(30,2))
            self.assertEqual(json.loads(selection.read_text())['thread_id'],'keep-me')
            self.assertEqual(SceneFeed(Service(),selection).quota_selection_path,preference)
            preference.write_text('{"window_minutes":0}')
            self.assertEqual(feed.snapshot(now=.2)['quota_window_minutes'],300)
