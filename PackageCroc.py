"""Build the crocodile desktop source preview using an explicit public allowlist."""
from pathlib import Path
import hashlib
import zipfile


def main():
    root = Path(__file__).resolve().parent
    output = root / 'dist' / 'CrocToken-preview-source.zip'
    output.parent.mkdir(exist_ok=True)
    files = ['croc.py', 'croc_desktop.py', 'local_quota.py', 'CrocDesktop.cs',
             'StartCroc.cmd', 'StartCrocDesktop.cmd', 'dashboard.py', 'history.py', 'metrics.py',
             'LICENSE', 'CROC_README.md', 'CROC_RELEASE_NOTES.md', 'web/croc.html', 'web/croc.css',
             'web/croc.js', 'web/croc-model.js', 'web/index.html', 'web/app.js', 'web/style.css',
             'web/assets/croc-rest.png', 'web/assets/croc-work.png',
             'web/assets/bottle.png', 'web/assets/poop.png']
    for name in files:
        if not (root / name).is_file():
            raise FileNotFoundError('Public release file missing: ' + name)
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
        for name in files:
            archive.write(root / name, 'CrocToken/' + name)
    output.with_suffix('.zip.sha256').write_text(hashlib.sha256(output.read_bytes()).hexdigest() + '  ' + output.name + '\n')
    print(output)


if __name__ == '__main__':
    main()
