const test=require('node:test'),assert=require('node:assert/strict'),{map,level}=require('../web/croc-model.js');
const fixture=(stage='generating')=>({source:{state:'ok'},metrics:{stage,turn_id:'new',turn_started_at_ms:100,turn_output_tokens:5000,last:{turn_id:'new',measured_at_ms:101,input_tokens:10000,rate:80}}});
test('quantity boundaries and zero',()=>{assert.equal(level(0,[500,2000,5000,15000]),0);assert.equal(level(5000,[500,2000,5000,15000]),4);assert.equal(map(fixture()).bottles,3);});
test('end immediately deactivates regardless of output count',()=>{const s=map(fixture('idle'));assert.equal(s.active,false);assert.equal(s.idle,true);});
test('new turn does not reuse old tokens or rate',()=>{const f=fixture();f.metrics.last.turn_id='old';const s=map(f);assert.equal(s.bottles,0);assert.equal(s.lanes,0);assert.equal(s.speed,0);});
test('missing telemetry and tools never fabricate moving output',()=>{const f=fixture();f.metrics.last.rate=null;assert.equal(map(f).speed,0);assert.equal(map(fixture('tools')).active,false);f.source.state='unreadable';assert.equal(map(f).active,false);});
test('speed monotonic and independent of lane count',()=>{const a=fixture(),b=fixture();b.metrics.last.rate=200;assert.ok(map(b).speed>map(a).speed);b.metrics.last.rate=80;b.metrics.turn_output_tokens=15000;assert.equal(map(a).speed,map(b).speed);assert.equal(map(b).lanes,5);});
