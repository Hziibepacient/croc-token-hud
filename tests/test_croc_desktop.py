import unittest
from croc_scene import bottles, scene, level


class DesktopSceneTests(unittest.TestCase):
    def sample(self, rate=30, stage='generating'):
        return {'source': {'state': 'ok'}, 'metrics': {'stage': stage, 'turn_id': 'a',
                'turn_started_at_ms': 100, 'turn_output_tokens': 15000, 'user_input': {'text_tokens_estimate': 100},
                'last': {'turn_id': 'a', 'measured_at_ms': 101, 'input_tokens': 100000, 'rate': rate}}}

    def test_continuous_speed_and_density(self):
        samples=[scene(self.sample(rate)) for rate in [1,10,30,80,200,500]]
        self.assertTrue(all(a['seconds']>b['seconds'] for a,b in zip(samples,samples[1:])))
        self.assertTrue(all(a['spacing']>=b['spacing'] for a,b in zip(samples,samples[1:])))
        self.assertEqual([s['lanes'] for s in samples],[1,1,2,2,3,3])
    def test_counts_and_no_estimated_input(self):
        s = scene(self.sample())
        self.assertEqual(('bottles' in s, s['lanes']), (False, 2))
        value = self.sample()
        value['metrics']['last']['turn_id'] = 'old'
        s = scene(value)
        self.assertEqual(('bottles' in s, s['lanes'], s['seconds']), (False, 0, 0))

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
            self.assertNotIn('bottles', scene(value))

    def test_fresh_message_estimate_boundaries(self):
        value = self.sample()
        for tokens, expected in [(0,0),(99,1),(100,2),(500,3),(2000,4),(8000,5)]:
            value['metrics']['user_input']['text_tokens_estimate'] = tokens
            self.assertNotIn('bottles', scene(value))

    def test_five_tier_boundaries(self):
        for rate,tier,lanes in [(14.9,1,1),(15,2,1),(29.9,2,1),(30,3,2),(59.9,3,2),(60,4,2),(99.9,4,2),(100,5,3)]:
            result=scene(self.sample(rate))
            self.assertEqual((result["speed_tier"],result["lanes"]),(tier,lanes))

    def test_quota_bottles_round_up_and_hide_unknown_or_expired(self):
        for remaining, expected in [(0,0),(0.1,1),(20,1),(20.1,2),(74,4),(100,5)]:
            self.assertEqual(bottles({'quota_status':'recorded','quota_remaining':remaining}), expected)
        self.assertEqual(bottles({'quota_status':'awaiting_reset','quota_remaining':80}), 0)
        self.assertEqual(bottles({'quota_status':'unknown','quota_remaining':None}), 0)
