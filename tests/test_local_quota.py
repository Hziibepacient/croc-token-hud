import json
import os
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
        self.assertEqual(reader.update([],window_minutes=300)['quota_status'],'unknown')
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
            stat=path.stat()
            path.write_text(json.dumps(record(at='2026-10-21T02:00:00Z',used=50))+'\n',encoding='utf8')
            # Same-size rewrites are detected by mtime; coarse Windows clocks can repeat it within one test.
            os.utime(path,ns=(stat.st_atime_ns,stat.st_mtime_ns+1_000_000))
            self.assertEqual(reader.update([path])['quota_remaining'],50)

    def test_primary_and_secondary_are_both_kept_and_selectable(self):
        reader=LocalQuota()
        value=record(used=25)
        value['payload']['rate_limits']['secondary']={'used_percent':60,'window_minutes':10080,'resets_at':2000005000}
        reader.feed(value)
        automatic=reader.update([],now=100)
        self.assertEqual(automatic['quota_window_minutes'],300)
        self.assertEqual(len(automatic['quota_windows']),2)
        weekly=reader.update([],now=100,window_minutes=10080)
        self.assertEqual((weekly['quota_remaining'],weekly['quota_window_label']),(40,'1 周'))
        self.assertEqual(reader.update([],now=100,window_minutes=300)['quota_remaining'],75)

    def test_automatic_uses_actual_window_without_five_hour_record(self):
        reader=LocalQuota()
        reader.feed(record(minutes=1440))
        result=reader.update([],now=100)
        self.assertEqual((result['quota_window_minutes'],result['quota_window_label']),(1440,'1 天'))
        missing=reader.update([],now=100,window_minutes=300)
        self.assertEqual((missing['quota_status'],missing['quota_remaining']),('unknown',None))
        self.assertEqual(len(missing['quota_windows']),1)

    def test_expiry_and_chronology_are_independent_per_window(self):
        reader=LocalQuota()
        reader.feed(record(used=10,reset=100))
        reader.feed(record(minutes=10080,used=80,reset=1000))
        reader.feed(record(at='2026-10-20T01:00:00Z',minutes=10080,used=1))
        self.assertEqual(reader.update([],now=100)['quota_remaining'],20)
        self.assertEqual(reader.update([],now=100,window_minutes=300)['quota_status'],'awaiting_reset')
        self.assertEqual(reader.update([],now=100,window_minutes=10080)['quota_remaining'],20)

    def test_arbitrary_cycles_and_invalid_periods(self):
        reader=LocalQuota()
        for invalid in [None,0,-1,True,1.5,float('nan'),float('inf')]:
            reader.feed(record(minutes=invalid))
        self.assertEqual(reader.update([])['quota_windows'],[])
        for minutes,label in [(90,'90 分钟'),(43200,'30 天'),(120,'2 小时')]:
            reader.feed(record(minutes=minutes))
            self.assertEqual(reader.update([],window_minutes=minutes)['quota_window_label'],label)
