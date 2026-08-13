(() => {
  const $ = s => document.querySelector(s);
  const drop=$('#drop'), fileInput=$('#file'), work=$('#work');
  const editor=$('#editor'), ectx=editor.getContext('2d',{willReadFrequently:true,alpha:true});
  const rawCanvas=document.createElement('canvas'), rawCtx=rawCanvas.getContext('2d',{willReadFrequently:true,alpha:true});

  let W=0,H=0,sourceName='image',originalData=null,workingData=null;
  let splitsX=[],splitsY=[],mode='none',dragStart=null,brushDrawing=false,undoStack=[];
  const MAX_UNDO=12;

  const clamp=(v,a,b)=>Math.max(a,Math.min(b,v));
  const baseName=n=>(n||'image').replace(/\.[^.]+$/,'');
  const detectThreshold=()=>$('#alphaNoise').checked?2:0;
  const minGap=()=>Math.max(2,parseInt($('#minGap').value||'24',10));
  const centerOn=()=>$('#centerContent').checked;
  const trimOn=()=>$('#trim').checked;

  function cloneImageData(src){return new ImageData(new Uint8ClampedArray(src.data),src.width,src.height);}
  function pushUndo(){if(!workingData)return;undoStack.push(new Uint8ClampedArray(workingData.data));if(undoStack.length>MAX_UNDO)undoStack.shift();}
  function restoreUndo(){if(!undoStack.length)return;workingData.data.set(undoStack.pop());rebuildAfterEdit();}

  function loadFile(file){
    if(!file)return;
    if(file.type && file.type!=='image/png'){alert('为保证无损流程可控，本工具 v2.2 只接受 PNG。');return;}
    sourceName=baseName(file.name);
    const url=URL.createObjectURL(file),img=new Image();
    img.onload=()=>{
      W=img.naturalWidth;H=img.naturalHeight;
      rawCanvas.width=W;rawCanvas.height=H;rawCtx.clearRect(0,0,W,H);rawCtx.imageSmoothingEnabled=false;rawCtx.drawImage(img,0,0,W,H);
      originalData=rawCtx.getImageData(0,0,W,H);workingData=cloneImageData(originalData);
      splitsX=[];splitsY=[];undoStack=[];mode='none';
      URL.revokeObjectURL(url);work.classList.remove('hidden');
      autoDetectX();rebuildAfterEdit(false);updateIcoUI();
    };
    img.onerror=()=>alert('PNG 读取失败。');img.src=url;
  }

  function rebuildRaw(){rawCanvas.width=W;rawCanvas.height=H;rawCtx.putImageData(workingData,0,0);}
  function rebuildAfterEdit(redetect=false){rebuildRaw();if(redetect)autoDetectX();drawEditor();renderAll();}

  function axisAnalysis(axis){
    const len=axis==='x'?W:H,cross=axis==='x'?H:W,occ=new Uint32Array(len),mass=new Float64Array(len),t=detectThreshold(),d=workingData.data;
    if(axis==='x'){
      for(let y=0;y<H;y++){let i=(y*W)*4;for(let x=0;x<W;x++,i+=4){const a=d[i+3];if(a>t)occ[x]++;mass[x]+=a;}}
    }else{
      for(let y=0;y<H;y++){let count=0,sum=0,i=(y*W)*4;for(let x=0;x<W;x++,i+=4){const a=d[i+3];if(a>t)count++;sum+=a;}occ[y]=count;mass[y]=sum;}
    }
    return {len,cross,occ,mass};
  }

  function detectAxis(axis,minSideFraction){
    const {len,cross,occ,mass}=axisAnalysis(axis),gap=minGap();
    const noisePixels=Math.max(2,Math.floor(cross*.003)),runs=[];let s=-1;
    for(let p=0;p<len;p++){
      const emptyLike=occ[p]<=noisePixels;
      if(emptyLike){if(s<0)s=p;}
      else if(s>=0){runs.push([s,p-1]);s=-1;}
    }
    if(s>=0)runs.push([s,len-1]);

    let total=0;for(const v of mass)total+=v;if(total<=0)return[];
    const prefix=new Float64Array(len);let acc=0;for(let p=0;p<len;p++){acc+=mass[p];prefix[p]=acc;}
    let candidates=[];
    for(const [a,b] of runs){
      const runLen=b-a+1;if(runLen<gap)continue;
      const mid=Math.round((a+b)/2),before=mid>0?prefix[mid-1]:0,frac=before/total;
      if(frac<minSideFraction||frac>1-minSideFraction)continue;
      candidates.push({a,b,len:runLen,mid,frac});
    }
    const groups=[];
    for(const c of candidates){
      const g=groups[groups.length-1];
      if(!g||Math.abs(c.frac-g[g.length-1].frac)>.005)groups.push([c]);else g.push(c);
    }
    return dedupe(groups.map(g=>g.reduce((best,c)=>c.len>best.len?c:best,g[0]).mid),len);
  }

  function dedupe(arr,len){
    const sorted=[...arr].map(v=>clamp(Math.round(v),1,len-1)).sort((a,b)=>a-b),out=[];
    for(const p of sorted)if(!out.length||p-out[out.length-1]>=2)out.push(p);return out;
  }

  function autoDetectX(){if(!workingData)return;splitsX=detectAxis('x',.025);splitsY=[];updateSplitUI();}
  function autoDetectGrid(){if(!workingData)return;splitsX=detectAxis('x',.05);splitsY=detectAxis('y',.10);updateSplitUI();drawEditor();renderAll();}
  function setHalf(){splitsX=[Math.floor(W/2)];splitsY=[];updateSplitUI();drawEditor();renderAll();}

  function updateSplitUI(){
    const box=$('#splitList');box.innerHTML='';
    if(!splitsX.length&&!splitsY.length){box.innerHTML='<span class="small">当前没有分割线，将作为 1 张图片输出。</span>';return;}
    splitsX.forEach((x,i)=>{const c=document.createElement('span');c.className='chip';c.innerHTML=`竖切 ${i+1}: X=${x}px <button title="删除">×</button>`;c.querySelector('button').onclick=()=>{splitsX.splice(i,1);updateSplitUI();drawEditor();renderAll();};box.appendChild(c);});
    splitsY.forEach((y,i)=>{const c=document.createElement('span');c.className='chip y';c.innerHTML=`横切 ${i+1}: Y=${y}px <button title="删除">×</button>`;c.querySelector('button').onclick=()=>{splitsY.splice(i,1);updateSplitUI();drawEditor();renderAll();};box.appendChild(c);});
  }

  function boundariesX(){return[0,...dedupe(splitsX,W),W];}
  function boundariesY(){return[0,...dedupe(splitsY,H),H];}
  function regionList(){
    const bx=boundariesX(),by=boundariesY(),out=[];let index=0;
    for(let row=0;row<by.length-1;row++)for(let col=0;col<bx.length-1;col++){
      if(bx[col+1]>bx[col]&&by[row+1]>by[row])out.push({x0:bx[col],x1:bx[col+1],y0:by[row],y1:by[row+1],row,col,index:index++});
    }
    return out;
  }

  function bbox(x0,x1,y0,y1,t=0){
    const d=workingData.data;let minX=x1,minY=y1,maxX=-1,maxY=-1;
    for(let y=y0;y<y1;y++){let i=(y*W+x0)*4+3;for(let x=x0;x<x1;x++,i+=4){if(d[i]>t){if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;if(y>maxY)maxY=y;}}}
    return maxX<minX?null:{x:minX,y:minY,w:maxX-minX+1,h:maxY-minY+1};
  }

  function detectTextZones(){
    const zones=[],t=detectThreshold(),d=workingData.data;
    for(const r of regionList()){
      const b=bbox(r.x0,r.x1,r.y0,r.y1,t);if(!b||b.h<20)continue;
      const occ=new Uint32Array(b.h);
      for(let yy=0;yy<b.h;yy++){const y=b.y+yy;let i=(y*W+b.x)*4+3;for(let xx=0;xx<b.w;xx++,i+=4)if(d[i]>t)occ[yy]++;}
      const start=Math.floor(b.h*.42),end=Math.floor(b.h*.92);let run=-1,candidates=[];
      for(let yy=start;yy<=end;yy++){
        if(occ[yy]===0){if(run<0)run=yy;}
        else if(run>=0){candidates.push([run,yy-1]);run=-1;}
      }
      if(run>=0)candidates.push([run,end]);
      candidates=candidates.filter(([a,z])=>z-a+1>=Math.max(3,Math.floor(H*.003))&&a>0&&z<b.h-1);
      candidates.sort((p,q)=>(q[1]-q[0])-(p[1]-p[0]));
      let chosen=null;
      for(const g of candidates){
        const belowStart=g[1]+1;let belowPixels=0,abovePixels=0;
        for(let yy=0;yy<g[0];yy++)abovePixels+=occ[yy];for(let yy=belowStart;yy<b.h;yy++)belowPixels+=occ[yy];
        if(abovePixels>0&&belowPixels>0&&(b.h-belowStart)<=b.h*.38){chosen=g;break;}
      }
      if(!chosen)continue;
      const cutY=b.y+chosen[1]+1;zones.push({x:b.x,y:cutY,w:b.w,h:(b.y+b.h)-cutY});
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
  function setMode(m){
    mode=m;dragStart=null;brushDrawing=false;
    ['splitXMode','splitYMode','rectErase','brushErase'].forEach(id=>$('#'+id).classList.remove('active'));
    if(m==='splitX')$('#splitXMode').classList.add('active');if(m==='splitY')$('#splitYMode').classList.add('active');if(m==='rect')$('#rectErase').classList.add('active');if(m==='brush')$('#brushErase').classList.add('active');
    $('#modeTip').textContent=m==='splitX'?'点击原图添加竖向分割线':m==='splitY'?'点击原图添加横向分割线':m==='rect'?'拖出矩形，矩形内像素变透明':m==='brush'?'按住拖动画笔删除':'普通预览模式';drawEditor();
  }

  function drawEditor(extra=null){
    if(!workingData)return;editor.width=W;editor.height=H;ectx.clearRect(0,0,W,H);ectx.putImageData(workingData,0,0);
    ectx.save();ectx.lineWidth=Math.max(2,W/700);ectx.setLineDash([Math.max(8,W/140),Math.max(5,W/220)]);
    ectx.strokeStyle='#2563eb';for(const x of splitsX){ectx.beginPath();ectx.moveTo(x,0);ectx.lineTo(x,H);ectx.stroke();}
    ectx.strokeStyle='#d97706';for(const y of splitsY){ectx.beginPath();ectx.moveTo(0,y);ectx.lineTo(W,y);ectx.stroke();}
    ectx.setLineDash([]);
    if(extra&&extra.type==='rect'){ectx.fillStyle='rgba(220,38,38,.14)';ectx.strokeStyle='#dc2626';ectx.fillRect(extra.x,extra.y,extra.w,extra.h);ectx.strokeRect(extra.x,extra.y,extra.w,extra.h);}
    ectx.restore();
  }

  editor.addEventListener('pointerdown',ev=>{
    if(mode==='none')return;editor.setPointerCapture(ev.pointerId);const p=canvasPoint(ev);
    if(mode==='splitX'){splitsX=dedupe([...splitsX,p.x],W);updateSplitUI();drawEditor();renderAll();return;}
    if(mode==='splitY'){splitsY=dedupe([...splitsY,p.y],H);updateSplitUI();drawEditor();renderAll();return;}
    if(mode==='rect'){dragStart=p;drawEditor({type:'rect',x:p.x,y:p.y,w:0,h:0});}
    if(mode==='brush'){pushUndo();brushDrawing=true;eraseCircle(p.x,p.y,Math.max(1,parseInt($('#brushSize').value||'28',10))/2);rebuildRaw();drawEditor();}
  });
  editor.addEventListener('pointermove',ev=>{
    const p=canvasPoint(ev);
    if(mode==='rect'&&dragStart)drawEditor({type:'rect',x:dragStart.x,y:dragStart.y,w:p.x-dragStart.x,h:p.y-dragStart.y});
    if(mode==='brush'&&brushDrawing){eraseCircle(p.x,p.y,Math.max(1,parseInt($('#brushSize').value||'28',10))/2);rebuildRaw();drawEditor();}
  });
  editor.addEventListener('pointerup',ev=>{
    const p=canvasPoint(ev);
    if(mode==='rect'&&dragStart){pushUndo();const x=Math.min(dragStart.x,p.x),y=Math.min(dragStart.y,p.y),w=Math.abs(p.x-dragStart.x),h=Math.abs(p.y-dragStart.y);eraseRectPixels(x,y,w,h);dragStart=null;rebuildAfterEdit(false);}
    if(mode==='brush'&&brushDrawing){brushDrawing=false;rebuildAfterEdit(false);}
  });

  function outputPlan(){
    const pad=Math.max(0,parseInt($('#padding').value||'0',10)),items=[];
    for(const cell of regionList()){
      const content=bbox(cell.x0,cell.x1,cell.y0,cell.y1,0);if(!content)continue;
      const cellRect={x:cell.x0,y:cell.y0,w:cell.x1-cell.x0,h:cell.y1-cell.y0};
      const copy=trimOn()?content:cellRect;items.push({cell,content,copy});
    }
    if(!items.length)return{items:[],outW:1,outH:1,pad};
    const maxW=Math.max(...items.map(v=>v.copy.w)),maxH=Math.max(...items.map(v=>v.copy.h));
    return{items,outW:maxW+pad*2,outH:maxH+pad*2,pad};
  }

  function makeTransparentOutput(item,outW,outH,pad){
    const c=document.createElement('canvas');c.width=outW;c.height=outH;const ctx=c.getContext('2d',{alpha:true});ctx.clearRect(0,0,outW,outH);
    const {copy,content}=item,data=rawCtx.getImageData(copy.x,copy.y,copy.w,copy.h);
    let dx=pad,dy=pad;
    if(centerOn()){
      if(trimOn()){
        dx=Math.floor((outW-copy.w)/2);dy=Math.floor((outH-copy.h)/2);
      }else{
        dx=Math.floor((outW-content.w)/2)-(content.x-copy.x);
        dy=Math.floor((outH-content.h)/2)-(content.y-copy.y);
      }
    }
    ctx.putImageData(data,dx,dy);return c;
  }

  function hexToRgb(hex){const n=parseInt(hex.slice(1),16);return[(n>>16)&255,(n>>8)&255,n&255];}
  function applyBackground(base){
    if($('#bgMode').value==='transparent')return base;
    const c=document.createElement('canvas');c.width=base.width;c.height=base.height;const ctx=c.getContext('2d',{alpha:false}),[br,bg,bb]=hexToRgb($('#bgColor').value);
    const src=base.getContext('2d').getImageData(0,0,base.width,base.height),d=src.data;
    for(let i=0;i<d.length;i+=4){const a=d[i+3]/255;d[i]=Math.round(d[i]*a+br*(1-a));d[i+1]=Math.round(d[i+1]*a+bg*(1-a));d[i+2]=Math.round(d[i+2]*a+bb*(1-a));d[i+3]=255;}
    ctx.putImageData(src,0,0);return c;
  }
  function makeOutputCanvas(item,outW,outH,pad){return applyBackground(makeTransparentOutput(item,outW,outH,pad));}

  function blankCanvas(w,h){
    const c=document.createElement('canvas');c.width=w;c.height=h;const ctx=c.getContext('2d',{alpha:$('#bgMode').value==='transparent'});
    if($('#bgMode').value==='solid'){ctx.fillStyle=$('#bgColor').value;ctx.fillRect(0,0,w,h);}else ctx.clearRect(0,0,w,h);return c;
  }
  function padCanvasLossless(base,target){
    if(target<base.width||target<base.height)throw new Error('target smaller than source');
    const c=blankCanvas(target,target),ctx=c.getContext('2d'),data=base.getContext('2d').getImageData(0,0,base.width,base.height);
    ctx.putImageData(data,Math.floor((target-base.width)/2),Math.floor((target-base.height)/2));return c;
  }
  function squareCanvasLossless(base){return padCanvasLossless(base,Math.max(base.width,base.height));}
  function resizeCanvasCompat(base,target){
    const c=blankCanvas(target,target),ctx=c.getContext('2d');ctx.imageSmoothingEnabled=true;ctx.imageSmoothingQuality='high';ctx.drawImage(base,0,0,target,target);return c;
  }

  function selectedIcoSizes(){return[...document.querySelectorAll('.icoSize:checked')].map(e=>parseInt(e.value,10)).filter(v=>v>=1&&v<=256).sort((a,b)=>a-b);}
  function icoMode(){return $('#icoMode').value;}
  function exportMode(){return $('#exportMode').value;}
  function canvasBytes(c){return new Promise(res=>c.toBlob(async b=>res(new Uint8Array(await b.arrayBuffer())),'image/png'));}

  async function buildIco(base){
    const square=squareCanvasLossless(base),side=square.width,mode=icoMode(),chosen=selectedIcoSizes();
    if(mode==='strict'){
      if(side>256)throw new Error(`严格无损 ICO 不可生成：当前方形画布 ${side}px > 256px。`);
      const sizes=[...new Set([side,...chosen.filter(s=>s>=side)])].sort((a,b)=>a-b),entries=[];
      for(const size of sizes){const layer=size===side?square:padCanvasLossless(square,size);entries.push({size,data:await canvasBytes(layer)});}
      return{blob:PNGSplitterBinary.makeIco(entries),sizes,lossless:true};
    }
    const sizes=chosen.length?chosen:[16,32,48,256],entries=[];
    for(const size of sizes){const layer=size===side?square:resizeCanvasCompat(square,size);entries.push({size,data:await canvasBytes(layer)});}
    return{blob:PNGSplitterBinary.makeIco(entries),sizes,lossless:false};
  }

  function saveBlob(blob,name){const a=document.createElement('a'),u=URL.createObjectURL(blob);a.href=u;a.download=name;document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(u),2000);}
  function downloadPng(c,name){c.toBlob(blob=>blob&&saveBlob(blob,name),'image/png');}
  async function downloadIco(c,name){try{const result=await buildIco(c);saveBlob(result.blob,name);}catch(e){alert(e.message);}}

  function updateIcoUI(){const visible=exportMode()!=='png';$('#icoOptions').classList.toggle('hidden',!visible);}

  function renderAll(){
    if(!workingData)return;rebuildRaw();updateSplitUI();updateIcoUI();
    const {items,outW,outH,pad}=outputPlan(),pre=$('#previews');pre.innerHTML='';
    const side=Math.max(outW,outH),strictIcoOk=side<=256;
    items.forEach((item,i)=>{
      const c=makeOutputCanvas(item,outW,outH,pad),card=document.createElement('div');card.className='previewCard';
      const title=document.createElement('div');title.className='previewTitle';title.innerHTML=`<b>图 ${String(i+1).padStart(2,'0')}</b><span class="small">${outW} × ${outH}</span>`;
      const wrap=document.createElement('div');wrap.className='previewCanvasWrap';wrap.appendChild(c);
      const row=document.createElement('div');row.style='margin-top:9px;display:flex;gap:8px;flex-wrap:wrap';
      if(exportMode()==='png'||exportMode()==='both'){
        const b=document.createElement('button');b.className='primary';b.textContent='下载 PNG';b.onclick=()=>downloadPng(c,`${sourceName}-${String(i+1).padStart(2,'0')}.png`);row.appendChild(b);
      }
      if(exportMode()==='ico'||exportMode()==='both'){
        const b=document.createElement('button');b.className='good';b.textContent=icoMode()==='strict'?'下载 ICO（严格）':'下载 ICO（兼容多尺寸）';
        b.disabled=icoMode()==='strict'&&!strictIcoOk;b.onclick=()=>downloadIco(c,`${sourceName}-${String(i+1).padStart(2,'0')}${icoMode()==='compat'?'-compat':''}.ico`);row.appendChild(b);
      }
      card.append(title,wrap,row);pre.appendChild(card);
    });
    const grid=`${splitsY.length+1} 行 × ${splitsX.length+1} 列`;
    $('#stats').innerHTML=`原图：<b>${W} × ${H}</b>　｜　分割结构：<b>${grid}</b>　｜　非空输出：<b>${items.length}</b> 张`;
    const icoText=icoMode()==='strict'?(strictIcoOk?`严格无损可用，原始方形 ${side}px`:`严格无损不可用：${side}px > 256px`):`兼容多尺寸：${selectedIcoSizes().join('/')||'16/32/48/256'}（会重采样）`;
    $('#outputInfo').innerHTML=`统一输出：<b>${outW} × ${outH}</b> px　｜　内容居中：<b>${centerOn()?'开启':'关闭'}</b>　｜　背景：<b>${$('#bgMode').value==='transparent'?'透明 Alpha':'纯色 '+$('#bgColor').value}</b>　｜　默认 PNG 内容缩放：<b>0 次</b>　｜　ICO：<b>${icoText}</b>`;
  }

  async function downloadAll(){
    const {items,outW,outH,pad}=outputPlan(),files=[],mode=exportMode();
    if(!items.length){alert('没有可输出的非空图块。');return;}
    if((mode==='ico'||mode==='both')&&icoMode()==='strict'&&Math.max(outW,outH)>256){
      if(mode==='ico'){alert(`严格无损 ICO 不可生成：当前方形画布 ${Math.max(outW,outH)}px > 256px。请继续使用无损 PNG，或主动切换到“兼容多尺寸”模式。`);return;}
      alert('当前尺寸超过 256px，严格无损 ICO 将跳过；本次仍会输出无损 PNG。');
    }
    for(let i=0;i<items.length;i++){
      const n=String(i+1).padStart(2,'0'),base=`${sourceName}-${n}`,c=makeOutputCanvas(items[i],outW,outH,pad);
      if(mode==='png'||mode==='both')files.push({name:`${base}.png`,data:await canvasBytes(c)});
      if(mode==='ico'||mode==='both'){
        if(icoMode()==='strict'&&Math.max(outW,outH)>256)continue;
        const ico=await buildIco(c);files.push({name:`${base}${ico.lossless?'':'-compat'}.ico`,data:new Uint8Array(await ico.blob.arrayBuffer())});
      }
    }
    if(files.length===1){const f=files[0];saveBlob(new Blob([f.data],{type:f.name.endsWith('.ico')?'image/x-icon':'image/png'}),f.name);return;}
    saveBlob(PNGSplitterBinary.makeZip(files),`${sourceName}-split-${items.length}.zip`);
  }

  drop.addEventListener('click',()=>fileInput.click());fileInput.addEventListener('change',e=>loadFile(e.target.files[0]));
  ['dragenter','dragover'].forEach(n=>drop.addEventListener(n,e=>{e.preventDefault();drop.classList.add('drag')}));
  ['dragleave','drop'].forEach(n=>drop.addEventListener(n,e=>{e.preventDefault();drop.classList.remove('drag')}));drop.addEventListener('drop',e=>loadFile(e.dataTransfer.files[0]));

  $('#autoSplitX').onclick=()=>{autoDetectX();drawEditor();renderAll();};
  $('#autoGrid').onclick=autoDetectGrid;$('#halfSplit').onclick=setHalf;
  $('#splitXMode').onclick=()=>setMode(mode==='splitX'?'none':'splitX');$('#splitYMode').onclick=()=>setMode(mode==='splitY'?'none':'splitY');
  $('#clearSplits').onclick=()=>{splitsX=[];splitsY=[];updateSplitUI();drawEditor();renderAll();};
  $('#autoText').onclick=autoRemoveText;$('#rectErase').onclick=()=>setMode(mode==='rect'?'none':'rect');$('#brushErase').onclick=()=>setMode(mode==='brush'?'none':'brush');
  $('#undo').onclick=restoreUndo;$('#restore').onclick=()=>{if(!originalData)return;workingData=cloneImageData(originalData);undoStack=[];autoDetectX();rebuildAfterEdit(false);setMode('none');};
  $('#alphaNoise').onchange=()=>{autoDetectX();drawEditor();renderAll();};$('#minGap').onchange=()=>{autoDetectX();drawEditor();renderAll();};
  $('#trim').onchange=renderAll;$('#centerContent').onchange=renderAll;$('#padding').oninput=renderAll;
  $('#bgMode').onchange=()=>{$('#colorWrap').classList.toggle('hidden',$('#bgMode').value!=='solid');renderAll();};$('#bgColor').oninput=renderAll;
  $('#exportMode').onchange=()=>{updateIcoUI();renderAll();};$('#icoMode').onchange=renderAll;document.querySelectorAll('.icoSize').forEach(e=>e.onchange=renderAll);
  $('#downloadAll').onclick=downloadAll;
})();
