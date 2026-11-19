from pathlib import Path
p=Path(__file__).resolve().parents[1]/'tests/r21-transfer-roundtrip.cjs'
s=p.read_text(encoding='utf-8-sig')
old="assert.equal(copy.productNumber,'');assert.deepEqual(copy.alarms,source.alarms);"
new="assert.equal(copy.productNumber,'');assert.equal(copy.name,source.name+'（副本）');for(const field of ['batch','notes','diameterMm','nominalCapacityM3','mediumId','medium','zone','channelId','shape','heightMm','temperatureMode','atsPointCount','pressure1','pressure2','trends'])assert.deepEqual(copy[field],source[field],'复制字段 '+field);for(const field of ['waterVolumeM3','standardDensity20KgM3','manualOperatingDensityKgM3','densitySource','showInstrumentDensity','baseVolumeMethod','fractionalMillimetreRule'])assert.deepEqual(copy.metering[field],source.metering[field],'复制计量 '+field);assert.deepEqual(copy.alarms,source.alarms);"
assert old in s;p.write_text(s.replace(old,new),encoding='utf-8')
