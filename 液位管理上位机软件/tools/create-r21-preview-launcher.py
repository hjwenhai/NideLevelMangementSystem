from pathlib import Path
r=Path(__file__).resolve().parents[1]
p=r/'tools/Start-R21Preview.cjs';assert not p.exists()
s=(r/'tools/Start-R20Preview.cjs').read_text(encoding='utf-8-sig')
s=s.replace('// Local, disposable r10 viewing environment. Never reuses completed soak databases.','// Independent r21 local preview. Never opens r20 or completed test databases.')
s=s.replace('r20','r21').replace('R20','R21').replace('20261102','20261106')
s=s.replace('output/客户交付资料_r21_20261106/液位管理系统_r21_自动结果复位改进版_20261106','output/客户交付资料_r21_20261107/液位管理系统_r21_客户反馈改进版_20261107')
a=s.index(' const tables=[];');b=s.index(" let cfg=await call('/api/config');",a)
s=s[:a]+''' const tables=[];
 for(const [number,kind,name,total] of [['CT-DEMO-001','Main','3000 m³演示罐容主表',3000],['PT-DEMO-001','StaticPressure','静压力容积修正演示表',12],['DT-DEMO-001','Decimal','五列分区小数演示表',3000]]){
  const decimalRows=Array.from({length:9},(_,i)=>({startHeightMm:0,endHeightMm:12000,number:i+1,centimetreLitres:2500*(i+1),millimetreLitres:250*(i+1)}));
  tables.push(await call('/api/capacity-tables','POST',{id:crypto.randomUUID(),revision:0,number,name,kind,heightUnit:'Millimetre',volumeUnit:kind==='Decimal'?'Litre':'CubicMetre',enabled:true,referenceDensityKgM3:1000,notes:'独立模拟样例，不是检定资料；主表每100mm一节点，小数表为对应累计增量。',points:kind==='Decimal'?[]:Array.from({length:121},(_,i)=>({height:i*100,volume:total*i/120})),decimalRows:kind==='Decimal'?decimalRows:[]}));
 }
''' +s[b:]
s=s.replace("pressure1:{enabled:true,unit:","pressure1:{enabled:i!==3,unit:")
s=s.replace("staticPressureTableId:tables[1].id,waterVolumeM3:1,standardDensity20KgM3:850","staticPressureTableId:tables[1].id,decimalTableId:i===0?tables[2].id:null,baseVolumeMethod:i===0?'MainPlusDecimal':'Interpolate',fractionalMillimetreRule:'Linear',densitySource:i===0?'Manual':'Instrument',manualOperatingDensityKgM3:i<2?800:null,showInstrumentDensity:i!==0,waterVolumeM3:1,standardDensity20KgM3:850")
s=s.replace("console.log('R21 PREVIEW READY", "console.log('R21 PREVIEW READY")
p.write_text(s,encoding='utf-8')
p=r/'output/client-viewing/Start-R21Preview.ps1';assert not p.exists()
s=(r/'output/client-viewing/Start-R20Preview.ps1').read_text(encoding='utf-8-sig').replace('r20','r21').replace('20261102-r21','20261106-r21').replace('客户交付资料_r21_20261102','客户交付资料_r21_20261107').replace('液位管理系统_r21_自动结果复位改进版_20261102','液位管理系统_r21_客户反馈改进版_20261107')
p.write_text(s,encoding='utf-8-sig')
(r/'output/client-viewing/打开r21客户端.cmd').write_bytes(b'@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-R21Preview.ps1"\r\nif errorlevel 1 pause\r\n')
print('Created independent r21 preview driver and client launcher')
