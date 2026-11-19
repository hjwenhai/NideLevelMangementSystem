from pathlib import Path
import zipfile
r=Path(__file__).resolve().parents[1];out=r/'output/r22-phase2-20261107';out.mkdir(exist_ok=True)
snap=out/'source-before-update.zip';assert not snap.exists()
with zipfile.ZipFile(snap,'x',zipfile.ZIP_DEFLATED) as z:
 for name in ['src','web','tests','tools','assets']:
  for p in (r/name).rglob('*'):
   if p.is_file() and not any(x in p.relative_to(r).parts for x in ['node_modules','bin','obj','wwwroot']):z.write(p,p.relative_to(r))
p=r/'src/LevelManager.Domain/PresentationConfiguration.cs';s=p.read_text(encoding='utf-8-sig');anchor='if(tank.Trends is null||';at=s.index(anchor);end=s.index('\n',at)
s=s[:end]+'\n            if(tank.Trends?.Axes is null||tank.Trends.Axes.Count>9||tank.Trends.Axes.Any(a=>!Enum.IsDefined(a.Key)||a.Value is null||!Enum.IsDefined(a.Value.Mode)||a.Value.MinimumSpan<0||a.Value.MinimumSpan>1000000000||a.Value.Mode==TrendAxisMode.Fixed&&(!a.Value.Minimum.HasValue||!a.Value.Maximum.HasValue||a.Value.Maximum<=a.Value.Minimum)||a.Value.Minimum is < -1000000000 or > 1000000000||a.Value.Maximum is < -1000000000 or > 1000000000))issues.Add(new("tanks/"+tank.Id,"曲线纵轴设置无效；固定上限须大于下限"));'+s[end:];p.write_text(s,encoding='utf-8')
p=r/'web/src/api.ts';s=p.read_text(encoding='utf-8-sig').replace('trends:{quantities:TrendQuantity[];colors:string[]}','trends:{quantities:TrendQuantity[];colors:string[];axes?:Partial<Record<TrendQuantity,TrendAxisSetting>>}')
s="export interface TrendAxisSetting {mode:'Auto'|'Fixed';minimumSpan:number;minimum:number|null;maximum:number|null}\n"+s;p.write_text(s,encoding='utf-8')
p=r/'web/src/App.vue';s=p.read_text(encoding='utf-8-sig');s=s.replace("const tankTabs=", "const copyGroupSource=ref<string|null>(null);\nconst tankTabs=",1)
s=s.replace("tankGroupIds.value=config.value?.groups.filter(g=>g.tankIds.includes(tankDraft.value?.id||'')).map(g=>g.id)||[];","tankGroupIds.value=config.value?.groups.filter(g=>g.tankIds.includes(copyGroupSource.value||tankDraft.value?.id||'')).map(g=>g.id)||[];copyGroupSource.value=null;")
start=s.index('function copyTank(t:Tank)');end=s.index('\nfunction acceptTank',start)
s=s[:start]+'''function copyTank(t:Tank){if((config.value?.tanks.length||0)>=128){error.value="最多配置128个罐";return}copyGroupSource.value=t.id;tankDraft.value=clone(t);Object.assign(tankDraft.value,{id:crypto.randomUUID(),code:"",name:(t.name||t.code)+"（副本）",address:0});message.value="已复制全部参数、分组、启用状态及罐容表；请填写新罐号和通讯地址，核对后保存。"}'''+s[end:];p.write_text(s,encoding='utf-8')
print('Domain axis validation and complete copy behavior applied')
