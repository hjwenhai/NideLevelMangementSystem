export type TCPService={listen:string,enabled:boolean};
type Interface={name:string,ip:string};
export function splitTCP(listen:string){
 const match=/^(?:\[([^\]]+)\]|([^:]*)):(\d+)$/.exec(listen);
 if(!match)throw Error('已保存的TCP监听地址格式无效，请联系厂家核查');
 const port=Number(match[3]);
 if(!Number.isInteger(port)||port<1||port>65535)throw Error('TCP服务端口须为1～65535的整数');
 return {host:match[1]??match[2],port};
}
function endpoint(host:string,port:number){return (host.includes(':')?'['+host+']':host)+':'+port}
export function withTCPPort(listen:string,port:number){
 if(!Number.isInteger(port)||port<1||port>65535)throw Error('TCP服务端口须为1～65535的整数');
 return endpoint(splitTCP(listen).host,port);
}
// Configuration imports may bring instrument/service settings, never another gateway's IP binding.
export function importedTCP(current:string,incoming:string){return withTCPPort(current,splitTCP(incoming).port)}
export function describeTCP(tcp:TCPService,network?:{actual?:Interface[],settings?:{interfaces?:Interface[]}},authorized=true){
 const {host,port}=splitTCP(tcp.listen),both=host===''||host==='0.0.0.0'||host==='::';
 const saved=(network?.settings?.interfaces||[]).filter(p=>p.name==='eth0'||p.name==='eth1');
 const actual=(network?.actual||[]).filter(p=>(p.name==='eth0'||p.name==='eth1')&&p.ip&&p.ip!=='0.0.0.0');
 const interfaces=actual.length?actual:saved;
 const matching=interfaces.find(p=>p.ip===host)||saved.find(p=>p.ip===host);
 const name=(n:string)=>n==='eth0'?'网口1（eth0）':'网口2（eth1）';
 const selected=both?'两个网口':matching?name(matching.name):host==='127.0.0.1'||host==='::1'?'本机回环地址':'已保存的指定IP';
 const rows=both?interfaces.map(p=>({label:name(p.name),address:endpoint(p.ip,port)})):[{label:selected,address:endpoint(host,port)}];
 const unique=rows.filter((p,i,a)=>a.findIndex(x=>x.address===p.address)===i);
 const state=!authorized?'设备授权未生效，TCP服务未开放':tcp.enabled?'TCP服务已启用':'TCP服务未启用，以下仅为已保存地址';
 const warning=both&&!actual.length?(saved.length?'未读到当前网口地址，以下根据保存配置显示，请核对网络状态。':'未取得网口地址，请在系统维护中检查网络设置；0.0.0.0不是上位机连接地址。'):'';
 return {selected,port,state,warning,rows:unique,copy:unique.map(p=>p.address).join('\n')};
}
