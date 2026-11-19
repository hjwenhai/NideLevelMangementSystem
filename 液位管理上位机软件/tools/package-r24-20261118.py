from pathlib import Path
import argparse,datetime,hashlib,json,shutil,subprocess,xml.etree.ElementTree as ET

R=Path(__file__).resolve().parents[1]
B=R/'output/r24-feedback_20261118'
P=B/'payload'
O=R/'output/客户安装向导_r24_20261118'
W=R/'deployment/wizard'

def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest().upper()

def prepare():
    assert not P.exists(),'新版本载荷已存在，拒绝覆盖'
    baseline=R/'output/r23-feedback_20261113/payload'
    for source in baseline.rglob('*'):
        relative=source.relative_to(baseline)
        if not source.is_file() or relative.parts[:2]==('app','server') or relative.parts[0]=='docs' or source.name=='package-manifest.json':continue
        destination=P/relative;destination.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(source,destination)
    shutil.copytree(B/'server',P/'app/server')
    (P/'docs').mkdir(exist_ok=True)
    O.mkdir(exist_ok=True)
    notes='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>液位管理系统 r24 修订与升级说明</title>
<style>body{font:16px/1.8 "Microsoft YaHei",sans-serif;color:#203c49;max-width:980px;margin:40px auto;padding:0 24px}h1{font-size:28px}h2{font-size:21px;margin-top:30px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d8e2e6;padding:9px;text-align:left}.note{background:#eef7f5;padding:16px}code{background:#f1f4f5;padding:2px 5px}</style>
<h1>液位管理系统 r24 修订与升级说明</h1><p>版本 20261118-r24，数据库结构仍为18。按2026年11月18日客户反馈修改，保留r23供对照。</p>
<h2>本次修改</h2><ol><li>总览在当前通讯有效且仪表处于已知非测量Ⅰ模式时，显示“液位冻结”，灰显本采集会话最后有效液位及灰色罐图。冻结值只作展示，不进入当前净体积、质量或库存合计。通讯过期、状态未知或无旧液位时仍显示缺测。</li><li>储罐配置的位号、罐号拆为两列，位号在前。</li><li>网关TCP、RTU的“网关仪表编号”改为“网关槽位号”，鼠标提示“填写网关仪表列表左侧的槽位号，不是液位计通讯地址。”</li><li>IFX30000故障定义以客户确认表红字为准：0x0002温度计超量程/7、0x0004模拟量输入超量程/9、0x0008参数错误/A、0x0010电机错误/1、0x0020浮子控制错误/8。其他项保持，0x0802分列6和7。首次检测时间和历史操作记录保留。</li><li>直连、IFX、网关TCP/RTU不再仅因仪表故障码非零拒绝操作，也不再仅因此把模式回读成功转为待核查。故障继续显示和报警；通讯有效、授权、逐罐开放、操作确认、绑定、认证及结果回读仍需满足。液位参数修改仍须取得新的原始位置以计算偏移；自动测温仍检查实际位置、模式、到位及温度有效性。</li></ol>
<h2>网关通讯要求</h2><p>兼容20261117-r1版V3.2-D2。读取、控制、独立停止、远程液位暂存区地址及状态0～5保持兼容，无需重新填写原网关地址。网关软件0.8.3起也允许带故障操作；较旧网关可能仍拒绝并返回历史错误码3，平台如实记录，不替网关虚构成功。应答不明、模式未确认、重启和改绑仍按原规则处理，不自动重发。</p>
<h2>已有客户升级</h2><ol><li>在系统管理中备份现有业务数据，记录实际数据目录、后台安装目录及端口。关闭客户端，确认没有正在执行的仪表操作或测温任务。</li><li>以管理员身份运行本目录的r24安装程序。后台电脑选择“后台及客户端”，客户端电脑选择“仅客户端”。</li><li>已有系统沿用原程序安装位置和原业务数据目录；不要选择空目录代替原数据，不删除数据库、授权目录或旧操作记录。r18升级也使用原业务数据目录，由安装程序执行兼容迁移。</li><li>确认端口、是否允许其他电脑连接及快捷方式后安装。仅客户端填写后台电脑实际访问地址。</li><li>安装后核对r24版本标识、原账号、储罐、通讯通道、罐容表、历史及操作记录，并按现场设备核对液位、温度和操作回读。已有授权继续按原授权范围使用，不会自动新增选配功能。</li></ol>
<p class="note">冻结液位仅作显示，不代表实时测量，不参与当前净体积、质量或库存合计。</p>
<h2>运行说明</h2><p>通讯中断或数据过期时显示缺测。操作结果以设备应答和回读为准；模式回读确认不代表浮子已到目标位置。升级后沿用原数据目录和授权。</p></html>'''
    for target in [P/'docs/r24修订与升级说明.html',O/'r24修订与升级说明.html',W/'安装向导使用说明.html']:target.write_text(notes,encoding='utf-8')
    (P/'docs/网关槽位填写说明.txt').write_text('网关槽位号指网关仪表列表左侧的槽位1～64，既不是网关Unit ID，也不是液位计实际地址。每槽可配置南向COM及站址，不强制按COM分段。\n',encoding='utf-8-sig')
    files=[]
    for path in sorted(P.rglob('*')):
        if path.is_file():files.append(dict(path=path.relative_to(P).as_posix(),bytes=path.stat().st_size,sha256=sha(path)))
    manifest=dict(productId='Naide.LevelManager.2026',version='0.3.0',build='20261118-r24',revision='r24',releaseLabel='r24',wordingUpdate='20261118-release-labels',schema=18,schemaVersion=18,customerFeedback='20261118',files=files)
    (P/'package-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(dict(prepared=True,payloadFiles=len(files))))

def build_installer(qa=False):
    target=B/'qa/installer/package' if qa else O
    target.mkdir(parents=True,exist_ok=True)
    args=[str(R/'.build-tools/innosetup64/ISCC.exe'),'/Qp',f'/DPayloadRoot={P}',f'/DOutputRoot={target}','/DVersionDirectory=20261118-r24','/DReleaseStamp=20261118']
    if qa:args.append('/DSetupQa')
    args.append(str(W/'LevelManager.iss'))
    result=subprocess.run(args,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
    (B/('installer-qa-build.log' if qa else 'installer-build.log')).write_bytes(result.stdout)
    assert result.returncode==0,result.stdout[-3000:]
    print(json.dumps(dict(installer=str(target/'液位管理系统_r24_安装程序_20261118.exe'),qa=qa)))

def seal(payload=P):
    manifest=json.loads((payload/'package-manifest.json').read_text(encoding='utf-8'))
    for item in manifest['files']:assert sha(payload/item['path'])==item['sha256'],item['path']
    for item in json.loads((B/'protected-releases.json').read_text(encoding='utf-8')):assert sha(R/item['path']).lower()==item['sha256'],item['path']
    tests=ET.parse(B/'qa/r24.trx').getroot();ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};counts=tests.find('.//t:Counters',ns).attrib;assert counts['failed']=='0'
    integration=sorted((B/'qa').glob('integration-*/result.json'))[-1];ui=json.loads(integration.read_text(encoding='utf-8'));assert ui['passed']
    install=json.loads((B/'qa/installer/package-result.json').read_text(encoding='utf-8-sig'));assert install['passed']
    exe=O/'液位管理系统_r24_安装程序_20261118.exe'
    release=dict(build='20261118-r24',revision='r24',schema=18,customerFeedback='20261118',builtAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),installer=dict(file=exe.name,bytes=exe.stat().st_size,sha256=sha(exe)),validation=dict(unitTests=int(counts['passed']),integrationGroups=len(ui['checks']),installerChecks=len(install['checks']),realDeviceActions=0),gatewayProtocol='V3.2-D2-20261117-r1',protectedR22R23=True,sourceSnapshot='../r24-feedback_20261118/r23-source-before.zip')
    if manifest.get('wordingUpdate'):
        wording=json.loads((R/'output/r24-release-wording_20261118/qa/result.json').read_text(encoding='utf-8'));assert wording['passed']
        release.update(version=manifest['version'],releaseLabel='r24',wordingUpdate=manifest['wordingUpdate'],wordingEvidence='../r24-release-wording_20261118/qa/result.json')
        release['validation'].update(wordingCases=len(wording['checks']),functionalBaselineReused=True)
    if manifest.get('sidebarNoteRemoved'):release['sidebarNoteRemoved']=manifest['sidebarNoteRemoved']
    guide_check=R/'output/r24-installer-no-guide_20261118/package-result.json'
    if guide_check.exists():
        guide_result=json.loads(guide_check.read_text(encoding='utf-8-sig'));assert guide_result['passed']
        release.update(installationGuideExternalOnly=True,installationGuideEvidence='../r24-installer-no-guide_20261118/verification.json')
        release['validation'].update(installationGuideChecks=len(guide_result['checks']),functionalBaselineReused=True)
    if manifest.get('lanIpHostFix'):
        lan_result=json.loads((R/'output/r24-lan-host_20261119/result.json').read_text(encoding='utf-8-sig'));assert lan_result['passed']
        lan_install=json.loads((R/'output/r24-lan-host_20261119/installer-result.json').read_text(encoding='utf-8-sig'));assert lan_install['passed']
        release.update(lanIpHostFix=manifest['lanIpHostFix'],lanIpHostEvidence='../r24-lan-host_20261119/verification.json')
        release['validation'].update(lanIpHostCases=len(lan_result['checks']),lanIpHostInstallerChecks=len(lan_install['checks']),functionalBaselineReused=True)
    if manifest.get('lanTankGraphicFix'):
        tank_result=json.loads((R/'output/r24-lan-tank-graphic_20261119/result.json').read_text(encoding='utf-8-sig'))
        assert tank_result['passed']
        release.update(lanTankGraphicFix=manifest['lanTankGraphicFix'],lanTankGraphicEvidence='../r24-lan-tank-graphic_20261119/result.json')
        release['validation'].update(lanTankGraphicChecks=len(tank_result['checks']),functionalBaselineReused=True)
    if manifest.get('realtimeTrendMinutes') == 60:
        evidence='../r24-realtime-hour_20261119/trend-ui/result.json'
        trend_result=json.loads((O/evidence).read_text(encoding='utf-8-sig'));assert trend_result['passed'] and trend_result['instrumentWrites']==0
        release.update(realtimeTrendMinutes=60,realtimeTrendEvidence=evidence)
        release['validation'].update(realtimeTrendChecks=len(trend_result['checks']),functionalBaselineReused=True)
    if payload != P:
        release['payloadDirectory']=payload.relative_to(R).as_posix()
    reinstall_cases=[case for case in install['checks'] if case['name'].startswith('reinstall-')]
    if reinstall_cases:
        assert all(case['passed'] for case in reinstall_cases)
        release.update(retainedDatabaseReinstall=True,retainedDatabaseReinstallEvidence='../r24-feedback_20261118/qa/installer/package-result.json')
        release['validation']['retainedDatabaseReinstallChecks']=len(reinstall_cases)
    (O/'release.json').write_text(json.dumps(release,ensure_ascii=False,indent=2),encoding='utf-8');(B/'verification-summary.json').write_text(json.dumps(release,ensure_ascii=False,indent=2),encoding='utf-8')
    (O/(exe.name+'.sha256')).write_text(sha(exe)+'  '+exe.name+'\n',encoding='utf-8')
    print(json.dumps(release,ensure_ascii=False))

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('action',choices=['prepare','build','build-qa','seal'])
    parser.add_argument('--payload-dir',type=Path)
    args=parser.parse_args()
    action=args.action
    if args.payload_dir and action!='seal':parser.error('--payload-dir is supported with seal')
    if action=='prepare':prepare()
    elif action=='seal':seal(args.payload_dir.resolve() if args.payload_dir else P)
    else:build_installer(action=='build-qa')
