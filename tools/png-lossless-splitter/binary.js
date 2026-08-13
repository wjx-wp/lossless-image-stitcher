(() => {
  const crcTable=(()=>{const t=new Uint32Array(256);for(let n=0;n<256;n++){let c=n;for(let k=0;k<8;k++)c=(c&1)?0xedb88320^(c>>>1):(c>>>1);t[n]=c>>>0;}return t;})();
  function crc32(bytes){let c=0xffffffff;for(const b of bytes)c=crcTable[(c^b)&255]^(c>>>8);return(c^0xffffffff)>>>0;}
  const u16=n=>new Uint8Array([n&255,(n>>>8)&255]);
  const u32=n=>new Uint8Array([n&255,(n>>>8)&255,(n>>>16)&255,(n>>>24)&255]);
  function concat(chunks){const n=chunks.reduce((s,a)=>s+a.length,0),o=new Uint8Array(n);let p=0;for(const a of chunks){o.set(a,p);p+=a.length;}return o;}
  function dosTimeDate(){const d=new Date(),time=((d.getHours()&31)<<11)|((d.getMinutes()&63)<<5)|((Math.floor(d.getSeconds()/2))&31),date=(((d.getFullYear()-1980)&127)<<9)|(((d.getMonth()+1)&15)<<5)|(d.getDate()&31);return{time,date};}

  function makeZip(files){
    const enc=new TextEncoder(),locals=[],centrals=[];let offset=0;const dt=dosTimeDate();
    for(const f of files){
      const name=enc.encode(f.name),data=f.data,crc=crc32(data);
      const local=concat([u32(0x04034b50),u16(20),u16(0x0800),u16(0),u16(dt.time),u16(dt.date),u32(crc),u32(data.length),u32(data.length),u16(name.length),u16(0),name,data]);
      locals.push(local);
      const central=concat([u32(0x02014b50),u16(20),u16(20),u16(0x0800),u16(0),u16(dt.time),u16(dt.date),u32(crc),u32(data.length),u32(data.length),u16(name.length),u16(0),u16(0),u16(0),u16(0),u32(0),u32(offset),name]);
      centrals.push(central);offset+=local.length;
    }
    const localBlob=concat(locals),centralBlob=concat(centrals);
    const end=concat([u32(0x06054b50),u16(0),u16(0),u16(files.length),u16(files.length),u32(centralBlob.length),u32(localBlob.length),u16(0)]);
    return new Blob([localBlob,centralBlob,end],{type:'application/zip'});
  }

  function makeIco(entries){
    if(!entries.length)throw new Error('ICO entries cannot be empty');
    const headerSize=6+16*entries.length;
    let offset=headerSize;
    const dir=[u16(0),u16(1),u16(entries.length)],images=[];
    for(const entry of entries){
      const size=entry.size,data=entry.data;
      if(size<1||size>256)throw new Error('ICO size must be 1~256');
      const dim=size===256?0:size;
      dir.push(new Uint8Array([dim,dim,0,0]),u16(1),u16(32),u32(data.length),u32(offset));
      images.push(data);offset+=data.length;
    }
    return new Blob([...dir,...images],{type:'image/x-icon'});
  }

  window.PNGSplitterBinary={makeZip,makeIco};
})();
