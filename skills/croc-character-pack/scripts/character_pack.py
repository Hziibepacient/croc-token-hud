"""Versioned, inert character packages. Pillow is needed only for image operations."""
from __future__ import annotations
import argparse
from contextlib import contextmanager
import hashlib
import io
import json
import math
from pathlib import Path, PurePosixPath
import re
import shutil
import tempfile
import threading
import uuid
import zipfile

def atomic_json(path, value):
    import os
    temporary=path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp')
    try:
        temporary.write_text(json.dumps(value,ensure_ascii=False,allow_nan=False),encoding='utf-8')
        os.replace(temporary,path)
    finally:
        temporary.unlink(missing_ok=True)


POSES = ('rest', 'crouch', 'work', 'recoil', 'relief', 'think', 'drink', 'jump')
FRAME_POSES = ('crouch', 'recoil', 'relief', 'think', 'drink', 'jump')
MAX_ZIP = 32 * 1024 * 1024
MAX_TOTAL = 48 * 1024 * 1024
MAX_FILE = 8 * 1024 * 1024
ID = re.compile(r'[a-z0-9][a-z0-9-]{0,47}\Z')
RESERVED = {'con','prn','aux','nul', *(f'com{i}' for i in range(1,10)), *(f'lpt{i}' for i in range(1,10))}
ALLOWED = {'character.json', 'preview.png', *('poses/' + p + '.png' for p in POSES)}


def pillow():
    try:
        from PIL import Image
        return Image
    except ImportError as exc:
        raise ValueError('角色导入需要 Pillow，请运行 python -m pip install -r requirements-croc.txt') from exc


def point(value, name):
    if not isinstance(value, list) or len(value) != 2 or any(isinstance(n, bool) or not isinstance(n, (int, float)) or not math.isfinite(n) or not 0 <= n < 1024 for n in value):
        raise ValueError(name + ' 必须是画布内的两个坐标')
    return value


def png(data, label, square=True):
    Image = pillow()
    try:
        with Image.open(io.BytesIO(data)) as im:
            if im.format != 'PNG' or im.width > 2048 or im.height > 2048:
                raise ValueError(label + ' 必须是有效 PNG，最长边不超过 2048')
            if square and im.size != (1024, 1024):
                raise ValueError(label + ' 必须是 1024×1024')
            if getattr(im, 'n_frames', 1) != 1:
                raise ValueError(label + ' 不接受动态 PNG')
            im.load()
            out = im.convert('RGBA')
    except (OSError, SyntaxError, pillow().DecompressionBombError) as exc:
        raise ValueError(label + ' 图片损坏') from exc
    if square:
        alpha = out.getchannel('A')
        bbox = alpha.getbbox()
        if not bbox or alpha.getextrema()[0] != 0 or bbox[0] < 4 or bbox[1] < 4 or bbox[2] > 1020 or bbox[3] > 1020:
            raise ValueError(label + ' 需要真实透明背景、完整角色以及至少 4 像素边距')
        if sum(alpha.histogram()[1:]) < 10000:
            raise ValueError(label + ' 角色过小或为空')
    return out


def nearby(im, xy, radius=64):
    x,y=map(int,xy)
    return im.getchannel('A').crop((max(0,x-radius),max(0,y-radius),min(1024,x+radius+1),min(1024,y+radius+1))).getextrema()[1] > 32


def validate(data):
    if not data or len(data) > MAX_ZIP:
        raise ValueError('ZIP 不能为空，且不能超过 32 MB')
    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
        with archive:
            entries = archive.infolist()
            if len(entries) != len(ALLOWED) or len({e.filename for e in entries}) != len(entries):
                raise ValueError('角色包只能包含清单、预览和八张动作图，不允许重复或多余文件')
            if {e.filename for e in entries} != ALLOWED:
                raise ValueError('角色包缺少文件或路径不符合规范，请将 character.json 等文件放在 ZIP 根目录')
            if sum(e.file_size for e in entries) > MAX_TOTAL:
                raise ValueError('解压后超过 48 MB')
            for e in entries:
                if e.file_size > MAX_FILE or e.flag_bits & 1 or ((e.external_attr >> 16) & 0o170000) == 0o120000:
                    raise ValueError('不接受超大文件、加密文件或符号链接')
            files = {e.filename: archive.read(e) for e in entries}
    except (zipfile.BadZipFile, RuntimeError, EOFError, NotImplementedError) as exc:
        raise ValueError('ZIP 损坏或格式不支持') from exc
    if len(files['character.json']) > 32768:
        raise ValueError('角色配置过大')
    try:
        manifest = json.loads(files['character.json'].decode('utf-8-sig'))
    except (ValueError, UnicodeError) as exc:
        raise ValueError('character.json 不是有效 UTF-8 JSON') from exc
    if not isinstance(manifest, dict) or type(manifest.get('version')) is not int or manifest['version'] != 1 or manifest.get('canvas') != [1024,1024]:
        raise ValueError('仅支持 version=1、canvas=[1024,1024]')
    if not isinstance(manifest.get('id'),str) or not ID.fullmatch(manifest['id']) or manifest['id'] == 'builtin-croc' or manifest['id'] in RESERVED:
        raise ValueError('角色 id 须为 1–48 位小写字母、数字或短横线，且不能占用原鳄鱼 id')
    name=manifest.get('name')
    if not isinstance(name,str) or not name.strip() or len(name)>40 or any(ord(c)<32 for c in name):
        raise ValueError('角色名称须为 1–40 个可见字符')
    if type(manifest.get('pixel_art', False)) is not bool:
        raise ValueError('pixel_art 必须是布尔值')
    if manifest.get('poses') != {p:'poses/'+p+'.png' for p in POSES}:
        raise ValueError('八个动作路径不符合规范')
    anchors=manifest.get('anchors')
    if not isinstance(anchors,dict) or set(anchors)!=set(POSES):
        raise ValueError('八个动作都必须记录 feet 和 outlet 坐标')
    images={}
    for pose in POSES:
        image=png(files['poses/'+pose+'.png'],pose)
        a=anchors[pose]
        if not isinstance(a,dict):
            raise ValueError(pose+' 定位配置无效')
        feet=point(a.get('feet'),pose+'.feet')
        outlet=point(a.get('outlet'),pose+'.outlet')
        if not nearby(image,feet) or not nearby(image,outlet):
            raise ValueError(pose+' 的脚底或喷射点附近没有角色，请检查配置')
        # Ground registration is deterministic, not left to the image model.
        target=(604,907 if pose=='jump' else 973)
        dx,dy=target[0]-feet[0],target[1]-feet[1]
        box=image.getbbox()
        if box[0]+dx<4 or box[1]+dy<4 or box[2]+dx>1020 or box[3]+dy>1020:
            raise ValueError(pose+' 对齐后会裁掉身体，请先缩放整理图片')
        images[pose]=image
    if len({hashlib.sha256(files['poses/'+p+'.png']).digest() for p in POSES}) != len(POSES):
        raise ValueError('八个动作不能使用完全相同的图片文件，请生成独立动作')
    png(files['preview.png'],'preview',False)
    return manifest, files, images


def compile_assets(folder, manifest, images):
    Image=pillow()
    target=folder/'desktop'
    target.mkdir()
    frames={};outlets={}
    for pose,image in images.items():
        a=manifest['anchors'][pose]
        dx=604-a['feet'][0];dy=(907 if pose=='jump' else 973)-a['feet'][1]
        normalized=Image.new('RGBA',(1024,1024))
        normalized.alpha_composite(image,(round(dx),round(dy)))
        frames[pose]=normalized.resize((416,416),Image.Resampling.NEAREST if manifest.get('pixel_art',False) else Image.Resampling.LANCZOS)
        outlets[pose]=[(a['outlet'][0]+dx)/1024,(a['outlet'][1]+dy)/1024]
    for p in ('rest','work'):
        frames[p].save(target/(p+'.png'))
    strip=Image.new('RGBA',(416*6,416))
    for i,p in enumerate(FRAME_POSES):
        strip.paste(frames[p],(416*i,0))
    strip.save(target/'poses.png')
    atomic_json(target/'render.json',{'outlets':outlets,'pixel_art':manifest.get('pixel_art',False)})


class CharacterStore:
    def __init__(self,runtime):
        self.runtime=Path(runtime)
        self.root=self.runtime/'characters'
        self.root.mkdir(parents=True,exist_ok=True)
        self.lock=threading.RLock()
        self.drafts={}

    def current(self):
        try:
            value=json.loads((self.runtime/'character-selection.json').read_text(encoding='utf-8-sig'))['id']
            return value if ID.fullmatch(value) and (value=='builtin-croc' or (self.root/value/'desktop/render.json').is_file()) else 'builtin-croc'
        except (ValueError,OSError,KeyError,TypeError):
            return 'builtin-croc'

    def catalog(self):
        with self.lock:
            rows=[{'id':'builtin-croc','name':'原版鳄鱼'}]
            for p in sorted(self.root.iterdir()):
                if not p.is_dir() or not ID.fullmatch(p.name): continue
                try:
                    m=json.loads((p/'character.json').read_text(encoding='utf-8'))
                    if (p/'desktop/render.json').is_file(): rows.append({'id':p.name,'name':m['name']})
                except (OSError,ValueError,KeyError,TypeError): pass
            return {'characters':rows,'selected':self.current()}

    def select(self,character_id):
        with self.lock:
            if character_id not in {r['id'] for r in self.catalog()['characters']}:
                raise ValueError('角色不存在')
            atomic_json(self.runtime/'character-selection.json',{'id':character_id})
            return self.catalog()

    def expire(self):
        import time
        for token, (_,created) in list(self.drafts.items()):
            if time.monotonic()-created>1800:
                del self.drafts[token]

    def stage(self,data):
        import time
        manifest,_,_=validate(data)
        with self.lock:
            self.expire()
            if len(self.drafts)>=2: self.drafts.pop(next(iter(self.drafts)))
            token=uuid.uuid4().hex
            self.drafts[token]=(data,time.monotonic())
        return {'draft':token,'manifest':manifest,'notice':'已通过文件与坐标检查；请目视确认身份、动作和喷射位置。'}

    def draft_file(self,token,name):
        with self.lock:
            self.expire()
            if token not in self.drafts or name not in ALLOWED: raise ValueError('预览已过期或文件不存在')
            with zipfile.ZipFile(io.BytesIO(self.drafts[token][0])) as z: return z.read(name)

    def commit(self,token):
        with self.lock:
            self.expire()
            if token not in self.drafts: raise ValueError('预览已过期，请重新上传')
            manifest,files,images=validate(self.drafts[token][0])
            final=self.root/manifest['id']
            digest=hashlib.sha256(self.drafts[token][0]).hexdigest()
            if final.exists():
                if (final/'package.sha256').read_text()!=digest:
                    raise ValueError('已有相同 id 的不同角色包，请使用新的 id；不会覆盖旧角色')
                del self.drafts[token]
                return self.select(manifest['id'])
            temporary=Path(tempfile.mkdtemp(prefix='.import-',dir=self.root))
            try:
                for name,content in files.items():
                    path=temporary/name;path.parent.mkdir(parents=True,exist_ok=True);path.write_bytes(content)
                compile_assets(temporary,manifest,images)
                (temporary/'package.sha256').write_text(digest)
                temporary.rename(final)
            finally:
                if temporary.exists(): shutil.rmtree(temporary)
            del self.drafts[token]
            return self.select(manifest['id'])


def pack(folder,output):
    folder=Path(folder);output=Path(output)
    stream=io.BytesIO()
    with zipfile.ZipFile(stream,'w',zipfile.ZIP_DEFLATED) as z:
        for name in sorted(ALLOWED): z.writestr(name,(folder/name).read_bytes())
    validate(stream.getvalue())
    output.parent.mkdir(parents=True,exist_ok=True)
    output.write_bytes(stream.getvalue())
    return output


def main():
    p=argparse.ArgumentParser(description=__doc__)
    sub=p.add_subparsers(dest='command',required=True)
    a=sub.add_parser('validate');a.add_argument('zip',type=Path)
    a=sub.add_parser('pack');a.add_argument('folder',type=Path);a.add_argument('output',type=Path)
    args=p.parse_args()
    if args.command=='validate':
        m,_,_=validate(args.zip.read_bytes());print('合格文件结构：'+m['name']+'；仍需目视检查动作和位置。')
    else: print(pack(args.folder,args.output))

if __name__=='__main__':
    main()
