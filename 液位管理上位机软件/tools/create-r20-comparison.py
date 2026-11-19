from pathlib import Path
r=Path(__file__).resolve().parents[1]
p=r/'tools/Start-R20Comparison.cjs';assert not p.exists()
s=(r/'tools/Start-R20Preview.cjs').read_text(encoding='utf-8-sig')
s=s.replace('// Local, disposable r10 viewing environment. Never reuses completed soak databases.','// Pure loopback r20 comparison; never opens the original r20 database or COM ports.')
s=s.replace("'output/'+revision+'-preview-'","'output/'+revision+'-comparison-preview-'")
s=s.replace('output/client-viewing/r20-preview-session.json','output/client-viewing/r20-comparison-session.json')
p.write_text(s,encoding='utf-8')
p=r/'output/client-viewing/Start-R20Comparison.ps1';assert not p.exists()
s=(r/'output/client-viewing/Start-R20Preview.ps1').read_text(encoding='utf-8-sig').replace('r20-preview-session.json','r20-comparison-session.json').replace('output\\r20-preview-','output\\r20-comparison-preview-')
p.write_text(s,encoding='utf-8-sig')
(r/'output/client-viewing/打开r20对照客户端.cmd').write_bytes(b'@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-R20Comparison.ps1"\r\nif errorlevel 1 pause\r\n')
print('Created separate r20 loopback comparison launcher; original r20 launcher/session unchanged')
