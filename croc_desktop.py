"""Windows desktop launcher and read-only telemetry bridge."""
from pathlib import Path
import argparse
import math
import os
import subprocess
import time

from dashboard import Dashboard
from metrics import atomic_json, read_selection
from local_quota import LocalQuota


def level(value, limits):
    if not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        return 0
    return 1 + sum(value >= limit for limit in limits)


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
    tier = level(rate, [15, 30, 60, 100])
    band = ['', '慢悠悠', '轻快', '忙碌', '飞快', '全速'][tier]
    return dict(working=ok and m.get('stage') in ('generating', 'tools'),
                model=m.get('model') or last.get('model') or '',
                cache_percent=last.get('cache_percent'),
                last_input=last.get('input_tokens'), last_output=last.get('output_tokens'),
                cached_input=last.get('cached_input_tokens'), measured_at=last.get('measured_at_ms'),
                emitting=ok and m.get('stage') == 'generating' and rate is not None,
                idle=ok and m.get('stage') == 'idle',
                speed_tier=tier,
                lanes=[0, 1, 1, 2, 2, 3][tier], rate=rate,
                seconds=0 if rate is None else max(.55,4.4/(1+rate/40)),
                spacing=70 if rate is None else max(25,70-9*math.log2(1+rate/10)),
                band=band, run=str(m.get('turn_id') or start or ''),
                status='日志暂不可读' if not ok else '工作中 · 等待计数' if rate is None and m.get('stage') != 'idle' else '休息一下' if m.get('stage') == 'idle' else band)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--codex-home', type=Path, default=Path(os.environ.get('CODEX_HOME') or Path.home() / '.codex'))
    parser.add_argument('--demo', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    runtime = root / 'runtime' / 'desktop'
    runtime.mkdir(parents=True, exist_ok=True)
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if not compiler.exists():
        compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
    exe = root / 'build' / 'CrocDesktop.exe'
    source = root / 'CrocDesktop.cs'
    exe.parent.mkdir(exist_ok=True)
    if not exe.exists() or source.stat().st_mtime > exe.stat().st_mtime:
        subprocess.run([str(compiler), '/nologo', '/target:winexe', '/optimize+', '/out:' + str(exe),
                        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', str(source)], check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    service = Dashboard(args.codex_home, runtime)
    quota = LocalQuota()
    process = subprocess.Popen([str(exe), str(root), str(runtime)] + (['--demo'] if args.demo else []))
    try:
        while process.poll() is None:
            state = dict(working=False, emitting=False, idle=True, bottles=0, lanes=0, seconds=0, band='', run='', status='未发现本地对话')
            rows = []
            selected = ''
            try:
                listing = service.get('threads', {})
                rows = sorted(listing['threads'], key=lambda r: r.get('updated_at_ms') or 0, reverse=True)
                choice = read_selection(runtime / 'selection.json').get('thread_id', '')
                selected = choice if any(r['id'] == choice for r in rows) else rows[0]['id'] if rows else ''
                if choice and selected != choice:
                    selected = ''
                    state['status'] = '固定对话已不可读，请右键重新选择'
                if selected:
                    state = scene(service.get('live', {'thread': [selected]}))
                state['following'] = '固定对话' if choice else '最新活动（可能是后台对话）'
            except Exception as error:
                state['status'] = '读取暂停：' + str(error)
            recent = sorted(service.catalog.files.values(), key=lambda r: r['updated_at_ms'], reverse=True)[:24]
            state.update(quota.update([r['path'] for r in recent]))
            state.update(updated=time.time(), selected=selected, threads=[dict(id=r['id'], title=r['title']) for r in rows[:40]])
            try:
                atomic_json(runtime / 'scene.json', state)
            except OSError:
                pass  # A brief Windows file-sharing collision is retried next poll.
            time.sleep(.1)
    finally:
        if process.poll() is None:
            process.terminate()


if __name__ == '__main__':
    try:
        main()
    except Exception:
        import traceback
        import ctypes
        error_log = Path(__file__).resolve().parent / 'runtime' / 'desktop' / 'launcher-error.txt'
        error_log.parent.mkdir(parents=True, exist_ok=True)
        error_log.write_text(traceback.format_exc(), encoding='utf-8')
        ctypes.windll.user32.MessageBoxW(None, '启动失败，请查看项目 runtime/desktop/launcher-error.txt', '桌面鳄鱼', 0x10)
