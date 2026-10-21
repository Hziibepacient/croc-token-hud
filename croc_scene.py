"""Telemetry-to-scene rules shared by the desktop and browser crocodile views."""
from pathlib import Path
import math
import time

from local_quota import LocalQuota
from metrics import read_selection

RATE_LIMITS = [15, 30, 60, 100]
LANES = [0, 1, 1, 2, 2, 3]
BANDS = ['', '慢悠悠', '轻快', '忙碌', '飞快', '全速']
THREAD_REFRESH_SECONDS = 2
QUOTA_REFRESH_SECONDS = 1


def level(value, limits):
    if not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        return 0
    return 1 + sum(value >= limit for limit in limits)


def bottles(quota):
    """One bottle per started 20% of the selected known quota window."""
    remaining = quota.get('quota_remaining')
    if quota.get('quota_status') != 'recorded' or not isinstance(remaining, (int, float)):
        return 0
    return math.ceil(max(0, min(100, remaining)) / 20)


def follow_preference(value):
    pinned = value.get('thread_id')
    pinned = pinned if isinstance(pinned, str) else ''
    if pinned:
        return 'pinned', pinned
    return ('current' if value.get('follow_mode') == 'current' else 'auto'), ''


def current_chat(rows, observation, now=None):
    """Bind only a fresh visible title with exactly one local match."""
    now = time.time() if now is None else now
    at = observation.get('updated_at')
    if not isinstance(at, (int, float)) or not math.isfinite(at) or not 0 <= now-at < 5:
        return '', '等待识别 Codex 当前对话'
    title = observation.get('title')
    if not isinstance(title, str) or not title:
        return '', '未识别到 Codex 对话窗口，请打开对话'
    matches = [row for row in rows if row['title'] == title]
    if len(matches) > 1:
        return '', '当前对话存在同名记录，请右键固定选择'
    if not matches:
        return '', '当前对话暂无可匹配的本地日志'
    return matches[0]['id'], ''


def scene(value):
    m = value.get('metrics') or {}
    last = m.get('last') or {}
    start = m.get('turn_started_at_ms')
    valid = (start is not None and last.get('measured_at_ms', 0) >= start and
             (not last.get('turn_id') or not m.get('turn_id') or last['turn_id'] == m['turn_id']))
    ok = value.get('source', {}).get('state') == 'ok'
    rate = last.get('rate') if valid else None
    if not isinstance(rate, (float, int)) or not math.isfinite(rate) or rate <= 0:
        rate = None
    tier = level(rate, RATE_LIMITS)
    band = BANDS[tier]
    return dict(working=ok and m.get('stage') in ('generating', 'tools'),
                model=m.get('model') or last.get('model') or '',
                cache_percent=last.get('cache_percent'),
                last_input=last.get('input_tokens'), last_output=last.get('output_tokens'),
                cached_input=last.get('cached_input_tokens'), measured_at=last.get('measured_at_ms'),
                emitting=ok and m.get('stage') == 'generating' and rate is not None,
                idle=ok and m.get('stage') == 'idle',
                speed_tier=tier,
                lanes=LANES[tier], rate=rate,
                seconds=0 if rate is None else max(.55,4.4/(1+rate/40)),
                spacing=70 if rate is None else max(25,70-9*math.log2(1+rate/10)),
                band=band, run=str(m.get('turn_id') or start or ''),
                status='日志暂不可读' if not ok else '工作中 · 等待计数' if rate is None and m.get('stage') != 'idle' else '休息一下' if m.get('stage') == 'idle' else band)


class SceneFeed:
    """Selected-thread scene plus the latest local quota, with throttled scans."""

    def __init__(self, service, selection_path=None):
        self.service = service
        self.selection_path = selection_path
        self.quota_selection_path = selection_path.with_name('quota-selection.json') if selection_path else None
        self.current_chat_path = selection_path.with_name('current-chat.json') if selection_path else None
        self.quota = LocalQuota()
        self.rows = []
        self.quota_state = self.quota.update([])
        self.rows_at = self.quota_at = None  # None: never scanned

    def threads(self, now):
        if self.rows_at is None or now - self.rows_at >= THREAD_REFRESH_SECONDS:
            self.rows_at = now
            listing = self.service.get('threads', {})
            self.rows = sorted(listing['threads'], key=lambda r: r.get('updated_at_ms') or 0, reverse=True)
        return self.rows

    def quota_snapshot(self, now):
        if self.quota_at is None or now - self.quota_at >= QUOTA_REFRESH_SECONDS:
            self.quota_at = now
            recent = sorted(self.service.catalog.files.values(), key=lambda r: r['updated_at_ms'], reverse=True)[:24]
            self.quota_state = self.quota.update([r['path'] for r in recent])
        selection = read_selection(self.quota_selection_path).get('window_minutes', 0) if self.quota_selection_path else 0
        value = self.quota.snapshot(window_minutes=selection)
        return dict(value, bottles=bottles(value))

    def snapshot(self, thread_id=None, now=None):
        """Scene for thread_id, or for the saved/latest selection when None."""
        now = time.monotonic() if now is None else now
        state = dict(working=False, emitting=False, idle=True, lanes=0, seconds=0, band='', run='', status='未发现本地对话')
        rows, selected = [], ''
        preference = read_selection(self.selection_path) if self.selection_path else {}
        if thread_id is not None:
            preference = {'thread_id': thread_id}
        mode, choice = follow_preference(preference)
        try:
            rows = self.threads(now)
            if mode == 'current':
                observed = read_selection(self.current_chat_path) if self.current_chat_path else {}
                selected, notice = current_chat(rows, observed)
                if notice:
                    state['status'] = notice
            else:
                selected = choice if any(r['id'] == choice for r in rows) else rows[0]['id'] if rows and not choice else ''
            if mode == 'pinned' and not selected:
                state['status'] = '固定对话已不可读，请右键重新选择'
            if selected:
                state = scene(self.service.get('live', {'thread': [selected]}))
        except Exception as error:
            state['status'] = '读取暂停：' + str(error)
        state['following'] = {'pinned': '固定对话', 'current': '跟随当前对话', 'auto': '最新活动（可能是后台对话）'}[mode]
        state.update(self.quota_snapshot(now))
        state.update(updated=time.time(), selected=selected,
                     follow_mode=mode, pinned_thread_id=choice,
                     threads=[dict(id=r['id'], title=r['title']) for r in rows[:40]])
        return state


def migrate_layout(old: Path, new: Path):
    """Keep layout/selection from the project-local runtime used by earlier previews."""
    for name in ('layout.json', 'selection.json'):
        source, target = old / name, new / name
        if source.is_file() and not target.exists():
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(source.read_bytes())
