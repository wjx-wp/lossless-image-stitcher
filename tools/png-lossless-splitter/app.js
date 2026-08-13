(() => {
  const $ = s => document.querySelector(s);
  const drop=$('#drop'), fileInput=$('#file'), work=$('#work');
  const editor=$('#editor'), ectx=editor.getContext('2d',{willReadFrequently:true,alpha:true});
  const rawCanvas=document.createElement('canvas'), rawCtx=rawCanvas.getContext('2d',{willReadFrequently:true,alpha:true});
  let W=0,H=0,sourceName='image',originalData=null,workingData=null,splits=[],mode='none',dragStart=null,brushDrawing=false,undoStack=[];
  const MAX_UNDO=12;

  const clamp=(v,a,b)=>Math.max(a,Math.min(b,v));
  const baseName=n=>(n||'image').replace(/\.[^.]+$/,'');
  const threshold=()=>$('#alphaNoise').checked?2:0;
  const minGap=()=>Math.max(2,parseInt($('#minGap').value||'24',10));

  function cloneImageData(src){ return new ImageData(new Uint8ClampedArray(src.data),src.width,src.height); }
  function pushUndo(){ if(!workingData)return; undoStack.push(new Uint8ClampedArray(workingData.data)); if(undoStack.length>MAX_UNDO)undoStack.shift(); }
  function restoreUndo(){ if(!undoStack.length)return; workingData.data.set(undoStack.pop()); rebuildAfterEdit(); }

  function loadFile(file){
    if(!file)return;
    if(file.type && file.type!=='image/png'){alert('为保证流程可控，本工具 v2.0 只接受 PNG。');return;}
    sourceName=baseName(file.name);
    const url=URL.createObjectURL(file), img=new Image();
    img.onload=()=>{
      W=img.naturalWidth;H=img.naturalHeight;
      rawCanvas.width=W;rawCanvas.height=H;rawCtx.clearRect(0,0,W,H);rawCtx.imageSmoothingEnabled=false;rawCtx.drawImage(img,0,0,W,H);
      originalData=rawCtx.getImageData(0,0,W,H);workingData=cloneImageData(originalData);undoStack=[];splits=[];
      URL.revokeObjectURL(url); work.classList.remove('hidden'); autoDetectSplits(); rebuildAfterEdit(false);
    };
    img.onerror=()=>alert('PNG 读取失败。'); img.src=url;
  }

  function rebuildRaw(){ rawCanvas.width=W;rawCanvas.height=H;rawCtx.putImageData(workingData,0,0); }
  function rebuildAfterEdit(redetect=false){ rebuildRaw(); if(redetect)autoDetectSplits(); drawEditor(); renderAll(); }

  function columnAnalysis(){
    const occ=new Uint32Array(W),mass=new Float64Array(W),t=threshold(),d=workingData.data;
    for(let y=0;y<H;y++){let i=(y*W)*4;for(let x=0;x<W;x++,i+=4){const a=d[i+3];if(a>t)occ[x]++;mass[x]+=a;}}
    return {occ,mass};
  }
  function autoDetectSplits(){
    if(!workingData)return;
    const {occ,mass}=columnAnalysis(),gap=minGap();
    // 分割检测允许每列存在极少量孤立噪点；这里只影响“检测”，不会删除这些像素。
    const noisePixels=Math.max(2,Math.floor(H*.003)),runs=[];let s=-1;
    for(let x=0;x<W;x++){
      const emptyLike=occ[x]<=noisePixels;
      if(emptyLike){if(s<0)s=x;}
      else if(s>=0){runs.push([s,x-1]);s=-1;}
    }
    if(s>=0)runs.push([s,W-1]);

    let total=0;for(const v of mass)total+=v;
    if(total<=0){splits=[];updateSplitUI();return;}
    const prefix=new Float64Array(W);let acc=0;for(let x=0;x<W;x++){acc+=mass[x];prefix[x]=acc;}
    let candidates=[];
    for(const [a,b] of runs){
      const len=b-a+1;if(len<gap)continue;
      const mid=Math.round((a+b)/2),left=mid>0?prefix[mid-1]:0,frac=left/total;
      // 过滤只切到边缘零星文字/噪点的假间隙；真正图块分界两侧都应有可观内容。
      if(frac<.02||frac>.98)continue;
      candidates.push({a,b,len,mid,frac});
    }
    // 同一真实分界附近可能被 1~2 个噪点切成多段空隙。
    // 如果两段前后的累计图像质量几乎相同，就视为同一分界，只保留最宽的那段。
    const groups=[];
    for(const c of candidates){
      const g=groups[groups.length-1];
      if(!g||Math.abs(c.frac-g[g.length-1].frac)>.005)groups.push([c]);else g.push(c);
    }
    candidates=groups.map(g=>g.reduce((best,c)=>c.len>best.len?c:best,g[0]));
    splits=dedupeSplits(candidates.map(c=>c.mid));updateSplitUI();
  }
  function dedupeSplits(arr){
    const sorted=[...arr].map(v=>clamp(Math.round(v),1,W-1)).sort((a,b)=>a-b),out=[];
    for(const x of sorted)if(!out.length||x-out[out.length-1]>=2)out.push(x);return out;
  }
  function setHalf(){splits=[Math.floor(W/2)];updateSplitUI();drawEditor();renderAll();}
  function updateSplitUI(){
    const box=$('#splitList');box.innerHTML='';
    if(!splits.length){box.innerHTML='<span class="small">当前没有分割线，将作为 1 张图片输出。</span>';return;}
    splits.forEach((x,i)=>{const c=document.createElement('span');c.className='chip';c.innerHTML=`分割 ${i+1}: X=${x}px <button title="删除">×</button>`;c.querySelector('button').onclick=()=>{splits.splice(i,1);updateSplitUI();drawEditor();renderAll();};box.appendChild(c);});
  }

  function boundaries(){return [0,...dedupeSplits(splits),W];}
  function regionList(){const b=boundaries(),out=[];for(let i=0;i<b.length-1;i++)if(b[i+1]-b[i]>0)out.push({x0:b[i],x1:b[i+1],index:i});return out;}

  function bbox(x0,x1,y0=0,y1=H,t=0){
    const d=workingData.data;let minX=x1,minY=y1,maxX=-1,maxY=-1;
    for(let y=y0;y<y1;y++){let i=(y*W+x0)*4+3;for(let x=x0;x<x1;x++,i+=4){if(d[i]>t){if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;if(y>maxY)maxY=y;}}}
    return maxX<minX?null:{x:minX,y:minY,w:maxX-minX+1,h:maxY-minY+1};
  }

  function detectTextZones(){
    const zones=[],t=threshold(),d=workingData.data;
    for(const r of regionList()){
      const b=bbox(r.x0,r.x1,0,H,t);if(!b||b.h<20)continue;
      const occ=new Uint32Array(b.h);
      for(let yy=0;yy<b.h;yy++){const y=b.y+yy;let i=(y*W+b.x)*4+3;for(let xx=0;xx<b.w;xx++,i+=4)if(d[i]>t)occ[yy]++;}
      const start=Math.floor(b.h*.42),end=Math.floor(b.h*.92);let run=-1,candidates=[];
      for(let yy=start;yy<=end;yy++){
        if(occ[yy]===0){if(run<0)run=yy;}
        else if(run>=0){candidates.push([run,yy-1]);run=-1;}
      }if(run>=0)candidates.push([run,end]);
      candidates=candidates.filter(([a,z])=>z-a+1>=Math.max(3,Math.floor(H*.003)) && a>0 && z<b.h-1);
      if(!candidates.length)continue;
      candidates.sort((p,q)=>(q[1]-q[0])-(p[1]-p[0]));
      let chosen=null;
      for(const g of candidates){
        const belowStart=g[1]+1;let belowPixels=0,abovePixels=0;
        for(let yy=0;yy<g[0];yy++)abovePixels+=occ[yy];for(let yy=belowStart;yy<b.h;yy++)belowPixels+=occ[yy];
        const belowHeight=b.h-belowStart;
        if(abovePixels>0 && belowPixels>0 && belowHeight<=b.h*.38){chosen=g;break;}
      }
      if(!chosen)continue;
      const cutY=b.y+chosen[1]+1;
      zones.push({x:b.x,y:cutY,w:b.w,h:(b.y+b.h)-cutY});
    }
    return zones;
  }

  function autoRemoveText(){
    const zones=detectTextZones();
    if(!zones.length){alert('没有检测到足够明确的“图标 / 底部文字”透明分隔带。未修改图片。');return;}
    pushUndo();for(const z of zones)eraseRectPixels(z.x,z.y,z.w,z.h);rebuildAfterEdit(false);
    $('#modeTip').textContent=`已删除 ${zones.length} 个疑似底部文字区域，可点击“撤销”恢复`;
  }

  function eraseRectPixels(x,y,w,h){
    x=clamp(Math.floor(x),0,W);y=clamp(Math.floor(y),0,H);const x2=clamp(Math.ceil(x+w),0,W),y2=clamp(Math.ceil(y+h),0,H),d=workingData.data;
    for(let yy=y;yy<y2;yy++){let i=(yy*W+x)*4+3;for(let xx=x;xx<x2;xx++,i+=4)d[i]=0;}
  }
  function eraseCircle(cx,cy,r){
    const d=workingData.data,r2=r*r,x0=clamp(Math.floor(cx-r),0,W-1),x1=clamp(Math.ceil(cx+r),0,W-1),y0=clamp(Math.floor(cy-r),0,H-1),y1=clamp(Math.ceil(cy+r),0,H-1);
    for(let y=y0;y<=y1;y++)for(let x=x0;x<=x1;x++){const dx=x-cx,dy=y-cy;if(dx*dx+dy*dy<=r2)d[(y*W+x)*4+3]=0;}
  }

  function canvasPoint(ev){const rect=editor.getBoundingClientRect();return{x:clamp((ev.clientX-rect.left)*W/rect.width,0,W),y:clamp((ev.clientY-rect.top)*H/rect.height,0,H)};}
  function setMode(m){mode=m;dragStart=null;brushDrawing=false;['splitMode','rectErase','brushErase'].forEach(id=>$('#'+id).classList.remove('active'));if(m==='split')$('#splitMode').classList.add('active');if(m==='rect')$('#rectErase').classList.add('active');if(m==='brush')$('#brushErase').classList.add('active');$('#modeTip').textContent=m==='split'?'点击原图任意位置添加分割线':m==='rect'?'拖出矩形，矩形内像素变透明':m==='brush'?'按住拖动画笔删除':'普通预览模式';drawEditor();}

  function drawEditor(extra=null){
    if(!workingData)return;editor.width=W;editor.height=H;ectx.clearRect(0,0,W,H);ectx.putImageData(workingData,0,0);
    ectx.save();ectx.lineWidth=Math.max(2,W/700);ectx.setLineDash([Math.max(8,W/140),Math.max(5,W/220)]);ectx.strokeStyle='#2563eb';
    for(const x of splits){ectx.beginPath();ectx.moveTo(x,0);ectx.lineTo(x,H);ectx.stroke();}
    ectx.setLineDash([]);
    if(extra&&extra.type==='rect'){ectx.fillStyle='rgba(220,38,38,.14)';ectx.strokeStyle='#dc2626';ectx.lineWidth=Math.max(2,W/700);ectx.fillRect(extra.x,extra.y,extra.w,extra.h);ectx.strokeRect(extra.x,extra.y,extra.w,extra.h);}
    ectx.restore();
  }

  editor.addEventListener('pointerdown',ev=>{
    if(mode==='none')return;editor.setPointerCapture(ev.pointerId);const p=canvasPoint(ev);
    if(mode==='split'){splits.push(p.x);splits=dedupeSplits(splits);updateSplitUI();drawEditor();renderAll();return;}
    if(mode==='rect'){dragStart=p;drawEditor({type:'rect',x:p.x,y:p.y,w:0,h:0});}
    if(mode==='brush'){pushUndo();brushDrawing=true;eraseCircle(p.x,p.y,Math.max(1,parseInt($('#brushSize').value||'28',10))/2);rebuildRaw();drawEditor();}
  });
  editor.addEventListener('pointermove',ev=>{
    const p=canvasPoint(ev);
    if(mode==='rect'&&dragStart){drawEditor({type:'rect',x:dragStart.x,y:dragStart.y,w:p.x-dragStart.x,h:p.y-dragStart.y});}
    if(mode==='brush'&&brushDrawing){eraseCircle(p.x,p.y,Math.max(1,parseInt($('#brushSize').value||'28',10))/2);rebuildRaw();drawEditor();}
  });
  editor.addEventListener('pointerup',ev=>{
    const p=canvasPoint(ev);
    if(mode==='rect'&&dragStart){pushUndo();const x=Math.min(dragStart.x,p.x),y=Math.min(dragStart.y,p.y),w=Math.abs(p.x-dragStart.x),h=Math.abs(p.y-dragStart.y);eraseRectPixels(x,y,w,h);dragStart=null;rebuildAfterEdit(false);}
    if(mode==='brush'&&brushDrawing){brushDrawing=false;rebuildAfterEdit(false);}
  });

  function outputRects(){
    const trim=$('#trim').checked,pad=Math.max(0,parseInt($('#padding').value||'0',10)),regs=regionList();let rects=[];
    for(const r of regs){const b=trim?bbox(r.x0,r.x1):{x:r.x0,y:0,w:r.x1-r.x0,h:H};rects.push(b||{x:r.x0,y:0,w:1,h:1,empty:true});}
    const maxW=Math.max(...rects.map(r=>r.w)),maxH=Math.max(...rects.map(r=>r.h));return{rects,outW:maxW+pad*2,outH:maxH+pad*2,pad};
  }

  function makeOutputCanvas(rect,outW,outH){
    const transparent=document.createElement('canvas');transparent.width=outW;transparent.height=outH;const tc=transparent.getContext('2d',{alpha:true});tc.clearRect(0,0,outW,outH);
    if(!rect.empty){const data=rawCtx.getImageData(rect.x,rect.y,rect.w,rect.h),dx=Math.floor((outW-rect.w)/2),dy=Math.floor((outH-rect.h)/2);tc.putImageData(data,dx,dy);}
    if($('#bgMode').value==='transparent')return transparent;
    const solid=document.createElement('canvas');solid.width=outW;solid.height=outH;const sc=solid.getContext('2d',{alpha:false});sc.fillStyle=$('#bgColor').value;sc.fillRect(0,0,outW,outH);sc.drawImage(transparent,0,0);return solid;
  }

  function renderAll(){
    if(!workingData)return;rebuildRaw();updateSplitUI();
    const {rects,outW,outH}=outputRects(),pre=$('#previews');pre.innerHTML='';
    rects.forEach((r,i)=>{
      const c=makeOutputCanvas(r,outW,outH),card=document.createElement('div');card.className='previewCard';
      const title=document.createElement('div');title.className='previewTitle';title.innerHTML=`<b>图 ${String(i+1).padStart(2,'0')}</b><span class="small">${outW} × ${outH}</span>`;
      const wrap=document.createElement('div');wrap.className='previewCanvasWrap';wrap.appendChild(c);
      const row=document.createElement('div');row.style='margin-top:9px';const btn=document.createElement('button');btn.className='primary';btn.textContent='下载 PNG';btn.onclick=()=>downloadCanvas(c,`${sourceName}-${String(i+1).padStart(2,'0')}.png`);row.appendChild(btn);
      card.append(title,wrap,row);pre.appendChild(card);
    });
    $('#stats').innerHTML=`原图：<b>${W} × ${H}</b>　｜　分割线：<b>${splits.length}</b> 条　｜　当前输出：<b>${rects.length}</b> 张`;
    $('#outputInfo').innerHTML=`统一输出尺寸：<b>${outW} × ${outH}</b> px　｜　背景：<b>${$('#bgMode').value==='transparent'?'透明 Alpha':'纯色 '+$('#bgColor').value}</b>　｜　内容缩放：<b>0 次</b>`;
  }

  function downloadCanvas(c,name){c.toBlob(blob=>{if(!blob)return;const a=document.createElement('a'),u=URL.createObjectURL(blob);a.href=u;a.download=name;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(u),1500);},'image/png');}

  // ZIP 打包逻辑位于 zip.js。
  function canvasBytes(c){return new Promise(res=>c.toBlob(async b=>res(new Uint8Array(await b.arrayBuffer())),'image/png'));}
  async function downloadZip(){
    const {rects,outW,outH}=outputRects(),files=[];
    for(let i=0;i<rects.length;i++){const c=makeOutputCanvas(rects[i],outW,outH);files.push({name:`${sourceName}-${String(i+1).padStart(2,'0')}.png`,data:await canvasBytes(c)});}
    const blob=PNGSplitterZip.makeZip(files),a=document.createElement('a'),u=URL.createObjectURL(blob);a.href=u;a.download=`${sourceName}-split-${files.length}.zip`;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(u),2500);
  }

  drop.addEventListener('click',()=>fileInput.click());fileInput.addEventListener('change',e=>loadFile(e.target.files[0]));
  ['dragenter','dragover'].forEach(n=>drop.addEventListener(n,e=>{e.preventDefault();drop.classList.add('drag')}));['dragleave','drop'].forEach(n=>drop.addEventListener(n,e=>{e.preventDefault();drop.classList.remove('drag')}));drop.addEventListener('drop',e=>loadFile(e.dataTransfer.files[0]));
  $('#autoSplit').onclick=()=>{autoDetectSplits();drawEditor();renderAll();};$('#halfSplit').onclick=setHalf;$('#splitMode').onclick=()=>setMode(mode==='split'?'none':'split');$('#clearSplits').onclick=()=>{splits=[];updateSplitUI();drawEditor();renderAll();};
  $('#autoText').onclick=autoRemoveText;$('#rectErase').onclick=()=>setMode(mode==='rect'?'none':'rect');$('#brushErase').onclick=()=>setMode(mode==='brush'?'none':'brush');$('#undo').onclick=restoreUndo;$('#restore').onclick=()=>{if(!originalData)return;workingData=cloneImageData(originalData);undoStack=[];rebuildAfterEdit(true);setMode('none');};
  $('#alphaNoise').onchange=()=>{autoDetectSplits();drawEditor();renderAll();};$('#minGap').onchange=()=>{autoDetectSplits();drawEditor();renderAll();};$('#trim').onchange=renderAll;$('#padding').oninput=renderAll;$('#bgMode').onchange=()=>{$('#colorWrap').classList.toggle('hidden',$('#bgMode').value!=='solid');renderAll();};$('#bgColor').oninput=renderAll;$('#downloadZip').onclick=downloadZip;
})();
