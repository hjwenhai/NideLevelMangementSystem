"""V3.1-D2 TCP diagnostics. No automatic reset/retry or physical-action replay."""
import argparse, socket, struct, json

def exchange(host,port,unit,pdu):
    with socket.create_connection((host,port),timeout=3) as s:
        s.settimeout(3);s.sendall(struct.pack('>HHHB',1,0,len(pdu)+1,unit)+pdu)
        def exact(n):
            data=b''
            while len(data)<n:
                part=s.recv(n-len(data))
                if not part:raise RuntimeError('connection closed; query status before any retry')
                data+=part
            return data
        tx,protocol,length,u=struct.unpack('>HHHB',exact(7))
        if (tx,protocol,u)!=(1,0,unit) or not 2<=length<=254:raise RuntimeError('invalid MBAP')
        out=exact(length-1)
        if out[0]&128:raise RuntimeError(f'Modbus exception {out[1]}')
        if out[0]!=pdu[0]:raise RuntimeError('unexpected function')
        return out
def read(args,address,count,fc=4):
    out=exchange(args.host,args.port,args.unit,struct.pack('>BHH',fc,address,count))
    if len(out)!=2+2*count or out[1]!=2*count:raise RuntimeError('invalid read response')
    return list(struct.unpack('>'+str(count)+'H',out[2:]))
def u32(w):return (w[0]<<16)|w[1]
def signed(v,bits):return v if v<(1<<(bits-1)) else v-(1<<bits)
def control(args):
    w=read(args,30000+12*(args.slot-1),12,3)
    return dict(slot=args.slot,command=w[0],state=w[1],error=w[2],accepted_count=u32(w[4:6]),stop_request=w[7],stop_state=w[8],stop_error=w[9],stop_count=u32(w[10:12]))
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--host',default='127.0.0.1');p.add_argument('--port',type=int,default=1502);p.add_argument('--unit',type=int,default=1)
    sub=p.add_subparsers(dest='operation',required=True)
    r=sub.add_parser('read');r.add_argument('address',type=int);r.add_argument('count',type=int);r.add_argument('--fc',type=int,choices=[3,4],default=4)
    sub.add_parser('batch');sub.add_parser('summary').add_argument('--meters',type=int,choices=range(1,65),default=64)
    for name in ['action','stop','reset','status']:
        q=sub.add_parser(name);q.add_argument('--slot',type=int,choices=range(1,65),required=True)
        if name=='action':q.add_argument('--command',type=int,choices=[1,2,3,4,5,6,7,9,10],required=True)
        if name=='reset':q.add_argument('--stop',action='store_true')
    args=p.parse_args()
    if args.operation=='read':
        if not 0<=args.address<=65535 or not 1<=args.count<=125 or args.address+args.count>65536:p.error('invalid address/count')
        result=read(args,args.address,args.count,args.fc)
    else:
        device=read(args,0,30)
        if device[2]!=3 or (device[3],device[27]) not in [(32,8),(64,16)]:raise RuntimeError('Expected V3 / (32,8) or (64,16) capacity')
        if hasattr(args,'slot') and args.slot>device[3]:raise RuntimeError('Slot exceeds device capacity')
        if args.operation=='summary' and args.meters>device[3]:raise RuntimeError('Requested meters exceed device capacity')
        if args.operation=='batch':
            values=read(args,15000,96);status=read(args,15100,96);result=[]
            if device[3]==64:values+=read(args,15200,96);status+=read(args,15300,96)
            for i in range(device[3]):
                b=i*3;v=status[b+2]
                result.append(dict(slot=i+1,level_mm=signed(u32(values[b:b+2]),32)/10 if v&1 else None,temperature_c=signed(values[b+2],16)/100 if v&2 else None,density_kg_m3=status[b]/10 if v&4 else None,quality=status[b+1],valid=v))
        elif args.operation=='summary':
            result=[]
            for start in range(0,args.meters,5):
                words=read(args,1000+start*24,min(5,args.meters-start)*24)
                for i in range(0,len(words),24):
                    w=words[i:i+24];v=w[1]
                    result.append(dict(slot=start+i//24+1,quality=w[0],valid=v,level_mm=signed(u32(w[4:6]),32)/10 if v&1 else None,temperature_c=signed(w[6],16)/100 if v&2 else None,density_kg_m3=w[7]/10 if v&4 else None,age_ms=u32(w[16:18]),port=w[18],address=w[19],binding_version=u32(w[20:22])))
        else:
            if device[28]!=3 or device[29]!=12:raise RuntimeError('D2 control capabilities unavailable')
            if args.operation!='status':
                address=30000+12*(args.slot-1);value=0
                if args.operation=='action':value=args.command
                elif args.operation=='stop':address+=7;value=1
                elif args.stop:address+=7
                pdu=struct.pack('>BHH',6,address,value)
                if exchange(args.host,args.port,args.unit,pdu)!=pdu:raise RuntimeError('invalid FC06 ACK; query state')
            result=control(args)
    print(json.dumps(result,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
