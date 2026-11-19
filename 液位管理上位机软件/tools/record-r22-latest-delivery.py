"""Record the sealed r22 delivery and the observed cleanup outcome."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
qa = root / 'output/r22-latest-delivery-qa_20261107'
package = json.loads((qa / 'package.json').read_text(encoding='utf-8-sig'))
assert package['passed'] and package['build'] == '20261107-r22-u2'
record = root / 'output/cleanup-records/20261107-r22-latest-only'
cleanup_path = record / 'result.json'
cleanup = json.loads(cleanup_path.read_text(encoding='utf-8-sig')) if cleanup_path.exists() else None
progress_path = root / 'docs/project_memory/progress.md'
progress = progress_path.read_text(encoding='utf-8-sig')
start, end = '<!-- r22-latest-delivery:start -->', '<!-- r22-latest-delivery:end -->'
if start in progress:
    before, existing = progress.split(start, 1)
    _, after = existing.split(end, 1)
    progress = before + after.lstrip('\n')
assert progress.startswith('# 项目进度')
body = [start, '## r22 最新交付资料（2026-11-07，当前有效入口）', '',
    '- 用户本轮授权：r22仅保留最新版，删除较早r22程序及原客户交付资料；r20、源代码、客户原件、授权密钥和当前数据保留。此授权取代此前旧r22完整包永久保留约定。',
    '- 最新客户包：output/客户交付资料_r22_最新版_20261107/液位管理系统_r22_客户交付版_20261107_发.zip；'
    f"{package['bytes']} bytes，SHA256 {package['sha256']}。build=20261107-r22-u2，schema17。该密封包不可原位修改。",
    '- 完整自包含程序、安装及业务恢复工具、WebView2离线环境、两份Word/HTML指南、三类罐容表模板、V1.0.3本机模拟器已合并。安装指南统一说明r18直接升级、较早r22更新及全新安装；不再附旧u1说明。开通平均温度测量，未确定项目及英文仍后续开发。',
    '- 两份Word共10页已逐页渲染目检；新套件1222文件SHA与ZIP逐项校验，原生安装/桌面/恢复等10组通过，既有428项单元及r18升级证据沿用；本轮未修改业务代码，未新增六小时长测。证据output/r22-latest-delivery-qa_20261107。']
if package.get('presentationUpdatedAtUtc'):
    body += ['- 2026-11-08按用户明确授权在当前r22内修改总览数据行，并替换同名客户ZIP，仍为20261107-r22-u2。仪表密度、人工工况密度、净体积、质量拆成名称/单位/数值三行；人工密度调用现有单位精度规则（kg/m³一位、g/cm³四位）。1920/1366宽度×三种卡片大小、隐藏实测密度及换算共8项浏览器样例检查通过，API写入0；在线55407已实际返回新CSS。',
             '- 当前运行wwwroot、源构建wwwroot与交付套件已同步，Host未重启，后端二进制、数据和授权未改。交付ZIP的旧SHA D3198A65…12BAF60已由上方最新SHA替代；ZIP逐文件SHA及CRC全验通过，安装/恢复二进制原验证沿用。更新证据output/r22-latest-delivery-qa_20261107/inventory-layout_20261108/published.json，未保留旧完整客户包。运行目录少量旧哈希资源仅为已打开页面的延迟加载兼容，交付包仅含最新资源。']
if cleanup and cleanup['passed']:
    net = (cleanup['deletedBytes'] - cleanup['archiveBytes']) / 1024**3
    body += [f"- 清理已完成：{cleanup['deletedTargets']}个旧r22目标、{cleanup['deletedFiles']}文件，净回收约{net:.2f}GiB。原两套r22客户资料及旧构建/运行副本已删除。",
        f"- 旧验证记录{cleanup['evidenceFiles']}文件先全量SHA核验归档：output/cleanup-records/20261107-r22-latest-only/旧r22验证记录归档.zip；SHA256 {cleanup['archiveSha256']}。旧报告按ZIP内原output相对路径检索；下文旧客户包/QA原路径为历史记录，不再作为当前入口。",
        '- r20交付包SHA未变，供方私钥仅核对大小/时间元数据未变。清理限定本软件output，未涉及源代码、客户资料目录。u2运行目录、现用数据/profile及client-viewing保留。当前驱动占用的两份resume-u2-retry日志保留在r22-import-restore-qa_20261107，未清除其活动句柄。']
else:
    body += ['- 清理尚未完成：已建立23个旧r22目标的安全清单，约3.38GiB；尚未删除。需先正常关闭仍从u1目录运行的旧客户端、打开u2客户端并完成进程路径核对。当前后台与原数据不停止。以cleanup-records/20261107-r22-latest-only/result.json是否通过为实际完成依据。']
body += ['- 本机当前入口：output/client-viewing/打开r22客户端.cmd；后台55407，模拟网关55406，数据output/r22-preview-1794033145639/isolated-data，程序output/r22-preview-runtime_20261107-u2。旧客户端在执行本轮清理前已退出；清理未停止后台，未重新初始化或改绑客户COM2。清理后Host23416与driver37936保持、后台build=u2且旧目标0个；核验见同一cleanup-records目录的post-verification.json。', end, '']
progress_path.write_text('# 项目进度\n\n' + '\n'.join(body) + '\n' + progress[len('# 项目进度'):].lstrip('\n'), encoding='utf-8')
print(json.dumps({'recorded': True, 'cleanupCompleted': bool(cleanup and cleanup['passed']), 'package': package['zip']}, ensure_ascii=False))
