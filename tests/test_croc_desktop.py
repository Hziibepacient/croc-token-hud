import unittest
from croc_desktop import scene, level


class DesktopSceneTests(unittest.TestCase):
    def sample(self, rate=30, stage='generating'):
        return {'source': {'state': 'ok'}, 'metrics': {'stage': stage, 'turn_id': 'a',
                'turn_started_at_ms': 100, 'turn_output_tokens': 15000, 'user_input': {'text_tokens_estimate': 100},
                'last': {'turn_id': 'a', 'measured_at_ms': 101, 'input_tokens': 100000, 'rate': rate}}}

    def test_agreed_speed_bands(self):
        for rate, seconds in [(1, 4), (29.9, 4), (30, 2), (79.9, 2), (80, .8), (200, .8)]:
            self.assertEqual(scene(self.sample(rate))['seconds'], seconds)

    def test_counts_and_no_estimated_input(self):
        s = scene(self.sample())
        self.assertEqual((s['bottles'], s['lanes']), (2, 5))
        value = self.sample()
        value['metrics']['last']['turn_id'] = 'old'
        s = scene(value)
        self.assertEqual((s['bottles'], s['lanes'], s['seconds']), (2, 0, 0))

    def test_completion_and_tool_wait(self):
        self.assertFalse(scene(self.sample(stage='idle'))['working'])
        self.assertTrue(scene(self.sample(stage='idle'))['idle'])
        tool = scene(self.sample(stage='tools'))
        self.assertTrue(tool['working'])
        self.assertFalse(tool['emitting'])

    def test_missing_and_invalid_rates_do_not_move(self):
        for rate in [None, float('nan'), 0, -1]:
            self.assertFalse(scene(self.sample(rate))['emitting'])
        self.assertEqual(level(0, [2000]), 0)

    def test_menu_metadata_preserves_recent_response_cache(self):
        value = self.sample()
        value['metrics']['model'] = 'example-model'
        value['metrics']['last'].update(cache_percent=80, cached_input_tokens=80000, output_tokens=2500)
        s = scene(value)
        self.assertEqual(s['model'], 'example-model')
        self.assertEqual(s['cache_percent'], 80)
        self.assertEqual(s['cached_input'], 80000)
        self.assertEqual(s['last_output'], 2500)

    def test_long_context_does_not_always_saturate_bottles(self):
        value = self.sample()
        for tokens in [9999, 10000, 50000, 150000, 247312, 300000]:
            value['metrics']['last']['input_tokens'] = tokens
            self.assertEqual(scene(value)['bottles'], 2)

    def test_fresh_message_estimate_boundaries(self):
        value = self.sample()
        for tokens, expected in [(0,0),(99,1),(100,2),(500,3),(2000,4),(8000,5)]:
            value['metrics']['user_input']['text_tokens_estimate'] = tokens
            self.assertEqual(scene(value)['bottles'], expected)
