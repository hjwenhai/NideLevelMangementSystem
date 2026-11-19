"""Generate the reviewed prototype/production comparison, not a test-pass claim."""
from pathlib import Path
import csv, html, json, argparse
parser=argparse.ArgumentParser();parser.add_argument('--revision',default='r10');revision=parser.parse_args().revision
if revision not in ['r9','r10']:raise ValueError('unsupported report revision')
from collections import Counter

root = Path(__file__).resolve().parent.parent
out = root / ('output/prototype-parity-'+revision)
out.mkdir(parents=True, exist_ok=True)
rows = []

def add(page, items, status, entry, explanation='', evidence='源码对照'):
    for item in items.split('|'):
        rows.append(dict(id=len(rows)+1, page=page, item=item, status=status, entry=entry,
                         explanation=explanation, evidence=evidence))

add('公共布局与品牌', '厂家名称|默认N标识与青色装饰|TANK MONITORING副标题|厂家标识与软件名称分开使用', '本轮补齐', '侧栏品牌区', '品牌来自供方授权。软件名称继续用于窗口和登录标题；不向客户开放品牌编辑。', '源码＋实际浏览器截图')
add('公共布局与品牌', '厂家Logo|程序图标|软件名称|厂家介绍与联系方式|平均测温交付开关', '已实现', '供方品牌授权、软件授权', '厂家未配置Logo时使用默认标识；可选模块按交付范围裁剪，不在客户设置中提供开关。', '授权与模块回归')
add('公共布局与品牌', '菜单图标|菜单选中状态|监控与管理分区|账号身份|退出登录|未启用报表入口', '已实现', '侧栏与顶部', evidence='逐菜单浏览器检查')

add('罐区总览', '三种罐型|液位及高度比例|瞬时温度IR26|仪表实测密度|净体积|产品质量|罐体状态颜色|报警文字|离线和过期留空|实时详情入口', '已实现', '罐区总览', evidence='显示／压力、计量与界面回归')
add('罐区总览', '名称展示', '本轮补齐', '罐号下方', '档案名称有值时显示。', '界面＋保存重开验证')
add('罐区总览', '分组及下级筛选|介质筛选|净体积合计|质量合计|有效罐数|隐藏和停用罐不参与合计|共用置顶及先后顺序', '已实现', '总览筛选与统计', evidence='显示／压力与置顶回归')
add('罐区总览', '卡片与表格切换', '入口不同', '罐区总览、罐清单列表', '正式版拆成两个相邻菜单，功能均有；不是首页上的两个切换按钮。')

add('罐清单列表', '当前读数|逐值状态|单位与小数位|接收时间|筛选范围|查看详情|导出当前清单|打印当前清单', '已实现', '罐清单列表', evidence='浏览器与显示／计量回归')
add('罐清单列表', '置顶与取消置顶', '已实现', '列表操作列', '管理员可操作，所有用户共用；筛选仍生效。', '管理员／浏览员对照验证')

add('单罐详情', '选择储罐|液位与实际浮子位置|液面依据|三种罐型|已配置操作按钮|瞬时温度IR26|ATS实时算术平均|最近一次无线平均温度|平均完成时间|当前净体积|产品质量|扣水前当前体积|人工水体积|20℃标密|两路压力|状态与故障码|故障含义与首次检测时间|来源及接收时间|ATS点号高度温度质量|实时趋势|编辑储罐入口', '已实现', '单罐详情', '无效值不填0；最近一次任务平均温度不会冒充新的瞬时温度。', '源码＋专项回归＋实际页面')
add('单罐详情', '名称|产品编号|设备批次|罐径|公称容量|所属罐组|备注', '本轮补齐', '单罐详情的档案信息', evidence='真实保存后页面显示')
add('单罐详情', '数据／操作／诊断／配置四个子页签', '呈现不同', '详情信息区、罐图下方操作、诊断与编辑配置', '正式版以同页分区和展开趋势呈现，并未照搬四个子页签；内容按实际入口核对。')
add('单罐详情', '单罐整页打印预览', '仍需补充', '尚无独立入口', '罐清单、历史和趋势已有打印；不能用这些代替原型的单罐详情打印。')
add('单罐详情', '已知高度ATS测点在罐图上的编号定位', '仍需补充', '当前仅ATS列表显示高度', '原型在高度完整时可在罐图定位；正式版尚未把测点标到罐图。缺高度时不得推定位置。')

add('储罐配置／罐体档案', '罐号|仪表位号|所属罐区|介质|储罐类型|罐高／量程|隐藏储罐', '已实现', '储罐配置→罐体档案', '仪表位号选填；隐藏后继续采集、报警及保存历史。', '实际表单检查')
add('储罐配置／罐体档案', '名称|产品编号|设备批次|罐径|公称容量|所属罐组配置入口', '本轮补齐', '储罐配置→罐体档案', '新增资料均可留空。罐组可多选，保留已有分组关系；尺寸和公称容量不代替罐容表计算。', '页面＋真实保存、取消、清空、重开和备份恢复')
add('储罐配置／罐体档案', '三个罐型之外的双层罐', '原型旧示意', '正式版为立式罐、球罐、浮顶罐', '本轮不额外增加第四种罐型，沿用已讨论的三种类型。')
add('储罐配置／通讯绑定', '采用协议|协议筛选采集通道|连接摘要|协议对应的地址名称及范围|采集启用状态', '本轮补齐', '储罐配置→通讯绑定', '切换协议不会静默改绑；必须显式选择匹配通道。串口／网络参数仍由通道共用。', '真实浏览器验证无匹配通道阻止提交')
add('储罐配置／通讯绑定', '直连Modbus RTU|IFX L25单COM与32罐|GT6757X Modbus TCP|GT6757X Modbus RTU|网关编号与Unit ID分开|每南向COM16通道', '已实现', '通讯绑定、通讯通道', '页面显示按当前已确认协议；未访问真实串口或仪表。', '协议单元测试＋独立模拟器')
add('储罐配置／报警配置', '液位低低／低／高／高高四限|温度四限|密度四限|压力1四限|压力2四限|回差|连续超限延时|通讯异常监测|仪表故障监测|限值可留空', '已实现', '储罐配置→报警配置', '正式版已扩展为每个项目独立四限、回差和延时；恢复需有效读数，确认不等于恢复。', '报警专项回归＋实际表单')
add('储罐配置／报警配置', '测量报警级别手工选择', '已确认不需要', '保留按既定报警规则分级', '需求方2026-10-28明确不需要手工选择测量报警级别；移出待补范围。报警分级、颜色、四限及确认恢复规则继续保留。', '需求方本会话明确确认')
add('储罐配置／报警配置', '安全液位上下限', '需明确用途', '当前未提供独立设置', '原型是作业量边界示意，不能当作已实现的安全联锁。与普通报警四限的关系及作业管理用途需明确。')
add('储罐配置／计量参数', '人工水体积|20℃标准密度|仪表密度来源|4mA对应工程值|20mA对应工程值|压力单位|未配置及无效数据处理', '已实现', '储罐配置→计量参数', '压力标定从原正式版的通讯绑定移到计量参数。视密与20℃标密分开，不开放原型已被后续覆盖的手动视密来源。', '压力和计量回归')
add('储罐配置／计量参数', 'VCF方法|标准体积', '已约定延后', '当前不计算', '没有双方确认的公式及样例时不计算、不填演示结果。')
add('储罐配置／罐容表', '主罐容表|静压力修正表|小数表关联|参与体积计算开关|参考密度所在位置|缺密度或超表范围不计算', '已实现', '储罐配置→罐容表', '参考密度由静压力表维护，默认1000；修正＝表值×实际密度÷参考密度。', '实际客户XLS导入与计算回归')
add('储罐配置／罐容表', '小数表组合计算', '已约定延后', '仅维护关联', '保留入口和资料；缺少确认的组合规则，不把其数值重复加入主表。')
add('储罐配置／操作与温度', '不带温度计|ATS／无线浮子互斥|ATS点数1～16|测点高度|正常ATS点参与平均|无线平均测温按交付范围隐藏|逐罐允许操作|控制等待参数', '已实现', '储罐配置→操作与温度', '0℃及液面以上的正常ATS点参与平均；故障和缺读不补0。', '测温规则单元测试＋表单回归')
add('储罐配置／操作与温度', '备注', '本轮补齐', '操作与温度末尾、详情档案', '选填，保存后可查看和修改。', '保存重开验证')
add('储罐配置／操作与温度', '远程液位修改及普通参数写入', '受协议和授权限制', '未默认开放', '原型中的候选操作不能视作原厂协议和现场条件已经确认。')
add('储罐配置／表单', '六个页签|切换保留草稿|跨页签校验|取消不修改配置|只改档案不重启采集', '已实现', '编辑／新增储罐', evidence='实际浏览器回归')
add('储罐配置／表单', '保存流程', '呈现不同', '应用到待保存配置→页面保存配置', '正式版仍分两步统一提交；原型是弹窗直接保存。当前会明确提示，不把“应用”误说成已经入库。')
add('储罐配置／列表', '新增|编辑|删除|128罐上限|已停用仍占配置数量|删除保留历史', '已实现', '储罐配置列表', evidence='配置与存储测试')
add('储罐配置／列表', 'Excel批量导入储罐配置|Excel导出储罐配置', '仍需补充', '列表尚无入口', '原型有配置导入预览和导出。现有罐容表Excel导入、当前读数导出不能代替储罐配置导入导出。')

add('通讯通道', '四种采集协议|串口号|波特率|数据位|校验位|停止位|TCP地址与端口|站号／Unit ID|轮询周期|超时与只读重试|启停通道|新增编辑删除|连接错误反馈', '已实现', '通讯通道', '不同协议按自身规则配置，控制写入不自动重试。', '协议／传输测试及通道页面')
add('通讯通道', '网关点表版本单独显示|通道点表预览', '仍需补充', '目前未提供页面入口', '驱动按已确认的固定协议实现，但界面没有原型的点表版本和寄存器摘要预览。展示前应核对实际协议版本，不能抄旧原型地址。')
add('罐组管理', '分组名称|罐区／罐组／介质分类|上级分组|分配储罐|下级汇总|删除分组保留储罐|循环上级校验', '已实现', '罐组管理', evidence='分组与显示专项回归')
add('罐组管理', '设备批次分组类型', '本轮补齐', '分组类型', '档案批次名称与分组关联分别维护，不自动覆盖用户分组。', '存储／备份及浏览器验证')

add('罐容表管理', '表编号|表名称|表类型|版本|高度单位mm／cm／m|容积单位m³／L|静压参考密度|资料备注|数据预览校核|启用停用|表格Excel导入|列与首行映射|导出表数据|批量关联|取消本表关联|筛选后全选|替换同类表确认', '已实现', '基础数据→罐容表管理', '按已确认要求保留批量关联；不恢复旧单罐关联下拉和旧“批量关联／取消”按钮文字。', '16634行客户XLS实际导入与计算回归')
add('罐容表管理', 'PDF自动识别导入', '不属当前实现', '当前仅Excel和手工数据', '不同排版及扫描PDF不能视为当前已有的标准表导入；需另行确定资料范围和识别校核要求。')
add('介质与计量', '介质编码|名称|类别|颜色|参考温度|参考密度|新增编辑删除|名称标签不覆盖罐体状态色', '已实现', '基础数据→介质与计量', evidence='显示与计量回归')
add('介质与计量', 'VCF方法配置', '已约定延后', '未参与计算', '与已确认延后的VCF、标准体积保持一致。')
add('显示与数据配置', '液位单位|体积单位|密度单位|质量单位|体积小数位|质量小数位|历史采样间隔|数据过期阈值|历史保留最长12个月|单罐30分钟趋势', '入口不同', '显示单位、系统基本参数／数据库维护、趋势曲线', '功能已有，但没有单独复制原型“数据与趋势配置”整页。', '实际界面＋显示／历史／趋势测试')
add('显示与数据配置', '页面刷新周期设置|全局默认历史趋势范围', '仍需补充', '目前刷新周期固定、趋势在各查询页选择', '原型可配置；正式版未提供全局设置。改变刷新不应改变仪表采集与历史采样周期。')
add('定时任务', '任务名称|每日时间|启停状态|选择储罐|数据快照任务|平均测温任务及点数', '已实现', '基础数据→定时任务', '测温仍受模块、协议、权限、设备状态和并发条件约束。', '定时任务专项测试＋页面检查')

add('平均温度测量', '手动新建任务|按V1.3组织|选择点数|任务状态|点明细|完成时间|历史结果|取消后续并停止|未知结果人工核查|导出当前记录', '已实现', '平均温度测量、无线浮子详情', '有界面和状态机不代表实际设备联调已完成。', '测温工作流／存储专项及页面')
add('平均温度测量', '网关整任务一次认证与停留后新I6|IFX位置源新鲜度证明', '受协议和设备限制', '控制与测温保护条件', '维持既有未解除限制，不能用缓存值充当新的位置或温度证明。')
add('历史数据', '储罐|起止时间|读取真实历史|历史当时配置／计算版本|值与质量|无效不补零|分页|当前页导出|全量查询Excel导出|打印当前查询页', '已实现', '历史数据', evidence='历史／Excel／计量回归')
add('历史数据', '按数据状态筛选', '仍需补充', '历史表可看质量，但查询没有状态筛选', '原型有“数据状态”选择；不能把逐行显示质量码等同于支持按状态筛选。')
add('趋势曲线', '实时与历史范围|最多5条|同单位共轴|自动纵轴|缺测断段|单罐共用曲线配置|曲线颜色|导出显示点|保存曲线图片|打印', '已实现', '趋势曲线、单罐实时趋势', evidence='趋势专项回归＋实际页面')
add('数据快照', '抓取快照|时间与名称|查看快照|导出快照|快照独立保留值与质量', '已实现', '数据快照', evidence='数据任务专项测试＋页面')
add('报警中心', '活动报警|待确认|历史报警|恢复未确认|按罐与时间查询|确认记录|无效读数不当恢复|报警导出', '已实现', '报警中心', evidence='报警专项回归＋页面')
add('事件记录', '操作日志|系统事件|通讯异常|仪表诊断|任务错误|报警事件|储罐筛选|时间筛选|操作人员筛选|关键字筛选|组合查询|Excel导出', '已实现', '事件记录', '操作日志集中在事件记录，没有放回趋势页。', '事件与账号安全回归')
add('报表中心', '单罐作业报表|罐区产品汇总报表|动态即时报表', '已约定延后', '报表中心保留入口', '按照用户要求标注暂未启用，不用原型演示数据伪装正式报表。', '菜单入口检查')

add('系统管理／基本参数', '服务启动后只读采集|采集启停|历史间隔|过期时间|通道状态', '已实现', '系统管理→基本参数', evidence='采集与界面测试')
add('系统管理／基本参数', '独立时区选择', '已确认不需要', '跟随Windows时区', '需求方2026-10-28明确不需要独立时区选择；移出待补范围。保持UTC存储及按电脑本地时区显示，不提供独立选择控件。', '需求方本会话明确确认')
add('系统管理／账号权限', '建立首个管理员|新增编辑账号|启停账号|三种岗位|修改本人密码|管理员重置密码|账号变更使原会话失效|最后管理员保护', '已实现', '系统管理→账号与权限', evidence='账号安全12组及存储测试')
add('系统管理／账号权限', '任意编辑岗位权限矩阵', '仍需补充', '当前为固定三角色及逐罐允许操作', '原型提供矩阵示意；正式版尚无可编辑的细粒度岗位权限配置，需核实首版范围。')
add('系统管理／账号权限', '登录日志独立页签', '入口不同', '事件记录→系统事件', '登录行为有审计记录，但没有原型的单独登录日志页签。')
add('系统管理／数据库维护', '保留期限|缩短期限备份确认|创建业务备份|下载任务进度|离线恢复|恢复现场核查|业务数据与授权分离|SQLite诊断', '已实现', '数据库维护、后台数据任务及维护工具', '恢复为离线维护，未在客户页面放任意覆盖数据库按钮。', '备份／恢复／原生包专项测试')
add('系统管理／数据库维护', '在线历史与归档保留月数分别设置|归档完成后清理的可选策略', '仍需明确范围', '当前按最长12个月在线保留及既定清理策略', '原型有分层归档示意；正式版尚无独立归档库保留期限和该策略下拉，不能把备份等同分层归档。')
add('系统管理／对外接口', '只读HTTP接口|允许输出的罐选择|独立令牌|令牌吊销|值质量时间及报警|过期规则一致', '已实现', '系统管理→对外接口', '网关Modbus TCP/RTU采集与对外输出接口分开。', '只读接口专项测试')
add('系统管理／对外接口', '对外点表可视预览|对外点表Excel导出', '仍需补充', '当前有接口说明，没有页面点表预览／Excel导出', '原型有这两个操作，正式版尚未提供。')
add('系统管理／对外接口', '对外Modbus TCP|对外OPC UA', '候选未全部选定', '当前只读HTTP', '原型明确是拟选一种接口，不能将三个候选都认定已经交付；与采集网关支持TCP/RTU不是一回事。')
add('系统管理／授权', '安装请求|导入本机授权|截止与提醒|到期保留监控归档确认停止|限制新任务／普通控制／历史导出|已有任务必要收尾|独立授权保护|供方品牌与模块管理', '已实现', '软件授权、供方工具', evidence='授权／到期／原生恢复回归')
add('系统管理／授权', '客户任意编辑品牌和到期日', '按确认不开放', '仅供方签发', '保持用户先前要求；不恢复原型演示版的自由编辑入口。')
add('原型演示专用', '切换演示身份|恢复演示样例|演示导览', '无需复制', '真实登录及独立测试环境', '这些用于原型讨论，不作为真实客户程序中的身份切换或恢复样例后门。')

# r10 implemented priorities, preserving the original discussion IDs and r9 evidence.
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
   row.update(status='本轮补齐',entry=completed[row['item']][0],explanation=completed[row['item']][1],evidence='218项单元＋六项优先实际浏览器／API回归；另有专项回归')

# Keep the original 14-item discussion numbers even after two exclusions.
planning = {
    '单罐整页打印预览': (1, '第二优先'),
    '已知高度ATS测点在罐图上的编号定位': (2, '首版优先'),
    '测量报警级别手工选择': (3, '不开发'),
    'Excel批量导入储罐配置': (4, '首版优先'),
    'Excel导出储罐配置': (5, '首版优先'),
    '网关点表版本单独显示': (6, '第二优先'),
    '通道点表预览': (7, '第二优先'),
    '页面刷新周期设置': (8, '首版优先'),
    '全局默认历史趋势范围': (9, '未指定排期'),
    '按数据状态筛选': (10, '首版优先'),
    '独立时区选择': (11, '不开发'),
    '任意编辑岗位权限矩阵': (12, '首版优先'),
    '对外点表可视预览': (13, '第二优先'),
    '对外点表Excel导出': (14, 'DCS联调前资料'),
}
for r in rows:
    decision = planning.get(r['item'])
    r['discussionNumber'] = decision[0] if decision else ''
    r['priority'] = decision[1] if decision else '—'
    r['planning'] = ('原第'+str(decision[0])+'项 · '+decision[1]) if decision else '—'
priority_decision = dict(confirmedAt='2026-10-28', originalDiscussionNumbers=True,
    firstRelease=[2,4,5,8,10,12], secondPriority=[1,6,7,13], excluded=[3,11],
    dcsPrerequisite='DCS联调前准备完整接口点表资料；页面Excel导出按钮未单独明确排期',
    combinedWork='储罐配置导出、模板和导入预览校验一起规划',
    unspecified=[9])
counts = Counter(r['status'] for r in rows)
payload = dict(reference='客户画面原型最终合并版及后续feedback/gateway覆盖逻辑',
               candidate=revision+'中文测试候选', method='源码逐项对照＋已实现功能专项回归＋全部菜单／页签实际浏览器检查',
               limitation='未把全部清单行等同于逐行自动化验收；实际仪表、Windows10、实际DPI及打印仍按现场条件验证。',
               counts=dict(counts), priorityDecision=priority_decision, rows=rows)
(out/'原型逐项对照清单.json').write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding='utf-8')
columns = [('id','序号'),('page','原型画面／页签'),('item','核对项目'),('planning','原讨论编号与安排'),('status','结论'),('entry','正式版入口／现状'),('explanation','差异与处理说明'),('evidence','核验依据')]
with (out/'原型逐项对照清单.csv').open('w', encoding='utf-8-sig', newline='') as f:
    w=csv.writer(f);w.writerow([x[1] for x in columns]);w.writerows([[r[k] for k,_ in columns] for r in rows])
e=html.escape
body=''.join('<tr data-status="'+e(r['status'])+'" data-priority="'+e(r['priority'])+'">'+''.join('<td>'+e(str(r[k]))+'</td>' for k,_ in columns)+'</tr>' for r in rows)
options='<option value="">全部结论</option>'+''.join('<option>'+e(k)+'</option>' for k in counts)
report='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>原型与正式系统逐项对照清单</title><style>
body{font:14px/1.65 "Microsoft YaHei","Segoe UI",sans-serif;color:#26414d;background:#f3f7f7;margin:0;padding:30px}main{max-width:1580px;margin:auto}h1{font-size:27px;margin:0 0 10px}p{max-width:1200px}.note{background:#fff;border:1px solid #dce8e7;border-radius:10px;padding:18px 22px;margin:18px 0}.stats{display:flex;gap:8px;flex-wrap:wrap}.stats span{background:#e9f2ef;padding:6px 12px;border-radius:7px}.filters{display:flex;gap:12px;position:sticky;top:0;background:#f3f7f7;padding:14px 0;z-index:2}select,input,button{font:inherit;padding:9px 12px;border:1px solid #ccdcd9;border-radius:7px;background:#fff}input{width:420px;max-width:50vw}table{width:100%;border-collapse:collapse;background:white}th,td{border:1px solid #dce6e5;padding:11px;vertical-align:top;text-align:left;font-size:12px}th{background:#eaf2ef}td:first-child{width:34px}td:nth-child(2){width:155px}td:nth-child(3){width:190px}td:nth-child(4){width:95px;font-weight:600}td:nth-child(6){min-width:220px}tr[data-status="仍需补充"] td:nth-child(4){color:#b06a25}tr[data-status="本轮补齐"] td:nth-child(4){color:#157d69}tr[hidden]{display:none}h2{font-size:19px;margin-top:24px}@media print{body{background:white;padding:0}.filters,button{display:none}th,td{font-size:9px;padding:6px}thead{display:table-header-group}tr{break-inside:avoid}.note{break-inside:avoid}h1{font-size:22px}@page{size:A4 landscape;margin:12mm}}</style><main><h1>原型与正式系统逐项对照清单</h1><p>2026-10-28 · REVISION中文测试候选</p><div class="note"><strong>核对依据：</strong>客户画面原型及其最终覆盖逻辑，结合已确认的三种罐型、计量公式、ATS规则、可选测温和延后项。原型中的演示操作、接口候选和未确认公式，不直接当作已经交付的功能。<br><strong>本轮已补：</strong>r9档案与品牌补齐；r10增加ATS罐图点位、储罐配置导入导出、页面刷新、历史状态筛选及可编辑岗位权限。<br><strong>核验口径：</strong>源码逐项对照、专项回归及全部菜单／页签的浏览器检查。清单不代表实际仪表、目标Windows版本或每行功能均已完成现场验收。r4十二小时模拟阶段验证已通过并收尾；r10按需求进行独立六小时测试，结果以本次记录为准，未完成时不标通过。</div><div class="stats">STATS</div><div class="note"><strong>2026-10-28范围确认：</strong>需求方明确不需要“手工选择测量报警级别”和“独立时区选择”，两项从待补清单移除。报警继续按既定规则分级，时间跟随Windows时区；不影响现有运行环境。</div><div class="note"><strong>2026-10-28优先级确认（沿用原14项编号）：</strong>首版优先第2、4、5、8、10、12项，共六项，包含可编辑岗位权限矩阵。储罐配置导出、模板与导入预览校验整体规划。单罐打印、版本说明及通道／对外点表预览列为第二优先；DCS联调前准备完整接口点表资料。第3、11项不开发。第9项全局默认历史趋势范围尚未指定排期。</div><h2>仍需补充的实际差异</h2><p>首版六项优先已补齐。仍有单罐详情打印、网关版本、通道点表预览、默认历史趋势范围、对外点表预览及页面导出，详见筛选结果。按已确认优先级安排；优先级表示开发顺序，不表示功能已经完成。</p><div class="filters"><select id="status">OPTIONS</select><select id="priority"><option value="">全部优先级</option><option>首版优先</option><option>第二优先</option><option>DCS联调前资料</option><option>不开发</option><option>未指定排期</option></select><input id="query" placeholder="搜索画面、字段或说明"><button onclick="window.print()">打印清单</button><span id="total"></span></div><table><thead><tr>HEADERS</tr></thead><tbody>ROWS</tbody></table></main><script>const all=[...document.querySelectorAll('tbody tr')];function filter(){let n=0;for(const r of all){r.hidden=!!(statusBox.value&&r.dataset.status!==statusBox.value||priorityBox.value&&r.dataset.priority!==priorityBox.value||queryBox.value&&!r.innerText.toLowerCase().includes(queryBox.value.toLowerCase()));if(!r.hidden)n++}document.getElementById('total').textContent=n+' / '+all.length+'项'}const statusBox=document.getElementById('status'),queryBox=document.getElementById('query'),priorityBox=document.getElementById('priority');statusBox.onchange=filter;priorityBox.onchange=filter;queryBox.oninput=filter;filter();</script></html>'''
report=report.replace('REVISION',revision).replace('STATS',''.join('<span>'+e(k)+' '+str(v)+'</span>' for k,v in counts.items())).replace('OPTIONS',options).replace('HEADERS',''.join('<th>'+e(label)+'</th>' for _,label in columns)).replace('ROWS',body)
(out/('原型与正式系统逐项对照清单_'+revision+'.html')).write_text(report,encoding='utf-8')
print(json.dumps(dict(total=len(rows),statuses=dict(counts),report=str(out/('原型与正式系统逐项对照清单_'+revision+'.html'))),ensure_ascii=False))
