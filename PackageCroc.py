"""Build the crocodile desktop source preview using an explicit public allowlist."""
from pathlib import Path
import hashlib
import zipfile


def main():
    root = Path(__file__).resolve().parent
    output = root / 'dist' / 'CrocToken-preview-source.zip'
    output.parent.mkdir(exist_ok=True)
    files = ['PackageCroc.py', 'PackageCharacterKit.py', 'docs/releases/croc-v1.4.0-beta.1.md', 'README.md', 'README.zh-CN.md', 'VERSION', 'docs/images/croc-desktop.png', 'character_pack.py', 'character_workbench.py', 'requirements-croc.txt', 'BuildCharacterGuide.py',
             'docs/character-pack-v1.md', 'docs/Codex角色包生成指令.md',
             'web/characters.html', 'web/characters.js', 'web/characters.css',
             'skills/croc-character-pack/SKILL.md', 'skills/croc-character-pack/references/generation.md',
             'skills/croc-character-pack/references/character-pack-v1.md', 'skills/croc-character-pack/scripts/character_pack.py', 'skills/croc-character-pack/scripts/requirements-croc.txt',
             'croc.py', 'croc_desktop.py', 'local_quota.py', 'CrocDesktop.cs',
             'CrocCurrentChat.cs', 'CodexTokenHud.cs', 'RuntimeConfiguration.cs',
             'StartCroc.cmd', 'StartCrocDesktop.cmd', 'dashboard.py', 'history.py', 'metrics.py',
             'LICENSE', 'CROC_README.md', 'CROC_RELEASE_NOTES.md', 'web/croc.html', 'web/croc.css',
             'web/croc.js', 'croc_scene.py', 'web/index.html', 'web/app.js', 'web/style.css',
             'web/assets/croc-rest.png', 'web/assets/croc-work.png',
             'web/assets/bottle.png', 'web/assets/poop.png', 'BuildCrocSprites.py',
             'web/assets/desktop/rest.png', 'web/assets/desktop/work.png', 'web/assets/desktop/poses.png',
             'web/assets/desktop/bottle.png', 'web/assets/desktop/bottle-empty.png']
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
