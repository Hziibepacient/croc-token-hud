"""Windows desktop launcher and read-only telemetry bridge."""
from pathlib import Path
import argparse
import os
import subprocess
import time

from croc_scene import SceneFeed, migrate_layout
from dashboard import Dashboard, data_directory
from metrics import atomic_json

HEARTBEAT_SECONDS = 1  # The window treats scenes older than 5 s as disconnected.


def runtime_directory(root: Path) -> Path:
    runtime = data_directory() / 'croc-desktop'
    migrate_layout(root / 'runtime' / 'desktop', runtime)
    runtime.mkdir(parents=True, exist_ok=True)
    return runtime


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--codex-home', type=Path, default=Path(os.environ.get('CODEX_HOME') or Path.home() / '.codex'))
    parser.add_argument('--demo', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    runtime = runtime_directory(root)
    compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if not compiler.exists():
        compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
    exe = root / 'build' / 'CrocDesktop.exe'
    source = root / 'CrocDesktop.cs'
    exe.parent.mkdir(exist_ok=True)
    if not exe.exists() or source.stat().st_mtime > exe.stat().st_mtime:
        subprocess.run([str(compiler), '/nologo', '/target:winexe', '/optimize+', '/out:' + str(exe),
                        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll', str(source)], check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    feed = SceneFeed(Dashboard(args.codex_home, runtime), runtime / 'selection.json')
    process = subprocess.Popen([str(exe), str(root), str(runtime)] + (['--demo'] if args.demo else []))
    written, written_at = None, 0
    try:
        while process.poll() is None:
            state = feed.snapshot()
            content = {k: v for k, v in state.items() if k != 'updated'}
            if content != written or time.monotonic() - written_at >= HEARTBEAT_SECONDS:
                try:
                    atomic_json(runtime / 'scene.json', state)
                    written, written_at = content, time.monotonic()
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
        error_log = data_directory() / 'croc-desktop' / 'launcher-error.txt'
        error_log.parent.mkdir(parents=True, exist_ok=True)
        error_log.write_text(traceback.format_exc(), encoding='utf-8')
        ctypes.windll.user32.MessageBoxW(None, '启动失败，请查看 ' + str(error_log), '桌面鳄鱼', 0x10)
