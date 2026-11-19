const net=require('net');
module.exports=function createGateway(options={}){
const sockets = new Set(); let errorGroup = false, requests = 0, writes = 0, analog1=12000, analog2=25000, offline=false, levelOverride=null,faultOverride=0;
const remoteTargets=new Map(),remoteLevels=new Map(),writeFrames=[];let stageHook=null,statusGroupOverride=null;
const controls=new Map();let nextUnknown=false,nextFailure=false,dropReply=false,dropResetReply=false;
function control(slot){if(!controls.has(slot))controls.set(slot,{normal:0,stop:0,error:0,mode:1,command:0,normalCount:0,stopCount:0});return controls.get(slot)}
function put32(words, at, value) { words[at] = value >>> 16; words[at + 1] = value & 65535; }
function image(start, count) {
  if(options.remoteLevel&&start>=31000&&start<31256){const t=remoteTargets.get(Math.floor((start-31000)/4)+1)||{words:[0,0],ready:0};return [...t.words,t.ready,0].slice((start-31000)%4,(start-31000)%4+count)}
  if(start>=30000&&start<30768){const slot=Math.floor((start-30000)/12)+1,c=control(slot),r=new Array(12).fill(0);r[0]=c.command;r[1]=c.normal;r[2]=c.normal?c.error:0;put32(r,4,c.normalCount);r[7]=c.stop?1:0;r[8]=c.stop;r[9]=c.stop?c.error:0;put32(r,10,c.stopCount);return r.slice((start-30000)%12,(start-30000)%12+count)}
  const header = new Array(31).fill(0);header[30]=options.remoteLevel?1:0; header[2] = 3; header[3] = 64; header[9] = 91; header[27] = 16; header[28] = 3; header[29] = 12;
  if(start>=0&&start<31)return header.slice(start,start+count);
  if (start >= 1000 && start < 2536) {
    const slot = Math.floor((start - 1000) / 24) + 1;
    const summary = new Array(24).fill(0); summary[0] = offline?0:3; summary[1] = 127; summary[3] = 42;
    put32(summary, 4, levelOverride===null?(remoteLevels.get(slot)??slot * 10000):levelOverride); summary[6] = 2800; summary[7] = 8320; summary[9] = options.control?(options.remoteFlag===false?0:0x8000)|control(slot).mode:1; summary[10]=faultOverride;summary[21] = 1;
    return summary.slice((start - 1000) % 24, (start - 1000) % 24 + count);
  }
  const slot = Math.floor((start - 3000) / 144) + 1, offset = (start - 3000) % 144;
  const detail = new Array(144).fill(0); put32(detail, 0, levelOverride===null?(remoteLevels.get(slot)??slot * 10000):levelOverride);
  for (let i = 0; i < 16; i++) detail[6 + i] = i === 0 ? 0 : 2500 + i * 100;
  detail[27]=analog1;detail[28]=analog2; detail[23] = 65535; detail[26] = 2800; detail[50] = 8320; detail[51] = 1; detail[55] = 1; detail[57] = 42;
  for (let i = 0; i < 4; i++) detail[58 + i] = 65535;
  for (let i = 0; i < 6; i++) { detail[64 + i * 12] = i === 0 && errorGroup ? 3 : 9; detail[67 + i * 12] = 21; }
  if(options.optionalDensityUnsupported)detail[64+4*12]=4;
  if(statusGroupOverride!==null)detail[64+2*12]=statusGroupOverride;
  return detail.slice(offset, offset + count);
}
const gateway = net.createServer(socket => {
  sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
  let bytes = Buffer.alloc(0);
  socket.on('data', data => {
    bytes = Buffer.concat([bytes, data]);
    while (bytes.length >= 7 && bytes.length >= bytes.readUInt16BE(4) + 6) {
      const length = bytes.readUInt16BE(4) + 6, frame = bytes.subarray(0, length); bytes = bytes.subarray(length);
      requests++; const fc = frame[7];
      if(fc===16&&options.remoteLevel){const at=frame.readUInt16BE(8),n=frame.readUInt16BE(10),slot=Math.floor((at-31000)/4)+1;
        if(at!==31000+4*(slot-1)||n!==2||frame[12]!==4)throw Error('bad remote stage');writes++;const words=[frame.readUInt16BE(13),frame.readUInt16BE(15)];writeFrames.push({fc,at,values:words});remoteTargets.set(slot,{words,ready:1});
        const reply=Buffer.from(frame.subarray(0,12));reply.writeUInt16BE(6,4);Promise.resolve(stageHook?.()).then(()=>socket.write(reply)).catch(()=>socket.destroy());continue;}
      if(fc===6&&options.control){writes++;const at=frame.readUInt16BE(8),v=frame.readUInt16BE(10),slot=Math.floor((at-30000)/12)+1,offset=(at-30000)%12,c=control(slot);
       if(options.strictControl&&v!==0&&((offset===0&&(c.normal!==0||c.stop!==0))||(offset===7&&c.stop!==0))){const reply=Buffer.alloc(9);frame.copy(reply,0,0,7);reply.writeUInt16BE(3,4);reply[7]=0x86;reply[8]=6;socket.write(reply);continue;}
       writeFrames.push({fc,at,values:[v]});
       if(v!==0){if(offset===0)c.normalCount++;if(offset===7)c.stopCount++;}
       if(options.remoteLevel&&offset===0&&v===11){const t=remoteTargets.get(slot);if(!t?.ready||c.normal||c.stop)throw Error('bad remote submit');t.ready=0;c.command=11;c.normal=nextUnknown?5:nextFailure?4:3;c.error=nextUnknown?14:nextFailure?4:0;nextUnknown=false;nextFailure=false;if(c.normal===3)remoteLevels.set(slot,((t.words[0]<<16)|t.words[1]));if(dropReply){dropReply=false;socket.destroy()}else socket.write(Buffer.from(frame));continue;}
       if(offset===0){c.command=v;c.normal=v===0?0:nextUnknown?5:nextFailure?4:3;c.error=nextUnknown?6:nextFailure?4:0;nextUnknown=false;nextFailure=false;if(v&&c.normal===3)c.mode=({1:1,2:16,3:32,4:4,5:2,6:256,7:64,9:8,10:128})[v]||1;}
       if(offset===7){c.stop=v===0?0:3;if(v)c.mode=4;}
       if(dropReply||(v===0&&dropResetReply)){dropReply=false;dropResetReply=false;socket.destroy()}else socket.write(Buffer.from(frame));continue;
      }
      if (fc !== 4 && fc !== 3) { writes++; socket.destroy(); continue; }
      const words = image(frame.readUInt16BE(8), frame.readUInt16BE(10));
      const reply = Buffer.alloc(9 + words.length * 2); frame.copy(reply, 0, 0, 7); reply.writeUInt16BE(3 + words.length * 2, 4);
      reply[7] = fc; reply[8] = words.length * 2; words.forEach((value, i) => reply.writeUInt16BE(value, 9 + i * 2)); socket.write(reply);
    }
  });
});

return {writeFrames,setStatusGroup(value){statusGroupOverride=value},setStageHook(fn){stageHook=fn},server:gateway,sockets,get requests(){return requests},get writes(){return writes},setInstrumentFault(value){faultOverride=value},setControlState(slot,normal,stop=0){const c=control(slot);if(normal!==c.normal)c.normalCount++;if(stop!==c.stop)c.stopCount++;c.normal=normal;c.stop=stop;c.command=normal?5:0;c.error=0},failNext(){nextFailure=true},unknownNext(){nextUnknown=true},dropNextReset(){dropResetReply=true},dropNext(){dropReply=true},setError(value){errorGroup=value},setAnalog(a,b){analog1=a;analog2=b},setOffline(value){offline=value},setLevelMm(value){levelOverride=value===null?null:Math.round(value*10)},async listen(){await new Promise(resolve=>gateway.listen(0,'127.0.0.1',resolve));return gateway.address().port},close(){for(const socket of sockets)socket.destroy();gateway.close()}};
};
