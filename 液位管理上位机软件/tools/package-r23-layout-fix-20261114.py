from pathlib import Path
import hashlib,json,shutil,subprocess,datetime
R=Path(__file__).resolve().parents[1];B=R/'output/r23-layout-fix_20261114';P=R/'output/r23-feedback_20261113/payload';O=R/'output/客户安装向导_r23_20261113';W=R/'deployment/wizard'
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest().upper()
qa=json.loads((B/'qa/result.json').read_text(encoding='utf-8'));assert qa['passed']
before=json.loads((B/'before-payload.json').read_text(encoding='utf-8'))
for f in before['protected']:assert sha(P/f['path'])==f['sha256'],f['path']
releasePath=O/'release.json';release=json.loads(releasePath.read_text(encoding='utf-8'))
if not (B/'previous-release.json').exists():shutil.copy2(releasePath,B/'previous-release.json')
# Bound deletion: only unused hashed asset files in this r23 payload, after the old client was closed.
src=R/'src/LevelManager.Host/wwwroot';dst=P/'app/server/wwwroot';assert dst.resolve().is_relative_to(P.resolve())
for p in (dst/'assets').iterdir():
 if p.is_file() and not (src/'assets'/p.name).exists():p.unlink()
note='''r23 画面布局修正（2026-11-14）

1. 历史数据、事件记录改为固定高度工作区：查询条件、操作按钮和表头保持，只有记录区域滚动。
2. 历史导出任务保留为下方可展开入口，展开不挤掉结果记录。
3. 总览卡片按实际可用宽度排布，不再依赖1850px窗口条件；常用宽屏、菜单收起时，紧凑8列、标准6列、大号4列。较窄窗口自动减少列数，以保证文字可读。
4. 紧凑选项只显示“紧凑”；紧凑罐图仍100×155px。标准、大号卡片相应调整图文间距和字号。

仍为r23，后台程序、数据结构、计算、授权规则不变。客户电脑使用当前同名r23安装程序更新；更新前按原说明备份，关闭客户端，再沿用原目录安装。
本机预览后台没有重启；已重新打开客户端，使用原预览账号登录即可。若停留旧页面，关闭后重新打开客户端。

前端构建通过；18组窗口/滚轮验证通过，覆盖1920、1704、1536、1280宽度及500行历史/事件样例。没有执行实际仪表动作、没有更改罐配置、没有新增长测。
'''
(O/'画面布局修正说明_20261114.txt').write_text(note,encoding='utf-8-sig');(P/'docs/画面布局修正说明_20261114.txt').write_text(note,encoding='utf-8-sig')
guide=P/'docs/r23修订与升级说明.html';s=guide.read_text(encoding='utf-8');s=s.replace('<h1>液位管理系统 r23 中文测试版</h1>','<h1>液位管理系统 r23 中文测试版</h1><p class="note">2026-11-14画面修正：历史/事件固定查询、按钮和表头，只滚动记录；紧凑/标准/大号在常用宽屏分别8/6/4列，随可用宽度调整。“紧凑”不再附列数。安装程序仍为同名r23，使用本目录最新文件。</p>');guide.write_text(s,encoding='utf-8')
shutil.copy2(guide,O/guide.name);shutil.copy2(guide,W/'安装向导使用说明.html')
files=[]
for p in P.rglob('*'):
 if p.is_file() and p.name!='package-manifest.json':files.append(dict(path=p.relative_to(P).as_posix(),bytes=p.stat().st_size,sha256=sha(p)))
manifest=dict(build='20261113-r23',schema=18,frontendUpdate='20261114-layout1',files=files)
(P/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
cmd=[str(R/'.build-tools/innosetup64/ISCC.exe'),'/Qp',f'/DPayloadRoot={P}',f'/DOutputRoot={O}','/DReleaseStamp=20261113',str(W/'LevelManager.iss')]
build=subprocess.run(cmd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT);(B/'installer-build.log').write_bytes(build.stdout);assert build.returncode==0,build.stdout[-2000:]
exe=O/'液位管理系统_r23_安装程序_20261113.exe';release['installer']=dict(file=exe.name,bytes=exe.stat().st_size,sha256=sha(exe));release['frontendUpdate']='20261114-layout1';release['frontendUpdatedAtUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat();release['validation']['frontendLayoutCases']=18;release['validation']['frontendConfigWrites']=0;release['validation']['frontendInstrumentActions']=0;release['frontendEvidence']='../r23-layout-fix_20261114/qa/result.json'
releasePath.write_text(json.dumps(release,ensure_ascii=False,indent=2),encoding='utf-8');(exe.with_name(exe.name+'.sha256')).write_text(sha(exe)+'  '+exe.name+'\n',encoding='utf-8')
old=R/'output/r23-feedback_20261113/verification-summary.json';summary=json.loads(old.read_text(encoding='utf-8'));summary.update({k:release[k] for k in ['installer','frontendUpdate','frontendUpdatedAtUtc','frontendEvidence']});summary['validation']=release['validation'];old.write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
binaryProtected=[f for f in before['protected'] if f['path'].startswith('app/')]
for f in binaryProtected:assert sha(P/f['path'])==f['sha256'],f['path']
for f in files:assert sha(P/f['path'])==f['sha256'],f['path']
result=dict(passed=True,frontendUpdate=release['frontendUpdate'],installer=release['installer'],payloadFiles=len(files),unchangedApplicationFiles=len(binaryProtected),backendRestarted=False,backendCodeChanged=False,layoutCases=18,realDeviceActions=0)
(B/'release-verification.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8');print(json.dumps(result,ensure_ascii=False))
