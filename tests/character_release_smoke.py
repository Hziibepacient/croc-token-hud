"""Verify native Croc windows and the actual extracted source release, using synthetic data."""
from pathlib import Path
import json
import os
import subprocess
import sys
import zipfile
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from character_pack import CharacterStore,pack
from test_character_pack import fixture


def compile_desktop(folder,compiler):
    (folder/'build').mkdir(exist_ok=True)
    subprocess.run([str(compiler),'/nologo','/target:winexe','/optimize+','/out:build/CrocDesktop-release.exe',
                    '/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Web.Extensions.dll','CrocDesktop.cs'],cwd=folder,check=True)
    return folder/'build/CrocDesktop-release.exe'


def smoke(exe,root,runtime,custom):
    runtime.mkdir(parents=True,exist_ok=True)
    subprocess.run([str(exe),str(root),str(runtime),'--demo','--smoke'],check=True,timeout=40)
    result=json.loads((runtime/'smoke-result.json').read_text(encoding='utf-8-sig'))
    if result.get('error'):raise RuntimeError(result['error'])
    assert result.get('characterMenu') and result.get('poopMotion') and result.get('bottleCountPreserved'),result
    assert result.get('customCharacter') is custom,result
    print('Native smoke passed:', 'imported character' if custom else 'original crocodile')


def main():
    compiler=Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    base=ROOT/'build/croc-release-verify';base.mkdir(parents=True,exist_ok=True)
    exe=compile_desktop(ROOT,compiler)
    smoke(exe,ROOT,base/'original',False)
    source=base/'fixture';fixture(source,'release-test-shape')
    data=pack(source,base/'fixture.zip').read_bytes()
    store=CharacterStore(base/'custom');store.commit(store.stage(data)['draft'])
    smoke(exe,ROOT,base/'custom',True)
    with zipfile.ZipFile(ROOT/'dist/CrocToken-preview-source.zip') as z:
        assert z.testzip() is None
        assert all(not any(p in name.split('/') for p in ('runtime','auth.json','selection.json','.git')) for name in z.namelist())
        z.extractall(base/'package')
    package=base/'package/CrocToken'
    exe=compile_desktop(package,compiler)
    subprocess.run([sys.executable,'-c',"from pathlib import Path; from croc_desktop import build_current_reader; build_current_reader(Path.cwd(),Path(r'"+str(compiler)+"'))"],cwd=package,check=True)
    subprocess.run([sys.executable,'BuildCharacterGuide.py','--check'],cwd=package,check=True)
    smoke(exe,package,base/'extracted',False)
    with zipfile.ZipFile(ROOT/'dist/Codex-character-maker-kit.zip') as z:assert z.testzip() is None
    print('Extracted release sources, helper compiler, guide and maker kit verified.')

if __name__=='__main__':main()
