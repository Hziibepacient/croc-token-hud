"""Loopback-only character workbench; shares the dashboard's request protections."""
from __future__ import annotations
import io
import json
from pathlib import Path
import secrets
import threading
from urllib.parse import parse_qs, urlsplit
import zipfile
from http.server import ThreadingHTTPServer

from character_pack import CharacterStore, MAX_ZIP, ALLOWED, png
from dashboard import Handler
from metrics import atomic_json

WEB=Path(__file__).parent/'web'
GENERATOR_FILES=('character_pack.py','requirements-croc.txt','docs/character-pack-v1.md','docs/Codex角色包生成指令.md',
    'skills/croc-character-pack/SKILL.md','skills/croc-character-pack/references/generation.md',
    'skills/croc-character-pack/references/character-pack-v1.md','skills/croc-character-pack/scripts/character_pack.py',
    'skills/croc-character-pack/scripts/requirements-croc.txt')

def preview_png(data,side,pixel_art):
    """Serve bounded decoded surfaces; never send full editing images for UI previews."""
    from character_pack import pillow
    Image=pillow()
    image=png(data,'preview',False)
    try:
        image.thumbnail((side,side),Image.Resampling.NEAREST if pixel_art else Image.Resampling.LANCZOS)
        buffer=io.BytesIO();image.save(buffer,format='PNG');return buffer.getvalue()
    finally:
        image.close()


class CharacterHandler(Handler):
    def do_GET(self):
        url=urlsplit(self.path)
        if not self.allowed(api=url.path.startswith('/api/')): return
        try:
            store=self.server.store
            if url.path=='/api/characters': self.send(200,store.catalog());return
            if url.path=='/api/manifest':
                cid=parse_qs(url.query).get('id',[''])[0]
                if cid=='builtin-croc' or cid not in {r['id'] for r in store.catalog()['characters']}: raise ValueError('无效角色')
                self.send(200,json.loads((store.root/cid/'character.json').read_text(encoding='utf-8')));return
            if url.path=='/api/asset':
                q=parse_qs(url.query);name=q.get('file',[''])[0]
                if name not in ALLOWED or not name.endswith('.png'): raise ValueError('无效图片')
                if q.get('draft'):
                    data=store.draft_file(q['draft'][0],name)
                else:
                    cid=q.get('id',[''])[0]
                    if cid not in {r['id'] for r in store.catalog()['characters']} or cid=='builtin-croc': raise ValueError('无效角色')
                    data=(store.root/cid/name).read_bytes()
                if 'size' in q:
                    try: side=int(q['size'][0])
                    except (ValueError,IndexError): raise ValueError('预览尺寸无效')
                    if side not in (96,256,416): raise ValueError('预览仅支持 96、256 或 416 像素')
                    if q.get('draft'):
                        config=json.loads(store.draft_file(q['draft'][0],'character.json').decode('utf-8-sig'))
                    else:
                        config=json.loads((store.root/cid/'character.json').read_text(encoding='utf-8-sig'))
                    data=preview_png(data,side,config.get('pixel_art',False) is True)
                self.send(200,data,'image/png');return
            if url.path=='/api/generator-kit':
                root=WEB.parent;stream=io.BytesIO()
                names=GENERATOR_FILES
                with zipfile.ZipFile(stream,'w',zipfile.ZIP_DEFLATED) as z:
                    for name in names: z.write(root/name,name)
                self.send(200,stream.getvalue(),'application/zip');return
            if url.path=='/api/prompt':
                self.send(200,(WEB.parent/'docs/Codex角色包生成指令.md').read_bytes(),'text/plain; charset=utf-8');return
            static={'/':('characters.html','text/html; charset=utf-8'),'/characters.js':('characters.js','text/javascript; charset=utf-8'),'/characters.css':('characters.css','text/css; charset=utf-8'),'/builtin.png':('assets/croc-rest.png','image/png'),'/bottle.png':('assets/bottle.png','image/png'),'/poop.png':('assets/poop.png','image/png')}
            if url.path not in static: self.send(404,{'error':'未找到页面'});return
            name,mime=static[url.path];self.send(200,(WEB/name).read_bytes(),mime)
        except (ValueError,OSError,KeyError,TypeError) as exc: self.send(400,{'error':str(exc)})

    def do_POST(self):
        if not self.allowed(api=True): return
        try:
            path=urlsplit(self.path).path
            size=int(self.headers.get('Content-Length','0'))
            limit=MAX_ZIP if path=='/api/import' else 32768
            if not 0<size<=limit: raise ValueError('请求过大或为空')
            data=self.rfile.read(size)
            if len(data)!=size: raise ValueError('上传不完整')
            store=self.server.store
            if path=='/api/import': result=store.stage(data)
            else:
                body=json.loads(data)
                if not isinstance(body,dict): raise ValueError('请求格式错误')
                if path=='/api/commit': result=store.commit(body.get('draft',''),body.get('name'))
                elif path=='/api/select': result=store.select(body.get('id',''))
                elif path=='/api/rename': result=store.rename(body.get('id',''),body.get('name'))
                else: raise ValueError('未知接口')
            self.send(200,result)
        except (ValueError,OSError,KeyError,TypeError) as exc: self.send(400,{'error':str(exc)})

class CharacterServer(ThreadingHTTPServer):
    daemon_threads=True
    allow_reuse_address=False
    def __init__(self,runtime,port=0):
        self.store=CharacterStore(runtime)
        self.token=secrets.token_urlsafe(32)
        super().__init__(('127.0.0.1',port),CharacterHandler)
        self.origin='http://127.0.0.1:'+str(self.server_port)
    def url(self): return self.origin+'/#token='+self.token


def start_workbench(runtime,port=0):
    server=CharacterServer(runtime,port)
    threading.Thread(target=server.serve_forever,name='character-workbench',daemon=True).start()
    atomic_json(Path(runtime)/'workbench.json',{'url':server.url()})
    return server
