"""Character packages: integrity, transactional import, HTTP access, and contract drift."""
import io
import json
from pathlib import Path
import tempfile
import unittest
import urllib.request
import urllib.error
import zipfile

try:
    from PIL import Image, ImageDraw
except ImportError:
    Image=None
from character_pack import POSES, CharacterStore, pack, validate, MAX_ZIP
from character_workbench import start_workbench
from BuildCharacterGuide import outputs


def fixture(folder, character_id='test-shape'):
    folder=Path(folder);(folder/'poses').mkdir(parents=True,exist_ok=True)
    anchors={}
    for i,p in enumerate(POSES):
        im=Image.new('RGBA',(1024,1024));d=ImageDraw.Draw(im)
        # Different colors are intentional fixtures, not a deliverable character.
        ground=907 if p=='jump' else 973
        d.ellipse((300,ground-650,810,ground),fill=(120+i*10,180,100,255))
        im.save(folder/'poses'/f'{p}.png')
        anchors[p]={'feet':[604,ground],'outlet':[795,ground-235]}
    im.resize((256,256)).save(folder/'preview.png')
    m={'version':1,'id':character_id,'name':'测试角色','canvas':[1024,1024],
       'poses':{p:f'poses/{p}.png' for p in POSES},'anchors':anchors}
    (folder/'character.json').write_text(json.dumps(m),encoding='utf-8')
    return m


def mutate(data,change):
    with zipfile.ZipFile(io.BytesIO(data)) as z: files={n:z.read(n) for n in z.namelist()}
    change(files)
    out=io.BytesIO()
    with zipfile.ZipFile(out,'w',zipfile.ZIP_DEFLATED) as z:
        for n,v in files.items(): z.writestr(n,v)
    return out.getvalue()


class GuideTests(unittest.TestCase):
    def test_generated_guides_and_skill_share_contract(self):
        root=Path(__file__).resolve().parents[1]
        for name,content in outputs().items(): self.assertEqual((root/name).read_text(encoding='utf-8-sig'),content,name)


@unittest.skipIf(Image is None,'角色图像检查需要 Pillow')
class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);fixture(self.root/'source')
        self.data=pack(self.root/'source',self.root/'cat.zip').read_bytes()
        self.store=CharacterStore(self.root/'runtime')

    def test_validate_and_compile_all_eight(self):
        m,_,images=validate(self.data);self.assertEqual(len(images),8)
        staged=self.store.stage(self.data)
        self.assertEqual(self.store.current(),'builtin-croc')
        result=self.store.commit(staged['draft'])
        self.assertEqual(result['selected'],'test-shape')
        with Image.open(self.store.root/'test-shape/desktop/poses.png') as im:self.assertEqual(im.size,(2496,416))
        render=json.loads((self.store.root/'test-shape/desktop/render.json').read_text())
        self.assertEqual(set(render['outlets']),set(POSES))
        self.assertAlmostEqual(render['outlets']['work'][0],795/1024)
        self.assertEqual(CharacterStore(self.root/'runtime').current(),'test-shape')

    def test_duplicate_identical_is_idempotent_conflicting_rejected(self):
        self.store.commit(self.store.stage(self.data)['draft'])
        self.store.commit(self.store.stage(self.data)['draft'])
        def alter(files):
            m=json.loads(files['character.json']);m['name']='不同版本';files['character.json']=json.dumps(m).encode()
        changed=mutate(self.data,alter)
        with self.assertRaisesRegex(ValueError,'相同 id'):self.store.commit(self.store.stage(changed)['draft'])
        self.assertEqual(self.store.current(),'test-shape')

    def test_missing_extra_traversal_corrupt_and_huge(self):
        variants=[b'broken',self.data[:30],mutate(self.data,lambda f:f.pop('poses/jump.png')),
                  mutate(self.data,lambda f:f.update({'../escape.py':b'pass'})),
                  mutate(self.data,lambda f:f.update({'run.exe':b'bad'})),b'x'*(MAX_ZIP+1)]
        for data in variants:
            with self.subTest(length=len(data)),self.assertRaises(ValueError):self.store.stage(data)
        self.assertEqual(self.store.current(),'builtin-croc')
        self.assertFalse((self.root/'escape.py').exists())

    def test_custom_name_rename_and_reimport_persist_without_changing_art(self):
        result=self.store.commit(self.store.stage(self.data)['draft'],'  我的伙伴  ')
        self.assertEqual(result['characters'][1]['name'],'我的伙伴')
        folder=self.store.root/'test-shape'
        before={str(p.relative_to(folder)):p.read_bytes() for p in folder.rglob('*') if p.is_file() and p.name!='character.json'}
        self.store.select('builtin-croc')
        result=self.store.rename('test-shape','ANS小鲨宾')
        self.assertEqual(result['selected'],'builtin-croc')
        self.assertEqual(result['characters'][1]['name'],'ANS小鲨宾')
        self.assertEqual(before,{str(p.relative_to(folder)):p.read_bytes() for p in folder.rglob('*') if p.is_file() and p.name!='character.json'})
        self.store=CharacterStore(self.root/'runtime')
        self.assertEqual(self.store.catalog()['characters'][1]['name'],'ANS小鲨宾')
        result=self.store.commit(self.store.stage(self.data)['draft'])
        self.assertEqual(result['characters'][1]['name'],'ANS小鲨宾')
        result=self.store.commit(self.store.stage(self.data)['draft'],'新名字')
        self.assertEqual(result['characters'][1]['name'],'新名字')

    def test_invalid_names_leave_install_and_existing_character_unchanged(self):
        draft=self.store.stage(self.data)['draft']
        for name in ['', '   ', '长'*41, 'bad\nname', 'bad\x7fname', 123]:
            with self.subTest(name=name),self.assertRaises(ValueError):self.store.commit(draft,name)
            self.assertFalse((self.store.root/'test-shape').exists())
        self.store.commit(draft,'保留名字')
        for name in ['', '长'*41, None, 'bad\tname']:
            with self.subTest(name=name),self.assertRaises(ValueError):self.store.rename('test-shape',name)
        self.assertEqual(self.store.catalog()['characters'][1]['name'],'保留名字')
        with self.assertRaises(ValueError):self.store.rename('../outside','名字')
        with self.assertRaises(ValueError):self.store.rename('builtin-croc','名字')

    def test_copied_pose_rejected(self):
        data=mutate(self.data,lambda f:f.update({'poses/think.png':f['poses/rest.png']}))
        with self.assertRaisesRegex(ValueError,'完全相同'):validate(data)

    def test_duplicate_zip_entries_rejected(self):
        source=io.BytesIO(self.data)
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter('ignore')
            with zipfile.ZipFile(source,'a') as z: z.writestr('character.json',b'{}')
        with self.assertRaises(ValueError): validate(source.getvalue())

    def test_pixel_art_and_noncanonical_anchor_registration(self):
        def alter(files):
            m=json.loads(files['character.json']);m['pixel_art']=True
            for p in POSES:
                m['anchors'][p]['feet'][0]=600
            files['character.json']=json.dumps(m).encode()
        data=mutate(self.data,alter)
        self.store.commit(self.store.stage(data)['draft'])
        render=json.loads((self.store.root/'test-shape/desktop/render.json').read_text())
        self.assertTrue(render['pixel_art'])
        self.assertAlmostEqual(render['outlets']['work'][0],799/1024)
        with Image.open(self.store.root/'test-shape/desktop/work.png') as im:
            self.assertEqual(sum(im.getchannel('A').histogram()[1:255]),0)

    def test_invalid_manifest_and_empty_opaque_images(self):
        for change in [lambda m:m.update(id='../oops'),lambda m:m.update(id='con'),lambda m:m.update(version=2),
                       lambda m:m['anchors']['work'].update(outlet=[float('nan'),738]),
                       lambda m:m['anchors']['work'].update(outlet=[20,20]),
                       lambda m:m['anchors']['rest'].update(feet=[604,100]),
                       lambda m:m['poses'].update(rest='../outside.png')]:
            def alter(files):
                m=json.loads(files['character.json']);change(m);files['character.json']=json.dumps(m).encode()
            with self.assertRaises(ValueError):validate(mutate(self.data,alter))
        for color,size in [((255,255,255,255),(1024,1024)),((0,0,0,0),(1024,1024)),((0,0,0,0),(64,64))]:
            b=io.BytesIO();Image.new('RGBA',size,color).save(b,format='PNG')
            with self.assertRaises(ValueError):validate(mutate(self.data,lambda f:f.update({'poses/rest.png':b.getvalue()})))

    def test_failed_compile_does_not_install_or_change_selection(self):
        from unittest.mock import patch
        staged=self.store.stage(self.data)
        with patch('character_pack.compile_assets',side_effect=OSError('disk full')):
            with self.assertRaises(OSError):self.store.commit(staged['draft'])
        self.assertEqual(self.store.current(),'builtin-croc')
        self.assertFalse((self.store.root/'test-shape').exists())
        self.assertEqual(list(self.store.root.iterdir()),[])

    def test_select_unknown_and_expired_draft(self):
        with self.assertRaises(ValueError):self.store.select('missing')
        import time
        staged=self.store.stage(self.data);self.store.drafts[staged['draft']]=(self.data,time.monotonic()-1801)
        with self.assertRaises(ValueError):self.store.commit(staged['draft'])

    def test_http_import_preview_commit_and_security(self):
        server=start_workbench(self.root/'http-runtime')
        self.addCleanup(server.server_close);self.addCleanup(server.shutdown)
        def request(path,body=None,headers=None):
            return urllib.request.urlopen(urllib.request.Request(server.origin+path,data=body,headers={'X-Hud-Token':server.token} if headers is None else headers),timeout=10)
        with self.assertRaises(urllib.error.HTTPError) as c:request('/api/characters',headers={})
        self.assertEqual(c.exception.code,403)
        with self.assertRaises(urllib.error.HTTPError) as c:request('/api/characters',headers={'X-Hud-Token':server.token,'Origin':'https://evil.test'})
        self.assertEqual(c.exception.code,403)
        with request('/api/import',self.data) as r:staged=json.load(r)
        with request('/api/asset?draft='+staged['draft']+'&file=poses/work.png') as r:self.assertTrue(r.read().startswith(b'\x89PNG'))
        with request('/api/asset?draft='+staged['draft']+'&file=poses/work.png&size=416') as r:
            with Image.open(io.BytesIO(r.read())) as im: self.assertEqual(im.size,(416,416))
        with self.assertRaises(urllib.error.HTTPError):request('/api/asset?draft='+staged['draft']+'&file=poses/work.png&size=4096')
        with request('/api/commit',json.dumps({'draft':staged['draft'],'name':'上传后起名'}).encode()) as r:
            result=json.load(r);self.assertEqual(result['selected'],'test-shape')
            self.assertEqual(result['characters'][1]['name'],'上传后起名')
        with request('/api/asset?id=test-shape&file=poses/rest.png&size=96') as r:
            with Image.open(io.BytesIO(r.read())) as im:self.assertEqual(im.size,(96,96))
        with request('/api/select',json.dumps({'id':'builtin-croc'}).encode()) as r:self.assertEqual(json.load(r)['selected'],'builtin-croc')
        with request('/api/rename',json.dumps({'id':'test-shape','name':'改名成功'}).encode()) as r:
            result=json.load(r);self.assertEqual(result['selected'],'builtin-croc')
            self.assertEqual(result['characters'][1]['name'],'改名成功')
        with request('/api/manifest?id=test-shape') as r:self.assertEqual(json.load(r)['name'],'改名成功')
        with self.assertRaises(urllib.error.HTTPError):request('/api/rename',json.dumps({'id':'test-shape','name':''}).encode())
        with request('/api/generator-kit') as r:
            with zipfile.ZipFile(io.BytesIO(r.read())) as z:
                self.assertIsNone(z.testzip());self.assertIn('character_pack.py',z.namelist())
        with request('/') as r:self.assertIn(b'characters.js',r.read())

if __name__=='__main__': unittest.main()
