"""Build a source preview with an explicit public-file allowlist."""
from pathlib import Path
import hashlib
import zipfile


def main():
    root = Path(__file__).resolve().parent
    output = root / 'dist' / 'CrocToken-preview-source.zip'
    output.parent.mkdir(exist_ok=True)
    files = ['croc.py', 'StartCroc.cmd', 'dashboard.py', 'history.py', 'metrics.py',
             'LICENSE', 'CROC_README.md', 'web/croc.html', 'web/croc.css',
             'web/croc.js', 'web/croc-model.js', 'web/index.html', 'web/app.js', 'web/style.css']
    with zipfile.ZipFile(output, 'w', zipfile.ZIP_DEFLATED) as archive:
        for name in files:
            archive.write(root / name, 'CrocToken/' + name)
    output.with_suffix('.zip.sha256').write_text(hashlib.sha256(output.read_bytes()).hexdigest() + '  ' + output.name + '\n')
    print(output)


if __name__ == '__main__':
    main()
