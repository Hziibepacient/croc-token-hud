"""Windows desktop launcher and read-only telemetry bridge."""
from pathlib import Path
import argparse
import os
import subprocess
import time

from croc_scene import SceneFeed, migrate_layout
from dashboard import Dashboard, data_directory
from metrics import atomic_json
from character_workbench import start_workbench

HEARTBEAT_SECONDS = 1  # The window treats scenes older than 5 s as disconnected.


def build_current_reader(root, compiler):
    sources = [root / name for name in ('CrocCurrentChat.cs', 'CodexTokenHud.cs', 'RuntimeConfiguration.cs')]
    exe = root / 'build' / 'CrocCurrentChat.exe'
    if not exe.exists() or any(p.stat().st_mtime > exe.stat().st_mtime for p in sources):
        wpf = compiler.parent / 'WPF'
        references = ['/r:' + str(wpf / name) for name in ('UIAutomationClient.dll', 'UIAutomationTypes.dll', 'WindowsBase.dll')]
        subprocess.run([str(compiler), '/nologo', '/target:winexe', '/optimize+',
                        '/main:CodexTokenHud.CrocCurrentChat', '/out:build/CrocCurrentChat.exe',
                        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Web.Extensions.dll',
                        '/r:Microsoft.CSharp.dll', *references, *[p.name for p in sources]],
                       cwd=root, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    return exe


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
    workbench = start_workbench(runtime)
    written, written_at = None, 0
    current_reader, next_reader_attempt = None, 0
    try:
        while process.poll() is None:
            state = feed.snapshot()
            if state['follow_mode'] == 'current':
                if (current_reader is None or current_reader.poll() is not None) and time.monotonic() >= next_reader_attempt:
                    next_reader_attempt = time.monotonic() + 15
                    try:
                        atomic_json(runtime / 'current-chat.json', {'title': '', 'updated_at': 0})
                        reader_exe = build_current_reader(root, compiler)
                        current_reader = subprocess.Popen([str(reader_exe), str(runtime), str(os.getpid())],
                                                          cwd=root, creationflags=subprocess.CREATE_NO_WINDOW)
                    except (OSError, subprocess.SubprocessError):
                        state['status'] = '当前对话识别器不可用，请切换最新活动或固定对话'
            elif current_reader is not None:
                if current_reader.poll() is None:
                    current_reader.terminate()
                    current_reader.wait(timeout=3)
                current_reader, next_reader_attempt = None, 0
            content = {k: v for k, v in state.items() if k != 'updated'}
            if content != written or time.monotonic() - written_at >= HEARTBEAT_SECONDS:
                try:
                    atomic_json(runtime / 'scene.json', state)
                    written, written_at = content, time.monotonic()
                except OSError:
                    pass  # A brief Windows file-sharing collision is retried next poll.
            time.sleep(.1)
    finally:
        workbench.shutdown()
        workbench.server_close()
        if current_reader is not None and current_reader.poll() is None:
            current_reader.terminate()
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
