import json
from pathlib import Path
import tempfile
import unittest
from local_quota import LocalQuota


def record(at='2026-10-21T01:00:00Z', used=25, reset=2000000000, minutes=300, bucket='codex'):
    return {'timestamp':at, 'payload':{'rate_limits':{'limit_id':bucket,'primary':{'used_percent':used,'window_minutes':minutes,'resets_at':reset}}}}


class LocalQuotaTests(unittest.TestCase):
    def test_real_percentage_zero_and_reset(self):
        reader=LocalQuota()
        reader.feed(record(used=0,reset=100))
        self.assertEqual(reader.update([],now=99)['quota_remaining'],100)
        self.assertEqual(reader.update([],now=100)['quota_status'],'awaiting_reset')
        self.assertEqual(reader.update([],now=100)['quota_remaining'],100)  # preserved snapshot, not a fresh refill

    def test_wrong_bucket_window_invalid_values_and_old_records(self):
        reader=LocalQuota()
        for r in [record(minutes=10080),record(bucket='other'),record(used=None),record(used=-1),record(used=float('nan'))]:reader.feed(r)
        self.assertEqual(reader.update([])['quota_status'],'unknown')
        reader.feed(record(used=25))
        reader.feed(record(at='2026-10-20T01:00:00Z',used=50))
        self.assertEqual(reader.update([])['quota_remaining'],75)

    def test_incremental_half_line_and_background_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'background.jsonl'
            data=json.dumps(record())+'\n'
            path.write_text(data[:30],encoding='utf8')
            reader=LocalQuota()
            self.assertEqual(reader.update([path])['quota_status'],'unknown')
            with path.open('a',encoding='utf8') as output:output.write(data[30:])
            self.assertEqual(reader.update([path])['quota_remaining'],75)
            path.write_text(json.dumps(record(at='2026-10-21T02:00:00Z',used=50))+'\n',encoding='utf8')
            self.assertEqual(reader.update([path])['quota_remaining'],50)
