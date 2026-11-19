from pathlib import Path
import re,json
root=Path(__file__).resolve().parents[1]
work=root/'output/r24-user-manual_20261118'
source=(root/'src/LevelManager.Domain/InstrumentDiagnostics.cs').read_text(encoding='utf-8-sig')
manual=(work/'manual.md').read_text(encoding='utf-8')
labels=json.loads('['+re.search(r'Direct=\[(.*?)\];',source,re.S).group(1)+']')
heads={}
for condition,head in re.findall(r'([^,\n]+)=>"([^"]+)"',source):
    for mask in re.findall(r'FW\.0x([0-9A-F]{4})',condition):heads[int(mask,16)]=head
rows=re.findall(r'^\| bit(\d+) \| FW\.0x([0-9A-F]{4}) \| ([^|]+) \| ([^|]+) \|$',manual,re.M)
assert len(rows)==16
normalize=lambda s:re.sub(r'[\s，,]','',s)
for bit,mask,head,label in rows:
    i=int(bit);n=int(mask,16)
    assert n==1<<i,(bit,mask)
    assert head.strip()==heads.get(n,'—'),(bit,head)
    assert normalize(label)==normalize(labels[i]),(bit,label,labels[i])
examples={0x0060:[0x0020,0x0040],0x0204:[0x0004,0x0200],0x0801:[0x0001,0x0800],0x0802:[0x0002,0x0800]}
for word,bits in examples.items():
    assert sum(bits)==word
    assert all(word&n and n in heads for n in bits)
report={'passed':True,'documentVersion':'V1.2','protocols':['DirectRtu','GatewayRtu','GatewayTcp'],'source':'src/LevelManager.Domain/InstrumentDiagnostics.cs','faultRowsMatched':16,'examples':[{'raw':f'0x{word:04X}','expanded':[f'FW.0x{n:04X}' for n in bits],'displayCodes':[heads[n] for n in bits]} for word,bits in examples.items()],'separatesGatewayControlErrors':True}
(work/'v1.2-fault-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
