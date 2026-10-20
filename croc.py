"""Launch the crocodile view using upstream's read-only local dashboard."""
from pathlib import Path
import argparse
import os
import time
import webbrowser

from dashboard import start_server
from metrics import atomic_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--no-browser', action='store_true')
    parser.add_argument('--codex-home', type=Path, default=Path(os.environ.get('CODEX_HOME') or Path.home() / '.codex'))
    args = parser.parse_args()
    runtime = Path(__file__).resolve().parent / 'runtime' / 'croc'
    server = start_server(args.codex_home, runtime)
    url = server.url().replace('/#', '/croc#')
    atomic_json(runtime / 'croc.json', {'url': url, 'pid': os.getpid()})
    print('Croc Token: ' + url, flush=True)
    if not args.no_browser:
        webbrowser.open(url)
    try:
        while True:
            time.sleep(0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.shutdown()
        server.server_close()


if __name__ == '__main__':
    main()
