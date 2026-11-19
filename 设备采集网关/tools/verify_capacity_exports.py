"""Read-only slot64 CSV verification against the isolated preview created by verify_v3_live."""
from pathlib import Path
import json,urllib.request,http.cookiejar,io,csv,time,hashlib
R=Path(__file__).resolve().parents[1]
p=json.loads((R/'tmp/capacity64-preview.json').read_text());base=p['url']
op=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
req=urllib.request.Request(base+'/api/login',json.dumps({'password':p['password']}).encode(),{'Content-Type':'application/json'},method='POST')
with op.open(req,timeout=5) as response:login=json.load(response)
now=int(time.time()*1000)
with op.open(base+f'/api/history.csv?from={now-86400000}&to={now+60000}&slot=64',timeout=5) as response:rows=list(csv.reader(io.StringIO(response.read().decode('utf-8-sig'))))
assert len(rows)>1 and len(rows[0])==17 and all(row[1]=='64' and row[15]=='液位计64' for row in rows[1:])
report=dict(date='2026-10-19',software='0.4.0',passed=True,rows=len(rows)-1,slot=64,all_names_match=True,scope='Synthetic 64-meter simulation; CSV queried through real Web authentication/proxy and SQLite history',binaries={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in (R/'output/windows-amd64').glob('gateway-*.exe')})
(R/'tests/capacity64-history-export-20261019.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report,ensure_ascii=False))
