from pathlib import Path
import json
root=Path(__file__).resolve().parents[1]
progress=root/'docs/project_memory/progress.md'
text=progress.read_text(encoding='utf-8-sig')
heading='''# 项目进度

更新：2026-11-07（北京时间）。当前r22已按确认方案补齐并原数据重启，build 20261107-r22-u1；415项单元、10组纵轴逻辑、20组界面/下载、12组复制/Excel往返及10个工作簿边框检查通过。尚未封新版安装包或另跑六小时测试。

## r22纵轴、Excel与复制补齐（当前状态）

- 用户本轮确认的全部修改已实现：[ADR-012](decisions/ADR-012-r22趋势纵轴与配置Excel关联.md)。九类数据项最小跨度/固定范围、按罐保存并供详情实时趋势使用；当前罐选择及新增曲线默认；所有Excel浅灰细边框；TANK-CONFIG-3独立罐组关联页（兼容旧格式）；复制保留原启用/隐藏/分组/三表等全部参数，仍须填写新罐号和地址后保存。
- 当前r22保持URL 127.0.0.1:55407、模拟网关55406、原数据output/r22-preview-1794033145639/isolated-data及原profile/授权。用户已添加第13个罐和COM2直连通道，更新完整保留配置revision5，不再视为纯loopback现用会话。原采集状态已恢复，升级器未下发仪表控制动作。
- 新运行快照output/r22-preview-runtime_20261107-u1，driver25508/Host28356/client43564；旧快照保留供回退。已用Computer Use核对实际新客户端登录页及r22地址，未自动登录。重开仍用output/client-viewing/打开r22客户端.cmd；账号不变，见同目录r22本机预览说明.txt。
- 切换前备份、配置及退出/启动核查：output/client-viewing/r22-update-1794037030533。原driver5988/Host65096正常退出，后台端口复用。全机仅新r22 Host；r20/r21端口仍关闭，未删除其他版本、数据、密封包或密钥。
- 验证总表output/r22-phase2-20261107/verification-summary.json：phase2-final-unit.trx（415/415）、axis-pure-result.json（10组）、integration-1794036710852（20组）、transfer/roundtrip-1794036477022（12组）、workbook-verification.json（10个文件/1234单元格，三模板数据公式验证原样）。全部QA仪表写0，1920×1080详情一屏通过；QA为隔离数据，现用会话未进行测试写入。
- 当前下一步由用户查看r22实际效果；本轮不额外封包或长测。历史进度中“待讨论”“12罐”“旧r22 PID65096”等为之前时点，均由本节取代。最新密封完整交付包仍是r21，不能把本机r22快照当作完整安装包发客户。

## 此前轮次记录（历史状态）

'''
assert text.startswith('# 项目进度')
progress.write_text(heading+text.split('\n',1)[1].lstrip(),encoding='utf-8')
# Correct the runtime metadata: a configured serial channel may be a physical
# device or a virtual simulator; configuration alone cannot prove which.
for f in [root/'output/client-viewing/r22-preview-session.json',root/'output/r22-preview-1794033145639/preview-session.json']:
    session=json.loads(f.read_text(encoding='utf-8-sig'))
    session['serialChannelConfigured']=True
    session['realDeviceAccess']=None
    session['deviceAccessNote']='Existing user COM2 configuration preserved; hardware versus virtual serial simulator not asserted.'
    f.write_text(json.dumps(session,ensure_ascii=False,indent=2),encoding='utf-8')
