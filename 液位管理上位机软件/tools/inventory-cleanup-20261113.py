"""Read-only inventory for the explicitly requested r22/r23 retention cleanup."""
from pathlib import Path
import os,json,time
R=Path(__file__).resolve().parents[1]
O=R/'output/cleanup-records/20261113-r22-r23-only'
O.mkdir(parents=True,exist_ok=True)
rows=[];errors=[];links=[]
def scan(d):
 try:
  entries=list(os.scandir(d))
 except OSError as e:errors.append(dict(path=str(d),error=str(e)));return
 for e in entries:
  try:
   st=e.stat(follow_symlinks=False);rel=Path(e.path).relative_to(R).as_posix()
   if st.st_file_attributes & 0x400:
    links.append(rel);continue
   if e.is_dir(follow_symlinks=False):scan(e.path)
   elif e.is_file(follow_symlinks=False):rows.append(dict(path=rel,bytes=st.st_size,mtime=st.st_mtime_ns))
  except OSError as ex:errors.append(dict(path=e.path,error=str(ex)))
scan(R)
totals={}
for f in rows:
 parts=Path(f['path']).parts
 key='/'.join(parts[:2]) if len(parts)>2 else f['path']
 t=totals.setdefault(key,dict(bytes=0,files=0));t['bytes']+=f['bytes'];t['files']+=1
result=dict(root=str(R),at=time.time(),files=rows,links=links,errors=errors,totals=totals)
(O/'before-inventory.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(totalGiB=round(sum(x['bytes'] for x in rows)/2**30,3),fileCount=len(rows),links=len(links),errors=errors,largest=[dict(path=k,GiB=round(v['bytes']/2**30,3),files=v['files']) for k,v in sorted(totals.items(),key=lambda x:-x[1]['bytes'])[:65]]),ensure_ascii=False,indent=2))
