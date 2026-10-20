'use strict';
(() => {
  const $ = id => document.getElementById(id), canvas = $('scene'), ctx = canvas.getContext('2d');
  const token = new URLSearchParams(location.hash.slice(1)).get('token') || '';
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const reference = new Image(); reference.src = '/local-croc.png';
  let selected = '', mode = 'live', state = { active:false, idle:true, bottles:0, lanes:0, speed:0 },
    particles = [], emission = 0, prev = performance.now(), demoEnd = 0, runId = null;
  async function api(path) {
    const response = await fetch('/api/' + path, {headers:{'X-Hud-Token':token},cache:'no-store'});
    const data = await response.json(); if (!response.ok) throw Error(data.error || '读取失败'); return data;
  }
  function clear() { particles=[]; emission=0; runId=null; state={active:false,idle:true,bottles:0,lanes:0,speed:0}; }
  function announce(text) { $('status').textContent=text; }
  async function list() {
    const value=await api('threads?force=1'), select=$('threads'); select.replaceChildren();
    for (const row of value.threads) { const option=document.createElement('option'); option.value=row.id; option.textContent=row.title+' · '+row.id.slice(-8); select.append(option); }
    selected=value.threads.some(t=>t.id===selected)?selected:value.threads[0]?.id||'';select.value=selected;
    if(!selected) announce('未发现兼容的本地对话。可以先试播动画。');
  }
  async function poll() {
    if(mode!=='live'||!selected)return;
    const id=selected;
    try {
      const value=await api('live?thread='+encodeURIComponent(id));
      if(mode!=='live'||selected!==id)return;
      const nextRun=value.metrics.turn_id||value.metrics.turn_started_at_ms;
      if(runId!==nextRun){particles=[];emission=0;runId=nextRun;}
      const mapped=CrocModel.map(value), previous=state;
      // Keep this round's fixed bottles only while its last particles leave.
      state={...mapped,bottles:mapped.idle?previous.bottles:mapped.bottles};
      announce(mapped.label);
      const s=value.metrics.last;
      $('lastRecord').textContent=s?'最近记录：输入 '+s.input_tokens.toLocaleString()+' Token，输出 '+s.output_tokens.toLocaleString()+' Token；此处为最近完成响应，画面行数按本轮累计输出。':'尚无完成响应的计数。';
    } catch(error){if(mode==='live'&&selected===id){state.active=false;state.speed=0;announce(error.message);}}
  }
  function readDemo(){state={active:true,idle:false,bottles:+$('bottles').value,lanes:+$('lanes').value,speed:+$('speed').value};}
  function finish(){state.active=false;state.idle=true;announce('对话已结束 · 角色恢复正常，剩余便便离场');}
  $('demo').onclick=()=>{clear();mode='demo';$('modeBadge').textContent='模拟演示';$('demoControls').hidden=false;readDemo();demoEnd=performance.now()+10000;announce('模拟输出中 · 10 秒后自动结束');};
  $('finish').onclick=finish;
  for(const id of ['bottles','lanes','speed'])$(id).onchange=()=>{readDemo();demoEnd=performance.now()+10000;};
  $('live').onclick=()=>{mode='live';clear();$('modeBadge').textContent='本地日志';$('demoControls').hidden=true;poll();};
  $('refresh').onclick=()=>list().then(poll).catch(e=>announce(e.message));
  $('threads').onchange=()=>{selected=$('threads').value;clear();if(mode==='live')poll();};
  function ellipse(x,y,rx,ry,fill,stroke){ctx.beginPath();ctx.ellipse(x,y,rx,ry,0,0,Math.PI*2);ctx.fillStyle=fill;ctx.fill();if(stroke){ctx.strokeStyle=stroke;ctx.lineWidth=2;ctx.stroke();}}
  function poop(x,y){
    if(reference.complete&&reference.naturalWidth){ctx.drawImage(reference,210,128,26,37,x-8,y-13,18,25);return;}
    ctx.fillStyle='#956039';ctx.beginPath();ctx.moveTo(x-8,y+8);ctx.bezierCurveTo(x-14,y,x-4,y-1,x-5,y-5);ctx.bezierCurveTo(x+3,y-4,x+2,y-11,x+1,y-13);ctx.bezierCurveTo(x+12,y-8,x+3,y-2,x+9,y);ctx.bezierCurveTo(x+15,y+7,x+8,y+10,x-8,y+8);ctx.fill();
  }
  function bottle(x,y){
    ctx.fillStyle='#e5a46f';ctx.fillRect(x-5,y-24,10,6);ellipse(x,y-26,3,5,'#f4c49a');
    ctx.fillStyle='#d8e8df';ctx.beginPath();ctx.roundRect(x-10,y-18,20,32,5);ctx.fill();
    ctx.fillStyle='#fff5d8';ctx.fillRect(x-7,y-9,14,19);ctx.fillStyle='#85a98f';ctx.fillRect(x-11,y-19,22,5);
  }
  function croc(x,y,size,working,t){
    const bob=working&&!reduced?Math.sin(t*5)*2:0;
    if(reference.complete&&reference.naturalWidth){ctx.drawImage(reference,57,25,155,171,x-size/2,y-size+bob,size,size*171/155);return;}
    ctx.save();ctx.translate(x,y+bob);ctx.scale(size/145,size/145);
    ctx.fillStyle='#78a751';ctx.beginPath();ctx.moveTo(25,-50);ctx.quadraticCurveTo(86,-23,71,-65);ctx.quadraticCurveTo(103,-17,33,-9);ctx.fill();
    ellipse(0,-52,35,48,'#9cc768','#628442');ellipse(1,-43,23,31,'#c8df93');
    ellipse(-11,-112,35,32,'#9cc768','#628442');ellipse(-37,-99,35,19,'#aacf7b','#628442');
    ellipse(-22,-130,13,17,'#fffbed','#628442');ellipse(2,-132,13,17,'#fffbed','#628442');
    ellipse(-23,-129,4,7,'#344c2c');ellipse(1,-131,4,7,'#344c2c');
    ellipse(-22,-8,19,7,'#78a751');ellipse(21,-8,19,7,'#78a751');
    ctx.strokeStyle='#628442';ctx.lineWidth=3;ctx.beginPath();ctx.moveTo(-39,-94);ctx.quadraticCurveTo(-20,-84,-8,-94);ctx.stroke();ctx.restore();
  }
  function frame(now){
    const dt=Math.min(.05,(now-prev)/1000);prev=now;
    if(mode==='demo'&&state.active&&now>=demoEnd)finish();
    const w=canvas.clientWidth,h=canvas.clientHeight,dpr=devicePixelRatio||1;
    if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}
    ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
    const middle=w*.40,size=Math.min(150,w*.29),start=middle+size*.47,end=w-16,ground=h-30;
    const moving=state.active&&state.speed>0&&state.lanes>0;
    if(moving&&!reduced){emission+=dt*state.speed;while(emission>=38){emission-=38;for(let row=0;row<state.lanes;row++)particles.push({x:start,y:ground-125+row*24,speed:state.speed});}}
    for(const p of particles)p.x+=(moving?state.speed:p.speed)*dt;
    particles=particles.filter(p=>p.x<end);
    if(state.idle&&!particles.length)state.bottles=0;
    const css=getComputedStyle(document.documentElement),line=css.getPropertyValue('--line').trim();
    ctx.strokeStyle=line;ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(16,ground);ctx.lineTo(w-16,ground);ctx.stroke();
    const left=w*.14;
    ellipse(left,ground-6,Math.min(54,w*.13),9,'#d8e2d1','#9aaf8c');
    for(let i=0;i<state.bottles;i++){const col=i%3,row=Math.floor(i/3);bottle(left+(col-1)*Math.min(24,w*.06),ground-27-row*42);}
    croc(middle,ground-6,size,state.active,now/1000);
    for(const p of particles)poop(p.x,p.y);
    if(reduced&&moving)for(let row=0;row<state.lanes;row++)for(let i=0;i<3;i++)poop(start+15+i*(end-start-20)/3,ground-125+row*24);
    $('characterState').textContent=state.active?'工作中':state.idle?'休息一下':'等待 / 工具执行';
    canvas.dataset.active=String(state.active);canvas.dataset.lanes=String(state.lanes);canvas.dataset.bottles=String(state.bottles);canvas.dataset.particles=String(particles.length);
    requestAnimationFrame(frame);
  }
  list().then(poll).catch(e=>announce(e.message));setInterval(poll,500);requestAnimationFrame(frame);
})();
