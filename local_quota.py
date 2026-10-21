"""Offline, incremental quota snapshots from local Codex logs only."""
import json
import math
import time
from pathlib import Path
from metrics import timestamp_ms


class LocalQuota:
    def __init__(self):
        self.files = {}
        self.latest = None

    def feed(self, event):
        payload = event.get('payload') or {}
        limits = payload.get('rate_limits') if isinstance(payload, dict) else None
        at = timestamp_ms(event.get('timestamp'))
        if not isinstance(limits, dict) or at is None:
            return
        if limits.get('limit_id') not in (None, 'codex'):
            return
        for name in ('primary', 'secondary'):
            window = limits.get(name)
            if not isinstance(window, dict) or window.get('window_minutes') != 300:
                continue
            used, reset = window.get('used_percent'), window.get('resets_at')
            if not isinstance(used, (int, float)) or not math.isfinite(used) or not 0 <= used <= 100:
                continue
            if not isinstance(reset, (int, float)) or not math.isfinite(reset) or reset <= 0:
                continue
            if self.latest and at <= self.latest['quota_recorded_at']:
                continue
            self.latest = dict(quota_used=used, quota_remaining=100-used,
                               quota_resets_at=reset, quota_recorded_at=at,
                               quota_source='local_log')

    def update(self, paths, now=None):
        for item in paths:
            path = Path(item)
            try:
                stat = path.stat()
                previous = self.files.get(str(path))
                if previous and previous['size'] == stat.st_size and previous['mtime'] == stat.st_mtime_ns:
                    continue
                reset = previous is None or previous['offset'] > stat.st_size or (previous['size'] == stat.st_size and previous['mtime'] != stat.st_mtime_ns)
                offset = max(0, stat.st_size-2_000_000) if reset else previous['offset']
                with path.open('rb') as stream:
                    stream.seek(offset)
                    if reset and offset:
                        stream.readline()
                    while True:
                        start = stream.tell()
                        line = stream.readline()
                        if not line or not line.endswith(b'\n'):
                            offset = start
                            break
                        try:
                            self.feed(json.loads(line))
                        except (ValueError, TypeError, AttributeError):
                            pass
                self.files[str(path)] = dict(offset=offset, size=stat.st_size, mtime=stat.st_mtime_ns)
            except OSError:
                continue
        if not self.latest:
            return dict(quota_status='unknown', quota_remaining=None)
        result = dict(self.latest)
        result['quota_status'] = 'recorded' if result['quota_resets_at'] > (time.time() if now is None else now) else 'awaiting_reset'
        return result
