"""Build the portable Codex character maker kit from the workbench allowlist."""
from pathlib import Path
import hashlib
import zipfile
from character_workbench import GENERATOR_FILES


def main():
    root=Path(__file__).resolve().parent
    output=root/'dist/Codex-character-maker-kit.zip'
    output.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as z:
        for name in GENERATOR_FILES: z.write(root/name,name)
    output.with_suffix('.zip.sha256').write_text(hashlib.sha256(output.read_bytes()).hexdigest()+'  '+output.name+'\n',encoding='utf-8')
    print(output)

if __name__=='__main__':main()
