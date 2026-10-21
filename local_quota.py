"""Offline, incremental quota snapshots from local Codex logs only."""
import json
import math
import time
from pathlib import Path
from metrics import timestamp_ms


def window_label(minutes):
    for size, unit in ((10080, '周'), (1440, '天'), (60, '小时')):
        if minutes % size == 0:
            return f'{minutes // size} {unit}'
    return f'{minutes} 分钟'


class LocalQuota:
    def __init__(self):
        self.files = {}
        self.windows = {}

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
            if not isinstance(window, dict):
                continue
            minutes = window.get('window_minutes')
            if isinstance(minutes, bool) or not isinstance(minutes, (int, float)) or not math.isfinite(minutes) or minutes <= 0 or minutes != int(minutes):
                continue
            minutes = int(minutes)
            used, reset = window.get('used_percent'), window.get('resets_at')
            if not isinstance(used, (int, float)) or not math.isfinite(used) or not 0 <= used <= 100:
                continue
            if not isinstance(reset, (int, float)) or not math.isfinite(reset) or reset <= 0:
                continue
            previous = self.windows.get(minutes)
            if previous and at <= previous['quota_recorded_at']:
                continue
            self.windows[minutes] = dict(quota_used=used, quota_remaining=100-used,
                               quota_resets_at=reset, quota_recorded_at=at,
                               quota_source='local_log', quota_window_minutes=minutes,
                               quota_window_label=window_label(minutes))

    def update(self, paths, now=None, window_minutes=0):
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
        return self.snapshot(now=now, window_minutes=window_minutes)

    def snapshot(self, now=None, window_minutes=0):
        now = time.time() if now is None else now
        selected = window_minutes if isinstance(window_minutes, int) and not isinstance(window_minutes, bool) and window_minutes > 0 else 0
        options = []
        for minutes, value in sorted(self.windows.items()):
            option = dict(value)
            option['quota_status'] = 'recorded' if option['quota_resets_at'] > now else 'awaiting_reset'
            options.append(option)
        effective = selected
        if not effective and options:
            active = [o['quota_window_minutes'] for o in options if o['quota_status'] == 'recorded']
            choices = active or [o['quota_window_minutes'] for o in options]
            effective = 300 if 300 in choices else min(choices)
        result = next((dict(o) for o in options if o['quota_window_minutes'] == effective), None)
        if result is None:
            result = dict(quota_status='unknown', quota_remaining=None,
                          quota_window_minutes=selected or None,
                          quota_window_label=window_label(selected) if selected else '额度')
        result.update(quota_windows=options, quota_selection_minutes=selected)
        return result
