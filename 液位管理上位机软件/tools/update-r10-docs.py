from pathlib import Path
import json,shutil
root=Path(__file__).resolve().parents[1]
p=root/'tools/prototype-comparison-report.py';s=p.read_text(encoding='utf-8')
s=s.replace("import csv, html, json", "import csv, html, json, argparse\nparser=argparse.ArgumentParser();parser.add_argument('--revision',default='r10');revision=parser.parse_args().revision\nif revision not in ['r9','r10']:raise ValueError('unsupported report revision')")
s=s.replace("out = root / 'output/prototype-parity-r9'","out = root / ('output/prototype-parity-'+revision)")
marker='# Keep the original 14-item discussion numbers even after two exclusions.'
assert marker in s
completed='''# r10 implemented priorities, preserving the original discussion IDs and r9 evidence.
completed = {
 '已知高度ATS测点在罐图上的编号定位': ('单罐详情→ATS罐图及点列表','按配置高度标注点号，0mm有效；无高度或超出罐高仅列表，不推定位置。ATS平均规则未改变。'),
 'Excel批量导入储罐配置': ('储罐配置→导入配置、预览和确认','三个配置页按罐号对应，检查通道、地址、关联及参数；逐罐对照字段原值与导入值，确认时校验最新配置版本。仅新增／更新，不删除未列出罐，不发送动作。'),
 'Excel导出储罐配置': ('储罐配置→导出配置／下载模板','档案与通讯、报警与计量、操作与温度三个配置页，附参考编号和填写说明；不含账号口令、授权、令牌或定时任务。可选模块未交付时隐藏对应选项。'),
 '页面刷新周期设置': ('基础数据→显示单位→页面刷新间隔','1～30秒，全用户共用；与物理采集轮询、历史归档分别配置；纯显示修改不重开采集，过期判断继续按时间推进。'),
 '按数据状态筛选': ('历史数据→状态对应数据项／数据状态','9类数据项分别筛选正常、全部无效及具体状态。数据库过滤、复合游标分页、当前页和后台Excel沿用同一条件，正常0与无效分开。'),
 '任意编辑岗位权限矩阵': ('系统管理→账号与权限→岗位权限矩阵','三个既有岗位的功能权限可编辑，界面及后台同步执行；账号／岗位／供方授权／恢复核查由管理员保留，操作与停止成组。权限变化要求重新确认原定时测温授权；不突破模块、逐罐操作和设备能力。')
}
if revision=='r10':
 for row in rows:
  if row['item'] in completed:
   row.update(status='本轮补齐',entry=completed[row['item']][0],explanation=completed[row['item']][1],evidence='215项单元＋六项优先实际浏览器／API回归；另有专项回归')

'''
s=s.replace(marker,completed+marker)
s=s.replace("candidate='r9中文测试候选'","candidate=revision+'中文测试候选'")
s=s.replace("原型与正式系统逐项对照清单_r9.html","原型与正式系统逐项对照清单_REVISION.html")
s=s.replace("(out/'原型与正式系统逐项对照清单_REVISION.html')","(out/('原型与正式系统逐项对照清单_'+revision+'.html'))")
s=s.replace('2026-10-28 · r9中文测试候选','2026-10-28 · REVISION中文测试候选')
s=s.replace('12小时持续测试保持原运行环境。','r4十二小时模拟阶段验证已通过并收尾；r10按需求进行独立六小时测试，结果以本次记录为准，未完成时不标通过。')
s=s.replace('罐体档案资料、罐组入口、品牌区、协议选择与摘要、备注及设备批次分组。','r9档案与品牌补齐；r10增加ATS罐图点位、储罐配置导入导出、页面刷新、历史状态筛选及可编辑岗位权限。')
s=s.replace('配置导入导出、单罐详情打印、ATS罐图点位、通道点表展示、数据状态筛选、全局刷新／默认趋势、可编辑岗位权限、对外点表预览及导出，详见筛选结果。','首版六项优先已补齐。仍有单罐详情打印、网关版本、通道点表预览、默认历史趋势范围、对外点表预览及页面导出，详见筛选结果。')
s=s.replace("report=report.replace('STATS'","report=report.replace('REVISION',revision).replace('STATS'")
p.write_text(s,encoding='utf-8')
# Customer-facing existing HTML documents: retain established style and precise boundaries.
p=root/'deployment/docs/中文快速上手.html';s=p.read_text(encoding='utf-8')
s=s.replace('版本：0.3.0-zh-test.1','版本：0.3.0-zh-test.1 · r10')
s=s.replace('本轮按12小时软件与模拟线路阶段验证推进；72小时可靠性验证留待后续，不能按已完成处理。','r4已完成12小时软件与模拟线路阶段验证。r10另行安排6小时模拟测试；未取得本版最终结果前不能标为通过。72小时及实际设备可靠性验证另行安排。')
section='''<h2>r10新增功能</h2><table><tr><th>入口</th><th>操作与规则</th></tr><tr><td>系统管理→账号与权限</td><td>在岗位权限矩阵中勾选管理员、操作员、浏览员可使用的功能并保存。当前登录账号的后续请求按新权限校验。账号、岗位、供方授权及恢复核查由管理员保留。允许仪表操作或平均测温时须同时保留停止权限；不改变本罐开放操作及设备限制。</td></tr><tr><td>储罐配置→导出／导入</td><td>先导出配置或下载模板。填写三个配置页，保持表头和对应罐号；通道、介质、罐组、罐容表编号从参考页复制。上传后预览，逐罐检查字段原值和导入值，再勾选确认保存。未列出的储罐保留；不会自动发送仪表动作。预览10分钟内有效，有其他用户保存配置后须重新预览。仅支持本系统xlsx模板，不导入账号、授权或定时任务。</td></tr><tr><td>单罐详情→ATS</td><td>已填高度的测点按实际高度标注点号；未填高度或超出罐高的点在列表显示。0mm不是空值；所有正常读取点仍参与平均，不按液面剔除。</td></tr><tr><td>基础数据→显示单位</td><td>页面刷新间隔1～30秒，与通道采集周期和历史保存间隔分别配置。修改刷新间隔不重启采集，过期数据仍按时间失效。</td></tr><tr><td>历史数据</td><td>先选“状态对应数据项”，再选正常、全部无效或具体状态。液位正常不能代表温度正常。分页及导出沿用已成功查询的条件，0与无效空值分别保留。</td></tr></table><p>岗位权限矩阵修改后，原定时测温授权须重新编辑并确认；不自动恢复旧授权。正在执行的任务仍保留必要停止和收尾。</p>'''
s=s.replace('<h2>8. 本次测试与现场记录</h2>',section+'<h2>8. 本次测试与现场记录</h2>')
s=s.replace('管理员设置','有相应权限的账号设置')
p.write_text(s,encoding='utf-8')
p=root/'deployment/docs/测试范围与现场准备.html';s=p.read_text(encoding='utf-8').replace('版本0.3.0-zh-test.1，','版本0.3.0-zh-test.1 · r10，')
s=s.replace('账号岗位、离线软件授权','可编辑岗位权限矩阵、储罐配置Excel导入导出与预览、页面独立刷新、历史按所选数据项状态筛选、ATS罐图点位；离线软件授权')
s=s.replace('连续72小时运行、现场掉电与恢复、真实线路异常。','r4十二小时模拟阶段验证已通过；r10独立六小时测试以其最终结果为准，不能提前认定通过。后续仍需更长运行、现场掉电与恢复及真实线路异常验证。')
p.write_text(s,encoding='utf-8')
p=root/'deployment/docs/备份恢复与维护.html';s=p.read_text(encoding='utf-8').replace('管理员在“系统管理→数据库维护”创建业务备份','有业务备份权限的账号在“系统管理→数据库维护”创建业务备份').replace('包含配置、账号、历史、事件、罐容表与任务记录。','包含配置、账号、岗位权限矩阵、历史、事件、罐容表与任务记录。').replace('业务库有自动迁移，','r10业务库升级至schema15，保留既有历史及账号，首次使用默认岗位权限；业务库有自动迁移，')
p.write_text(s,encoding='utf-8')
# Test docs only: preserve original output records and use a new comparison folder.
p=root/'docs/分阶段任务与验收清单_V1.3_20261028.md';s=p.read_text(encoding='utf-8');s=s.replace('优先级不是已完成状态。','六项优先已实现并通过215项单元与专项浏览器/API回归；r10六小时稳定性测试单独记录，未完成前不标通过。')
for n in ['2','4、5','8','10','12']:s=s.replace('- [ ] 原第'+n+'项','- [x] 原第'+n+'项')
p.write_text(s,encoding='utf-8')
print('r10 comparison and customer instructions updated; original packages untouched')
