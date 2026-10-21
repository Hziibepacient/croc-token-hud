'use strict';
(() => {
  const $ = id => document.getElementById(id), canvas = $('scene'), ctx = canvas.getContext('2d');
  const token = new URLSearchParams(location.hash.slice(1)).get('token') || '';
  const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
  const restingSprite = new Image(); restingSprite.src = '/assets/croc-rest.png';
  const workingSprite = new Image(); workingSprite.src = '/assets/croc-work.png';
  const poopSprite = new Image(); poopSprite.src = '/assets/poop.png';
  const bottleSprite = new Image(); bottleSprite.src = '/assets/bottle.png';
  // Same demo tiers as the desktop menu: seconds to cross and lane count.
  const demoTiers = [[4, 1], [3, 1], [2, 2], [1.2, 2], [.7, 3]];
  const laneOffsets = [0, -24, 24]; // Butt first, then spread above/below.
  const idleState = {working:false, emitting:false, idle:true, bottles:0, lanes:0, seconds:0, spacing:70};
  let selected = '', mode = 'live', state = {...idleState}, quota = {},
    particles = [], emission = [0, 0, 0], prev = performance.now(), demoEnd = 0, runId = null,
    lanes = 0, pendingLanes = 0, laneChangeAt = 0, motion = 0, spacing = 70;
  async function api(path) {
    const response = await fetch('/api/' + path, {headers:{'X-Hud-Token':token},cache:'no-store'});
    const data = await response.json(); if (!response.ok) throw Error(data.error || '读取失败'); return data;
  }
  function clear() { particles=[]; emission=[0,0,0]; runId=null; lanes=pendingLanes=0; state={...idleState,bottles:state.bottles}; }
  function announce(text) { $('status').textContent=text; }
  function clock(seconds) { return new Date(seconds*1000).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',hour12:false}); }
  function bar() {
    const known=quota.quota_status==='recorded';
    $('barQuota').textContent=known?+quota.quota_remaining.toFixed(1)+'%':'待更新';
    $('barReset').textContent=quota.quota_resets_at>0?clock(quota.quota_resets_at):'—';
    $('barRate').textContent=Number.isFinite(state.rate)?+state.rate.toFixed(1)+' tok/s':'—';
  }
  async function list() {
    const value=await api('threads?force=1'), select=$('threads'); select.replaceChildren();
    const rows=value.threads.sort((a,b)=>(b.updated_at_ms||0)-(a.updated_at_ms||0));
    for (const row of rows) { const option=document.createElement('option'); option.value=row.id; option.textContent=row.title+' · '+row.id.slice(-8); select.append(option); }
    selected=rows.some(t=>t.id===selected)?selected:rows[0]?.id||'';select.value=selected;
    if(!selected) announce('未发现兼容的本地对话。可以先试播动画。');
  }
  async function poll() {
    const id=selected;
    try {
      // Scene rules live in croc_scene.py and are shared with the desktop window.
      const value=await api('croc?thread='+encodeURIComponent(id));
      if(selected!==id)return;
      quota=value;
      if(mode==='live'){
        const nextRun=value.selected+'/'+value.run;
        if(runId!==nextRun){particles=[];runId=nextRun;}
        state=value;announce(value.status);
      }
      bar();
      $('lastRecord').textContent=value.last_output!=null?'最近完成响应：输入 '+value.last_input.toLocaleString()+' Token，输出 '+value.last_output.toLocaleString()+' Token。':'尚无完成响应的计数。';
    } catch(error){if(mode==='live'&&selected===id){state={...state,working:false,emitting:false};announce(error.message);}}
  }
  function readDemo(){
    const [seconds,demoLanes]=demoTiers[+$('tier').value], q=$('quota').value;
    const bottles=q===''?state.bottles:Math.ceil(+q/20);
    state={...state,working:true,emitting:true,idle:false,bottles,lanes:demoLanes,seconds,spacing:Math.max(25,Math.min(70,seconds*15+10)),rate:NaN};
  }
  function finish(){state={...state,working:false,emitting:false,idle:true};announce('对话已结束 · 角色恢复正常，剩余便便离场');}
  $('demo').onclick=()=>{clear();mode='demo';$('modeBadge').textContent='模拟演示';$('demoControls').hidden=false;readDemo();demoEnd=performance.now()+10000;announce('模拟输出中 · 10 秒后自动结束');};
  $('finish').onclick=finish;
  for(const id of ['tier','quota'])$(id).onchange=()=>{readDemo();demoEnd=performance.now()+10000;};
  $('live').onclick=()=>{mode='live';clear();$('modeBadge').textContent='本地日志';$('demoControls').hidden=true;poll();};
  $('refresh').onclick=()=>list().then(poll).catch(e=>announce(e.message));
  $('threads').onchange=()=>{selected=$('threads').value;clear();poll();};
  function ellipse(x,y,rx,ry,fill,stroke){ctx.beginPath();ctx.ellipse(x,y,rx,ry,0,0,Math.PI*2);ctx.fillStyle=fill;ctx.fill();if(stroke){ctx.strokeStyle=stroke;ctx.lineWidth=2;ctx.stroke();}}
  function poop(x,y,alpha=1){
    ctx.globalAlpha=alpha;
    if(poopSprite.complete&&poopSprite.naturalWidth)ctx.drawImage(poopSprite,x-13,y-8,26,16);
    else{ctx.fillStyle='#956039';ctx.beginPath();ctx.ellipse(x,y,10,6,0,0,Math.PI*2);ctx.fill();}
    ctx.globalAlpha=1;
  }
  function bottle(x,y){
    if(bottleSprite.complete&&bottleSprite.naturalWidth){ctx.drawImage(bottleSprite,x-14,y-27,28,41);return;}
    ctx.fillStyle='#d8e8df';ctx.beginPath();ctx.roundRect(x-10,y-18,20,32,5);ctx.fill();
    ctx.fillStyle='#85a98f';ctx.fillRect(x-11,y-19,22,5);
  }
  function croc(x,y,size,working,t){
    const bob=working&&!reduced?Math.sin(t*5)*2:0;
    const sprite = working ? workingSprite : restingSprite;
    canvas.dataset.pose = working ? 'working' : 'resting';
    if(sprite.complete&&sprite.naturalWidth){
      // Both completed sprites share the same size and foot baseline.
      const height=size*1.18, width=height*sprite.naturalWidth/sprite.naturalHeight;
      ctx.drawImage(sprite,x-width/2,y-height+bob,width,height);
      return;
    }
    ctx.save();ctx.translate(x,y+bob);ctx.scale(size/145,size/145);
    ellipse(0,-52,35,48,'#9cc768','#628442');ellipse(-11,-112,35,32,'#9cc768','#628442');
    ctx.restore();
  }
  function frame(now){
    const dt=Math.min(.05,(now-prev)/1000);prev=now;
    if(mode==='demo'&&state.emitting&&now>=demoEnd)finish();
    const w=canvas.clientWidth,h=canvas.clientHeight,dpr=devicePixelRatio||1;
    if(canvas.width!==Math.round(w*dpr)||canvas.height!==Math.round(h*dpr)){canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);}
    ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
    const middle=w*.40,size=Math.min(150,w*.29),start=middle+size*.47,end=w-16,ground=h-30,butt=ground-101;
    // Lane changes wait ~600 ms; speed and density ease over ~300 ms, as on the desktop.
    const target=state.emitting?state.lanes:0;
    if(target===0||lanes===0){lanes=pendingLanes=target;}
    else if(target!==lanes){if(pendingLanes!==target){pendingLanes=target;laneChangeAt=now;}else if(now-laneChangeAt>600)lanes=target;}
    const blend=1-Math.exp(-dt/.3);
    motion+=((state.emitting&&state.seconds>0?1/state.seconds:0)-motion)*blend;
    spacing+=((state.spacing||70)-spacing)*blend;
    const speed=(end-start)*motion;
    if(state.emitting&&speed>0&&!reduced)for(let lane=0;lane<lanes;lane++){
      emission[lane]+=speed*dt/Math.max(17,spacing);
      while(emission[lane]>=1){emission[lane]-=1;particles.push({x:start,y:butt+laneOffsets[lane],speed});}
    }
    for(const p of particles){p.speed+=((state.emitting?speed:p.speed)-p.speed)*blend;p.x+=p.speed*dt;}
    particles=particles.filter(p=>p.x<end);
    const css=getComputedStyle(document.documentElement),line=css.getPropertyValue('--line').trim();
    ctx.strokeStyle=line;ctx.lineWidth=1;ctx.beginPath();ctx.moveTo(16,ground);ctx.lineTo(w-16,ground);ctx.stroke();
    // A stable, loose cluster in front of the snout; quantity changes do not
    // reshuffle existing bottles.
    const bottleRight=middle-size*.57-18;
    const spread=Math.min(40,Math.max(24,bottleRight-30));
    const bottleScale=.85*Math.min(1,size/140);
    // Keep the entire rotated bottle between the visible head and feet.
    const bottleTop=ground-size*1.04+30*bottleScale;
    const bottleBottom=ground-size*.18-18*bottleScale;
    const bottleLayout=[
      [0,.16,.08],[-.95,.50,-.12],[-.08,.88,-.08],
      [-.88,0,.10],[-.90,1,.06],
    ];
    for(let i=0;i<Math.min(5,state.bottles||0);i++){
      const [offsetX,offsetY,angle]=bottleLayout[i];
      ctx.save();
      ctx.translate(bottleRight+offsetX*spread,bottleTop+offsetY*(bottleBottom-bottleTop));
      ctx.rotate(angle);ctx.scale(bottleScale,bottleScale);bottle(0,0);ctx.restore();
    }
    for(const p of particles)poop(p.x,p.y,Math.max(0,Math.min(1,(end-p.x)/20)));
    if(reduced&&state.emitting)for(let lane=0;lane<lanes;lane++)for(let i=0;i<3;i++)poop(start+15+i*(end-start-20)/3,butt+laneOffsets[lane]);
    // Paint the character last so new particles emerge from behind it.
    croc(middle,ground-6,size,state.working,now/1000);
    $('characterState').textContent=state.emitting?'工作中':state.working?'等待 / 工具执行':'休息一下';
    canvas.dataset.active=String(state.emitting);canvas.dataset.lanes=String(lanes);canvas.dataset.bottles=String(state.bottles||0);canvas.dataset.particles=String(particles.length);
    requestAnimationFrame(frame);
  }
  list().then(poll).catch(e=>announce(e.message));setInterval(poll,500);requestAnimationFrame(frame);
})();
