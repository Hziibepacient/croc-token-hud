import unittest
from metrics import Measurements, estimate_user_input


class FreshInputTests(unittest.TestCase):
    def event(self, kind, payload):
        return {'timestamp': '2026-10-20T10:00:00Z', 'type': kind, 'payload': payload}

    def test_only_actual_request_is_estimated(self):
        text = '# Files mentioned by the user:\nImage attachment: true\n<in-app-browser-context>hidden context</in-app-browser-context>\n## My request:\n你好'
        result = estimate_user_input(text)
        self.assertEqual(result['text_tokens_estimate'], 2)
        self.assertTrue(result['attachments_unknown'])
        self.assertNotIn('text', result)

    def test_both_event_orders_and_no_stale_carry(self):
        for before in [True, False]:
            m = Measurements()
            user = self.event('event_msg', {'type': 'user_message', 'message': '你好'})
            start = self.event('event_msg', {'type': 'task_started', 'turn_id': 'a'})
            for e in ([user, start] if before else [start, user]):
                m.feed(e)
            self.assertEqual(m.snapshot()['user_input']['text_tokens_estimate'], 2)
            m.feed(self.event('event_msg', {'type': 'task_complete'}))
            m.feed(self.event('event_msg', {'type': 'task_started', 'turn_id': 'b'}))
            self.assertIsNone(m.snapshot()['user_input'])

    def test_injected_instructions_and_files(self):
        self.assertIsNone(estimate_user_input('<environment_context>not a prompt</environment_context>'))
        self.assertEqual(estimate_user_input('', True)['text_tokens_estimate'], 0)
