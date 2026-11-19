import { describeIndicators } from './indicators.js';
import { describeAnalog } from './analog.js';
import { splitTCP, withTCPPort, importedTCP, describeTCP } from './tcp-access.js';
const $ = (id) => document.getElementById(id);
const input = (id) => $(id);
const select = (id) => $(id);
let csrf = '', cfg, views = [], detailSlot = 0, logged = false, polling = false, configDirty = false;
let detailText = '';
let historyChoicesKey = '';
let restarting = false;
let bridgeNeedsGrant = false;
let upgradeUploading = false, upgrading = false, upgradePolling = false, upgradeStarted = 0, upgradeState;
const upgradeDisabled = new Map();
function renderUpgrade(s) {
    upgradeState = s || {};
    const phaseNames = { ready: '校验通过，等待确认', queued: '已受理', preparing: '重新校验', backing_up: '停服与备份', switching: '切换程序', starting: '启动服务', verifying: '启动验证', rolling_back: '恢复原程序', succeeded: '升级成功', rolled_back: '已恢复原程序', failed: '升级未执行', needs_attention: '需要人工维护' };
    $('upgrade-current').textContent = s?.current || '—';
    $('upgrade-target').textContent = s?.target || '—';
    $('upgrade-arch').textContent = s?.arch || '—';
    $('upgrade-notes').textContent = s?.notes || '选择厂家提供的.gwup升级文件';
    const phaseLabel = phaseNames[s?.phase] || '', description = s?.message || '当前启动方式未启用网页升级';
    $('upgrade-status').textContent = description.startsWith(phaseLabel) ? description : phaseLabel + '：' + description;
    if (input('upgrade-confirmed').dataset.id !== s?.id) {
        input('upgrade-confirmed').checked = false;
        input('upgrade-confirmed').dataset.id = s?.id || '';
    }
    $('upgrade-confirm').disabled = !s?.supported || s?.phase !== 'ready' || s?.busy || upgradeUploading;
    $('upgrade-upload').disabled = !s?.supported || s?.busy || upgradeUploading;
    input('upgrade-file').disabled = !s?.supported || s?.busy || upgradeUploading;
    if (s?.busy) {
        if (!upgrading) {
            upgradeStarted = Date.now();
            upgrading = true;
        }
        ;
        $('upgrade-progress').hidden = false;
        $('upgrade-progress').textContent = $('upgrade-status').textContent + '。请勿断电或重复提交；恢复后重新登录核对结果。';
        document.querySelectorAll('#configuration input,#configuration select,#configuration button,#system input,#system button,#reset-form input,#reset-form button,#reset-form textarea,#level-adjust-form input,#level-adjust-form select,#level-adjust-form button,#level-adjust-form textarea,#sync-time').forEach(e => { if (e.id === 'upgrade-refresh')
            return; if (!upgradeDisabled.has(e))
            upgradeDisabled.set(e, e.disabled); e.disabled = true; });
    }
    else {
        for (const [e, disabled] of upgradeDisabled)
            e.disabled = disabled;
        upgradeDisabled.clear();
        upgrading = false;
        if (s?.id) {
            $('upgrade-progress').hidden = false;
            $('upgrade-progress').textContent = $('upgrade-status').textContent;
        }
        ;
        $('upgrade-confirm').disabled = !s?.supported || s?.phase !== 'ready' || upgradeUploading;
        $('upgrade-upload').disabled = !s?.supported || upgradeUploading;
        input('upgrade-file').disabled = !s?.supported || upgradeUploading;
    }
    if (bridgeNeedsGrant)
        $('upgrade-confirm').disabled = true;
}
async function pollUpgrade() { if (upgradePolling || !upgrading)
    return; upgradePolling = true; try {
    const c = new AbortController(), timer = setTimeout(() => c.abort(), 4000);
    let r;
    try {
        r = await fetch('/api/upgrade/status', { credentials: 'same-origin', cache: 'no-store', signal: c.signal });
    }
    finally {
        clearTimeout(timer);
    }
    if (r.status === 401) {
        upgrading = false;
        showLogin();
        window.location.reload();
        return;
    }
    if (r.ok)
        renderUpgrade(await r.json());
}
catch {
    if (Date.now() - upgradeStarted > 300000) {
        upgrading = false;
        showLogin();
        $('upgrade-progress').textContent = '暂未确认升级结果，请重新打开网页；若仍无法访问，请厂家检查服务及升级日志。不要重新提交升级或断电。';
    }
}
finally {
    upgradePolling = false;
} }
bind('upgrade-refresh', async () => { renderUpgrade(await api('upgrade/status')); });
bind('upgrade-upload', async () => { if (configDirty)
    throw Error('请先保存或重新加载未保存的配置'); const file = input('upgrade-file').files?.[0]; if (!file || !file.name.toLowerCase().endsWith('.gwup'))
    throw Error('请选择厂家提供的.gwup升级文件'); if (file.size > 32 * 1024 * 1024)
    throw Error('升级文件不能超过32MiB'); const b = $('upgrade-upload'); upgradeUploading = true; b.disabled = true; $('upgrade-status').textContent = '正在上传并校验，请稍候…'; try {
    const c = new AbortController(), timer = setTimeout(() => c.abort(), 180000);
    let r;
    try {
        r = await fetch('/api/upgrade/upload', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/octet-stream', 'X-CSRF-Token': csrf }, body: file, signal: c.signal });
    }
    finally {
        clearTimeout(timer);
    }
    if (!r.ok)
        throw Error(await r.text());
    renderUpgrade(await r.json());
    message('校验通过，请核对版本和更新说明后确认安装');
}
catch (e) {
    renderUpgrade(await api('upgrade/status'));
    throw e;
}
finally {
    upgradeUploading = false;
    renderUpgrade(upgradeState);
} });
bind('upgrade-confirm', async () => { if (configDirty)
    throw Error('请先保存或重新加载未保存的配置'); if (!input('upgrade-confirmed').checked)
    throw Error('请确认维护窗口及现场状态'); if (upgradeState?.phase !== 'ready' || !upgradeState.id)
    throw Error('请先上传并通过校验'); upgrading = true; upgradeStarted = Date.now(); $('upgrade-progress').hidden = false; $('upgrade-progress').textContent = '正在提交升级请求，请勿重复操作。'; try {
    renderUpgrade(await api('upgrade/confirm', 'POST', { id: upgradeState.id, confirmed: true }));
    input('upgrade-confirmed').checked = false;
}
catch (e) {
    $('upgrade-progress').textContent = '请求是否受理尚未确认，正在查询状态；请勿重复提交。';
    message(String(e));
    void pollUpgrade();
} });
function clearPasswordFields() { for (const id of ['password', 'current-password', 'new-password', 'confirm-password'])
    input(id).value = ''; }
function showLogin() { logged = false; csrf = ''; clearPasswordFields(); $('workspace').hidden = true; $('login').hidden = false; }
function renderRestart(status) { const r = status.program_restart; const b = $('restart-program'); b.disabled = !r?.supported || r.pending || restarting; $('program-restart-status').textContent = r?.pending ? '程序重启任务已受理' : r?.error || (!r?.supported ? '当前启动方式未启用网页程序重启，请按部署说明使用服务启动。' : '仅重启网关程序，不重启整台设备。'); }
function renderHistoryMeters() {
    const meters = views.map(v => v.config).filter(m => m.slot).sort((a, b) => a.slot - b.slot);
    const key = JSON.stringify(meters.map(m => [m.slot, m.name, m.enabled]));
    if (key === historyChoicesKey)
        return;
    historyChoicesKey = key;
    const list = select('history-slot'), chosen = list.value || '0';
    list.replaceChildren(new Option('全部液位计（全部槽位）', '0'));
    const configured = document.createElement('optgroup');
    configured.label = '当前配置的液位计';
    const other = document.createElement('optgroup');
    other.label = '其他槽位（仍可导出历史）';
    for (let slot = 1; slot <= 64; slot++) {
        const meter = meters.find(m => m.slot === slot);
        const label = '槽位' + slot + ' · ' + (meter ? meter.name + (meter.enabled ? '' : '（停用）') : '未配置');
        (meter ? configured : other).append(new Option(label, String(slot)));
    }
    if (configured.children.length)
        list.append(configured);
    if (other.children.length)
        list.append(other);
    list.value = chosen;
}
function message(text) { $('message').textContent = text; $('message').hidden = false; setTimeout(() => $('message').hidden = true, 8000); }
async function api(path, method = 'GET', body) { const r = await fetch('/api/' + path, { method, credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf }, body: body === undefined ? undefined : JSON.stringify(body) }); if (!r.ok) {
    if (r.status === 401) {
        showLogin();
    }
    throw Error(await r.text());
} return r.json(); }
function labelTable(id) { const body = $(id); const headings = Array.from(body.closest('table').querySelectorAll('thead th')).map(e => e.textContent || ''); for (const row of Array.from(body.rows))
    Array.from(row.cells).forEach((td, i) => td.dataset.label = headings[i] || ''); }
function cell(row, text) { const td = row.insertCell(); if (text !== undefined)
    td.textContent = text; return td; }
function rowInput(row, value, onchange, type = 'text') { const e = document.createElement('input'); e.type = type; e.value = String(value); e.addEventListener('change', () => onchange(e.value)); cell(row).append(e); return e; }
function rowCheck(row, value, onchange, label) { const e = document.createElement('input'); e.type = 'checkbox'; e.checked = value; if (label)
    e.setAttribute('aria-label', label); e.addEventListener('change', () => onchange(e.checked)); cell(row).append(e); }
function rowSelect(row, value, choices, onchange) { const e = document.createElement('select'); for (const [v, t] of choices) {
    const o = document.createElement('option');
    o.value = String(v);
    o.textContent = t;
    e.append(o);
} e.value = String(value); e.addEventListener('change', () => onchange(e.value)); cell(row).append(e); }
function renderConfig() {
    const p = $('port-settings');
    p.replaceChildren();
    for (const port of cfg.ports) {
        const row = p.insertRow();
        cell(row, 'COM' + port.id);
        rowSelect(row, port.role, [['south_rtu_master', '南向采集'], ['north_rtu_slave', '北向RTU'], ['disabled', '停用']], v => port.role = v);
        rowSelect(row, port.baud, [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200].map(v => [v, String(v)]), v => port.baud = Number(v));
        rowSelect(row, port.parity, [['E', '偶校验'], ['O', '奇校验'], ['N', '无校验']], v => port.parity = v);
        rowSelect(row, port.stop_bits, [[1, '1'], [2, '2']], v => port.stop_bits = Number(v));
        rowInput(row, port.timeout_ms, v => port.timeout_ms = Number(v), 'number');
        rowInput(row, port.unit_id, v => port.unit_id = Number(v), 'number');
        rowCheck(row, port.allow_writes, v => port.allow_writes = v);
    }
    const t = $('meter-settings');
    t.replaceChildren();
    for (const meter of cfg.meters) {
        const row = t.insertRow();
        cell(row, String(meter.slot));
        rowInput(row, meter.name, v => meter.name = v);
        rowSelect(row, meter.port, [[1, 'COM1'], [2, 'COM2'], [3, 'COM3'], [4, 'COM4']], v => meter.port = Number(v));
        rowInput(row, meter.address, v => meter.address = Number(v), 'number');
        rowCheck(row, meter.enabled, v => meter.enabled = v);
        rowCheck(row, meter.temperature_detail, v => meter.temperature_detail = v);
        rowCheck(row, meter.density_detail, v => meter.density_detail = v);
        rowCheck(row, meter.analog, v => meter.analog = v, '槽位' + meter.slot + ' 模拟量采集');
        const b = document.createElement('button');
        b.className = 'secondary';
        b.textContent = '删除';
        b.addEventListener('click', () => { captureSettings(); cfg.meters = cfg.meters.filter(v => v.slot !== meter.slot); configDirty = true; renderConfig(); });
        cell(row).append(b);
    }
    const actions = $('action-settings');
    actions.replaceChildren();
    for (const [bit, name] of [[1, '测量Ⅰ'], [16, '测量Ⅱ（界面）'], [32, '测量Ⅲ（罐底）'], [4, '停止'], [2, '上升'], [256, '下降'], [64, '远程密度'], [8, '重复性检查'], [128, '液体震荡回避'], [512, '远程液位修改（校准）']]) {
        const label = document.createElement('label');
        label.className = 'check';
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = cfg.allowed_actions.includes(bit);
        cb.addEventListener('change', () => { cfg.allowed_actions = cb.checked ? [...cfg.allowed_actions.filter(a => a !== bit), bit] : cfg.allowed_actions.filter(a => a !== bit); });
        label.append(cb, document.createTextNode(name));
        actions.append(label);
    }
    select('mode').value = cfg.mode;
    input('fast-ms').value = String(cfg.fast_ms);
    input('detail-ms').value = String(cfg.detail_ms);
    input('tcp-port').value = String(splitTCP(cfg.tcp.listen).port);
    input('tcp-unit').value = String(cfg.tcp.unit_id);
    input('tcp-clients').value = String(cfg.tcp.max_clients);
    input('tcp-enabled').checked = cfg.tcp.enabled;
    input('tcp-writes').checked = cfg.tcp.allow_writes;
    input('history-period').value = String(cfg.history.period_sec);
    input('history-days').value = String(cfg.history.retention_days);
    input('history-max').value = String(cfg.history.max_mb);
    $('config-version').textContent = '配置版本 ' + cfg.revision + ' · 已绑定 ' + cfg.meters.length + ' 台' + (configDirty ? ' · 有未保存修改' : '');
    labelTable('port-settings');
    labelTable('meter-settings');
}
function quality(v) { if (v.quality & 16)
    return '停用'; const s = []; if (!(v.quality & 1))
    s.push('未采集');
else if (!(v.quality & 2))
    s.push('离线');
else if (!(v.quality & (4 | 256)))
    s.push('正常'); if ((v.quality & 256) && !(v.quality & 4))
    s.push('部分采集失败，保留旧值'); if (v.quality & 4)
    s.push('过期 / 部分数据缺失'); if (v.quality & 8)
    s.push('仪表故障'); if (v.quality & 32)
    s.push('寄存器不支持'); if (v.quality & 128)
    s.push('时间未确认'); return s.join(' · '); }
function renderData() { const table = $('readings'); table.replaceChildren(); const port = Number(select('port-filter').value); for (const v of views) {
    if (!v.config.slot || port && v.config.port !== port)
        continue;
    const row = table.insertRow();
    cell(row, v.config.slot + ' / ' + v.config.name);
    cell(row, 'COM' + v.config.port + ' / ' + v.config.address);
    cell(row, v.valid & 1 ? v.level_mm.toFixed(1) : '—');
    cell(row, v.valid & 2 ? v.temperature_c.toFixed(2) : '—');
    cell(row, v.valid & 4 ? v.density_kg_m3.toFixed(1) : '—');
    cell(row, v.age_ms === 4294967295 ? '未采集' : (v.age_ms / 1000).toFixed(1) + ' 秒');
    const badge = document.createElement('span');
    badge.className = 'badge' + (v.quality & ~3 ? ' warn' : '');
    badge.textContent = quality(v);
    cell(row).append(badge);
    const b = document.createElement('button');
    b.className = 'secondary';
    b.textContent = detailSlot === v.config.slot ? '收起' : '查看';
    b.setAttribute('aria-expanded', String(detailSlot === v.config.slot));
    b.setAttribute('aria-controls', 'meter-detail');
    b.dataset.slot = String(v.config.slot);
    b.addEventListener('click', () => { detailSlot = detailSlot === v.config.slot ? 0 : v.config.slot; input('detail-live').checked = true; renderData(); if (detailSlot)
        $('meter-detail').scrollIntoView({ behavior: 'smooth', block: 'start' }); });
    cell(row).append(b);
} $('enabled-count').textContent = String(views.filter(v => v.config.enabled).length); $('online-count').textContent = String(views.filter(v => v.config.enabled && (v.quality & 2)).length); $('stale-count').textContent = String(views.filter(v => v.config.enabled && (v.quality & 4)).length); labelTable('readings'); renderDetail(); }
function renderDetail() {
    const pane = $('meter-detail');
    const v = views.find(v => v.config.slot === detailSlot);
    if (!detailSlot || !v) {
        pane.hidden = true;
        return;
    }
    pane.hidden = false;
    $('detail-title').textContent = '槽位 ' + detailSlot + ' · ' + v.config.name;
    if (!input('detail-live').checked && detailText)
        return;
    const analog = describeAnalog(v);
    renderAnalog(analog);
    const groups = [['液位及变化率', 'IR0～5'], ['温度状态及平均温度', 'IR22～26'], ['密度、浮子状态及故障', 'IR49～52'], ['16点温度', 'IR6～21'], ['16点密度', 'IR33～48'], ['模拟量', 'IR27～30']];
    detailText = JSON.stringify({ 仪表编号: v.config.slot, 名称: v.config.name, 端口: 'COM' + v.config.port, 仪表地址: v.config.address, 绑定版本: v.config.binding_version, 快速完整采集时间: v.fast_complete_ms ? new Date(v.fast_complete_ms).toLocaleString() : '未完成', 质量说明: quality(v), 质量字: v.quality, 有效字: v.valid, 最旧快速数据年龄毫秒: v.age_ms, 液位mm: v.valid & 1 ? v.level_mm : null, 平均温度摄氏度: v.valid & 2 ? v.temperature_c : null, 当前密度kg每立方米: v.valid & 4 ? v.density_kg_m3 : null, 采集组: v.groups.map((g, i) => ({ 组名: groups[i][0], 寄存器: groups[i][1], 最近成功时间: g.time_ms ? new Date(g.time_ms).toLocaleString() : '尚未成功采集', 采集时间已确认: g.time_trusted, 成功次数: g.sequence, 累计失败次数: g.failures, 寄存器不支持: g.unsupported, 最近错误: g.error || '无', time_ms: g.time_ms })), 温度原始值: v.raw.slice(6, 22), 温度状态: v.raw.slice(22, 26), 密度原始值: v.raw.slice(33, 49), 模拟量: analog, 浮子状态: v.raw[51], 故障位: v.raw[52] }, null, 2);
    $('detail-content').textContent = detailText;
    $('detail-note').textContent = '累计失败次数成功后不清零；温度原值按int16解码后÷100为℃，密度原值÷10为kg/m³。时间可信与数值有效分别判断。';
}
function renderAnalog(analog) {
    $('analog-status').textContent = analog.采集状态;
    $('analog-status').className = 'badge' + (analog.数据.every(p => p.有效) && !analog.显示为旧值 ? '' : ' warn');
    $('analog-meta').textContent = ['最近成功：' + (analog.最近成功时间毫秒 ? new Date(analog.最近成功时间毫秒).toLocaleString() : '尚无成功数据'), '数据年龄：' + (analog.数据年龄毫秒 === null ? '—' : (analog.数据年龄毫秒 / 1000).toFixed(1) + '秒'), '目标周期：' + analog.目标周期毫秒 / 1000 + '秒', '累计失败：' + analog.累计失败次数, '最近错误：' + analog.最近错误, analog.采集时间已确认 ? '采集时间已确认' : '采集时间未确认'].join(' · ');
    const table = $('analog-values');
    table.replaceChildren();
    for (const point of analog.数据) {
        const row = table.insertRow();
        cell(row, point.寄存器);
        cell(row, point.数值 === null ? '—' : point.数值.toFixed(point.单位 === 'mA' ? 3 : 2));
        cell(row, point.单位);
        cell(row, point.原始值 === null ? '—' : String(point.原始值));
        cell(row, String(point.网关地址));
    }
    labelTable('analog-values');
}
function closeDetail() { const slot = detailSlot; detailSlot = 0; detailText = ''; renderData(); const button = $('readings').querySelector('[data-slot="' + slot + '"]'); button?.focus(); }
async function copyDetail() {
    input('detail-live').checked = false;
    const text = $('detail-content').textContent || '';
    if (!text)
        throw Error('当前没有可复制的详情');
    try {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
            message('详情已复制，已暂停详情刷新');
            return;
        }
    }
    catch { }
    const box = $('copy-text');
    box.value = text;
    $('copy-fallback').hidden = false;
    box.focus();
    box.select();
    box.setSelectionRange(0, box.value.length);
    let copied = false;
    try {
        copied = document.execCommand('copy');
    }
    catch { }
    if (copied) {
        $('copy-fallback').hidden = true;
        message('详情已复制，已暂停详情刷新');
    }
    else
        message('浏览器未允许自动复制，已选中详情，请长按文本选择复制，或按Ctrl+C');
}
async function refresh() { if (!logged || polling || restarting || upgrading)
    return; polling = true; try {
    const [data, status] = await Promise.all([api('data'), api('status')]);
    views = data;
    tcpSaved = status.tcp || cfg.tcp;
    tcpAuthorized = status.license?.state === 'bridge' || !status.license?.required || !!status.license?.valid;
    renderTCPAccess();
    if (!configDirty && status.config_revision !== cfg.revision) {
        cfg = await api('config');
        renderConfig();
    }
    renderLicense(status.license);
    renderRestart(status);
    renderData();
    renderHistoryMeters();
    $('connection').textContent = (status.simulate ? '模拟运行' : '设备运行') + (status.mode === 'maintenance' ? ' · 维护（采集暂停）' : '') + ' · 点表 V' + status.map_version;
    $('clock').textContent = new Date(status.time_ms).toLocaleTimeString();
    $('time-quality').textContent = status.time_trusted ? (status.clock?.source === 'rtc' ? '设备时间已确认（RTC恢复）' : '设备时间已确认') : '设备时间尚未确认';
    const clock = status.clock;
    $('clock-detail').textContent = clock ? [clock.reason, clock.rtc_saved ? 'RTC已保存' : 'RTC未保存', clock.calibrated_ms ? '最近校核：' + new Date(clock.calibrated_ms).toLocaleString() : '尚无校时记录', clock.warning || ''].filter(Boolean).join(' · ') : '';
    $('status-detail').textContent = JSON.stringify(status, null, 2);
    renderIndicators(status.indicators, status.simulate, status.mode === 'maintenance');
    if (!$('maintenance').hidden) {
        const [events, commands] = await Promise.all([api('events'), api('commands')]);
        $('event-detail').textContent = JSON.stringify(events, null, 2);
        $('command-detail').textContent = JSON.stringify(commands, null, 2);
        renderCycles(commands);
        renderLevelResult(commands);
    }
    renderUpgrade(status.upgrade);
    if (!$('system').hidden || !$('configuration').hidden)
        await refreshNetwork(false);
}
catch (e) {
    $('connection').textContent = '连接异常';
    message(String(e));
}
finally {
    polling = false;
} }
async function enter() { $('restart-progress').hidden = true; input('restart-confirmed').checked = false; logged = true; $('login').hidden = true; $('workspace').hidden = false; cfg = await api('config'); renderConfig(); await refresh(); await refreshNetwork(true); if (networkState?.busy) {
    document.querySelector('nav [data-page="system"]')?.click();
} }
function bind(id, fn) { $(id).addEventListener('click', () => Promise.resolve().then(fn).catch(e => message(String(e)))); }
function captureSettings() { cfg.mode = select('mode').value; cfg.fast_ms = Number(input('fast-ms').value); cfg.detail_ms = Number(input('detail-ms').value); cfg.tcp.listen = withTCPPort(cfg.tcp.listen, Number(input('tcp-port').value)); cfg.tcp.unit_id = Number(input('tcp-unit').value); cfg.tcp.max_clients = Number(input('tcp-clients').value); cfg.tcp.enabled = input('tcp-enabled').checked; cfg.tcp.allow_writes = input('tcp-writes').checked; cfg.history.period_sec = Number(input('history-period').value); cfg.history.retention_days = Number(input('history-days').value); cfg.history.max_mb = Number(input('history-max').value); }
bind('sync-time', async () => { const result = await api('time', 'POST', { utc_ms: Date.now() }); message([result.clock?.reason || '时间已校核', result.clock?.warning || ''].filter(Boolean).join('；')); await refresh(); });
$('login-form').addEventListener('submit', async (e) => { e.preventDefault(); try {
    const session = await api('login', 'POST', { password: input('password').value });
    input('password').value = '';
    csrf = session.csrf;
    await enter();
}
catch (e) {
    message(String(e));
} });
document.querySelectorAll('nav [data-page]').forEach(b => b.addEventListener('click', () => { document.querySelectorAll('.page').forEach(p => p.hidden = p.id !== b.dataset.page); document.querySelectorAll('nav [data-page]').forEach(v => v.classList.remove('active')); b.classList.add('active'); void refresh(); }));
select('port-filter').addEventListener('change', renderData);
bind('detail-close', closeDetail);
bind('detail-copy', copyDetail);
bind('copy-close', () => { $('copy-fallback').hidden = true; });
input('detail-live').addEventListener('change', renderDetail);
$('configuration').addEventListener('input', () => { configDirty = true; $('config-version').textContent = '有未保存修改，请保存并应用配置'; });
$('configuration').addEventListener('change', () => { configDirty = true; });
window.addEventListener('beforeunload', event => { if (configDirty) {
    event.preventDefault();
    event.returnValue = '';
} });
bind('logout', async () => { await api('logout', 'POST'); showLogin(); });
bind('add-meter', () => { captureSettings(); const slot = Array.from({ length: 64 }, (_, i) => i + 1).find(i => !cfg.meters.some(m => m.slot === i)); if (!slot)
    throw Error('64个槽位已满'); const port = cfg.ports.find(p => p.role === 'south_rtu_master' && cfg.meters.filter(m => m.enabled && m.port === p.id).length < 16); if (!port)
    throw Error('没有可用的南向口'); const address = Array.from({ length: 243 }, (_, i) => i + 1).find(a => !cfg.meters.some(m => m.enabled && m.port === port.id && m.address === a)); configDirty = true; cfg.meters.push({ slot, port: port.id, address, name: '液位计' + slot, enabled: true, binding_version: cfg.slot_versions[slot - 1], temperature_detail: true, density_detail: true, analog: false }); renderConfig(); });
bind('save-config', async () => { const b = $('save-config'); b.disabled = true; try {
    captureSettings();
    cfg.mode = select('mode').value;
    cfg.fast_ms = Number(input('fast-ms').value);
    cfg.detail_ms = Number(input('detail-ms').value);
    cfg.tcp.listen = withTCPPort(cfg.tcp.listen, Number(input('tcp-port').value));
    cfg.tcp.unit_id = Number(input('tcp-unit').value);
    cfg.tcp.max_clients = Number(input('tcp-clients').value);
    cfg.tcp.enabled = input('tcp-enabled').checked;
    cfg.history.period_sec = Number(input('history-period').value);
    cfg.history.retention_days = Number(input('history-days').value);
    cfg.history.max_mb = Number(input('history-max').value);
    cfg = await api('config', 'PUT', cfg);
    configDirty = false;
    renderConfig();
    message('配置已应用，版本 ' + cfg.revision);
    await refresh();
}
finally {
    b.disabled = false;
} });
bind('export-config', async () => { const saved = await api('config'); const url = URL.createObjectURL(new Blob([JSON.stringify(saved, null, 2)], { type: 'application/json' })); const a = document.createElement('a'); a.href = url; a.download = 'gateway-config.json'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); });
input('import-config').addEventListener('change', async () => { try {
    const file = input('import-config').files?.[0];
    if (!file)
        return;
    if (file.size > 65536)
        throw Error('配置文件过大');
    const imported = JSON.parse(await file.text());
    if (!Array.isArray(imported.slot_versions) || !((imported.schema_version === 2 && imported.slot_versions.length === 32 && imported.meters.every((m) => m.slot >= 1 && m.slot <= 32)) || (imported.schema_version === 3 && imported.slot_versions.length === 64)))
        throw Error('仅接受schema2的32槽位或schema3的64槽位配置');
    imported.schema_version = 3;
    const current = await api('config');
    imported.tcp.listen = importedTCP(current.tcp.listen, imported.tcp.listen);
    imported.revision = current.revision;
    imported.slot_versions = current.slot_versions;
    for (const m of imported.meters)
        m.binding_version = current.slot_versions[m.slot - 1];
    cfg = imported;
    configDirty = true;
    renderConfig();
    message('配置已载入，请检查后保存；保留本机TCP接入IP，仅导入服务端口，网口须在系统维护设置');
}
catch (e) {
    message(String(e));
} });
const localTime = (d) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
input('history-to').value = localTime(new Date());
input('history-from').value = localTime(new Date(Date.now() - 86400000));
bind('download-history', () => { const from = new Date(input('history-from').value).getTime(), to = new Date(input('history-to').value).getTime(), slot = Number(select('history-slot').value); if (!Number.isFinite(from) || !Number.isFinite(to) || to < from || to - from > 7 * 86400000)
    throw Error('导出范围须为0～7天'); if (!Number.isInteger(slot) || slot < 0 || slot > 64)
    throw Error('请选择液位计或全部槽位'); const a = document.createElement('a'); a.href = '/api/history.csv?from=' + from + '&to=' + to + '&slot=' + slot; a.download = slot ? 'level-history-slot-' + String(slot).padStart(2, '0') + '.csv' : 'level-history.csv'; a.click(); });
let reviewHadPending = false;
function renderCycles(commands) {
    const table = $('control-cycles');
    table.replaceChildren();
    const pending = [];
    let failed = 0, uncertain = 0;
    if (!commands.control_error)
        for (const slot of commands.controls ?? []) {
            for (const [key, label] of [['main', '正常动作'], ['stop', '独立停止']]) {
                const cycle = slot[key];
                if (cycle.state !== 4 && cycle.state !== 5)
                    continue;
                if (cycle.state === 4)
                    failed++;
                else
                    uncertain++;
                pending.push(cycle.request.id);
                const row = table.insertRow();
                const name = views.find(v => v.config.slot === slot.slot)?.config.name;
                const target = cycle.request.port ? '（原目标 COM' + cycle.request.port + ' / 地址' + cycle.request.unit + '）' : '';
                cell(row, String(slot.slot) + (name ? ' / ' + name : '') + target);
                cell(row, label);
                cell(row, String(cycle.value));
                const state = cycle.state === 4 ? '控制失败（状态4）' : '结果不确定（状态5）';
                cell(row, state);
                cell(row, String(cycle.error));
                cell(row, (cycle.message || '请核对控制记录') + '；' + (cycle.sent ? '动作可能已发送' : '未发送动作'));
                const b = document.createElement('button');
                b.textContent = cycle.state === 4 ? '复位失败结果' : '核查并复位';
                b.className = 'secondary';
                b.addEventListener('click', () => {
                    input('reset-slot').value = String(slot.slot);
                    input('reset-channel').value = label;
                    input('reset-id').value = cycle.request.id;
                    input('reset-state').value = state;
                    input('reset-channel').dataset.stop = String(key === 'stop');
                    input('reset-operator').value = '';
                    $('reset-reason').value = '';
                    input('reset-verified').checked = false;
                    $('reset-form').hidden = false;
                    $('reset-form').scrollIntoView({ behavior: 'smooth', block: 'center' });
                });
                cell(row).append(b);
            }
        }
    $('uncertain-count').textContent = commands.control_error ? '（控制存储异常）' : pending.length ? '（' + failed + '项控制失败，' + uncertain + '项结果不确定）' : '（当前无需处理）';
    $('control-empty').textContent = commands.control_error ? '控制存储异常：' + commands.control_error : pending.length ? '选择一条记录，核对现场状态后复位。复位只清除选中结果，不停止浮子、不重发动作。' : '当前没有状态4或5的控制记录，无需复位。';
    $('control-table').hidden = pending.length === 0;
    if (pending.length && !reviewHadPending)
        $('review-panel').open = true;
    reviewHadPending = pending.length > 0;
    if (!pending.includes(input('reset-id').value)) {
        $('reset-form').hidden = true;
        input('reset-id').value = '';
        input('reset-verified').checked = false;
    }
    labelTable('control-cycles');
}
bind('cancel-reset', () => { $('reset-form').hidden = true; input('reset-id').value = ''; input('reset-verified').checked = false; });
$('reset-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const b = $('reset-form').querySelector('button[type="submit"]');
    b.disabled = true;
    try {
        const req = { slot: Number(input('reset-slot').value), stop: input('reset-channel').dataset.stop === 'true', id: input('reset-id').value, operator: input('reset-operator').value.trim(), reason: $('reset-reason').value.trim(), site_verified: input('reset-verified').checked };
        if (!req.id || !req.site_verified || !req.operator || req.reason.length < 4)
            throw Error('请填写核查人员、至少4字的核查结果或原因，并勾选现场确认');
        await api('control/maintenance-reset', 'POST', req);
        input('reset-id').value = '';
        input('reset-verified').checked = false;
        $('reset-form').hidden = true;
        message('控制结果已复位，记录已保存；未停止浮子，未重发动作');
        await refresh();
    }
    catch (e) {
        message(String(e));
    }
    finally {
        b.disabled = false;
    }
});
$('password-change-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const button = $('change-password');
    button.disabled = true;
    try {
        if (configDirty)
            throw Error('请先保存或重新加载未保存的配置，再修改密码');
        const current = input('current-password').value, next = input('new-password').value, confirm = input('confirm-password').value;
        if (next !== confirm || next.length < 12 || next.length > 128 || next.trim() !== next || !/^[ -~]+$/.test(next))
            throw Error('新密码须12～128个ASCII字符，两次一致且无首尾空格');
        const result = await api('admin/password', 'POST', { current_password: current, new_password: next, confirm_password: confirm });
        showLogin();
        message('密码已修改，请用新密码重新登录。' + (result.warning || ''));
    }
    catch (error) {
        message(String(error));
    }
    finally {
        clearPasswordFields();
        button.disabled = false;
    }
});
bind('restart-program', async () => {
    if (configDirty)
        throw Error('请先保存或重新加载未保存的配置，再重启程序');
    if (!input('restart-confirmed').checked)
        throw Error('请先确认现场状态及通讯中断影响');
    const button = $('restart-program');
    button.disabled = true;
    try {
        await api('maintenance/restart', 'POST', { confirmed: true });
        input('restart-confirmed').checked = false;
        restarting = true;
        $('restart-progress').hidden = false;
        $('restart-progress').textContent = '程序重启已受理，正在等待重新连接。请勿断电或重复提交。';
        void reconnectAfterRestart();
    }
    catch (error) {
        button.disabled = false;
        message(String(error));
    }
});
async function reconnectAfterRestart() {
    const deadline = Date.now() + 120000;
    while (Date.now() < deadline) {
        await new Promise(resolve => setTimeout(resolve, 2000));
        try {
            const controller = new AbortController();
            const timer = setTimeout(() => controller.abort(), 4000);
            let response;
            try {
                response = await fetch('/api/session', { credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            }
            finally {
                clearTimeout(timer);
            }
            if (response.status === 401) {
                restarting = false;
                showLogin();
                $('restart-progress').textContent = '网页已恢复连接，请登录后核对运行时间和采集数据。';
                return;
            }
            if (response.ok) {
                const status = await api('status');
                if (!status.program_restart?.pending && status.program_restart?.error) {
                    restarting = false;
                    $('restart-progress').textContent = status.program_restart.error;
                    await refresh();
                    return;
                }
            }
        }
        catch { }
    }
    restarting = false;
    $('restart-progress').textContent = '暂未确认程序恢复，请检查网线及设备服务日志；不要自动重复重启。';
    showLogin();
}
let levelCycle, levelCommands;
function renderLevelResult(commands) {
    levelCommands = commands;
    const list = select('level-slot'), chosen = list.value;
    const options = cfg.meters.filter(m => m.enabled).map(m => [String(m.slot), '槽位' + m.slot + ' · ' + m.name]);
    if (list.dataset.key !== JSON.stringify(options)) {
        list.replaceChildren(...options.map(([v, t]) => new Option(t, v)));
        list.dataset.key = JSON.stringify(options);
        if (chosen)
            list.value = chosen;
    }
    const slot = Number(list.value);
    levelCycle = commands.controls.find((s) => s.slot === slot)?.main;
    const v = views.find(v => v.config.slot === slot);
    const c = levelCycle;
    const isLevel = c?.request?.command === 11;
    const stateNames = ['空闲', '已受理', '执行中', '修改已确认', '失败 / 已取消', '结果不确定'];
    $('level-result').textContent = (v ? (v.valid & 1 ? '当前采集液位 ' + v.level_mm.toFixed(1) + ' mm（计算前还会重新读取）' : '当前液位无效') + '；' : '') + (c ? stateNames[c.state] : '未配置') + (isLevel ? '；目标 ' + (c.request.level_target_raw / 10 || 0).toFixed(1) + ' mm；' + (c.message || '等待总线执行') : c?.state ? '；已有普通动作结果，请先处理并复位' : '');
    $('level-submit').disabled = !v || !cfg.allowed_actions.includes(512) || !!c?.state || restarting;
    $('level-reset').disabled = !isLevel || ![3, 4, 5].includes(c.state) || restarting;
    $('level-reset').dataset.id = isLevel ? c.request.id : '';
}
select('level-slot').addEventListener('change', () => { input('level-confirm').checked = false; input('level-target').value = ''; if (levelCommands)
    renderLevelResult(levelCommands); void refresh(); });
async function reloadLevelControls() { const [commands, data] = await Promise.all([api('commands'), api('data')]); views = data; renderCycles(commands); renderLevelResult(commands); $('command-detail').textContent = JSON.stringify(commands, null, 2); }
function levelHuman() { const operator = input('level-operator').value.trim(), reason = $('level-reason').value.trim(); if (!input('level-confirm').checked || !operator || reason.length < 4)
    throw Error('请填写操作人员及至少4字的原因，核对现场后勾选确认'); return { operator, reason, site_verified: true }; }
$('level-adjust-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const b = $('level-submit');
    b.disabled = true;
    try {
        const human = levelHuman();
        const text = input('level-target').value;
        if (!/^-?\d+(?:\.\d)?$/.test(text))
            throw Error('目标液位最多保留1位小数');
        const raw = Math.round(Number(text) * 10);
        if (!Number.isSafeInteger(raw) || raw < -99999 || raw > 999999)
            throw Error('目标液位应为-9999.9～99999.9mm');
        await api('control/level-adjust', 'POST', { slot: Number(select('level-slot').value), target_raw: raw, ...human });
        input('level-confirm').checked = false;
        message('液位修改已受理，请查看最终状态；不会自动重写');
        await reloadLevelControls();
        await refresh();
    }
    catch (e) {
        message(String(e));
    }
    finally {
        b.disabled = !!levelCycle?.state || !cfg.allowed_actions.includes(512) || restarting;
    }
});
bind('level-reset', async () => { const human = levelHuman(); const id = $('level-reset').dataset.id; if (!id)
    throw Error('请选择本次液位修改结果'); await api('control/maintenance-reset', 'POST', { slot: Number(select('level-slot').value), stop: false, id, ...human }); input('level-confirm').checked = false; message('已复位本次结果；未恢复原偏移，未重写液位'); await reloadLevelControls(); await refresh(); });
// Network changes are a manual two-step transaction. Never confirm automatically.
let networkState, networkDirty = false;
const networkDisabled = new Map();
function renderNetwork(n, fill) {
    networkState = n;
    renderTCPAccess();
    $('network-status').textContent = n.message + (n.deadline_ms ? ' · 确认截止：' + new Date(n.deadline_ms).toLocaleTimeString() : '');
    $('network-warning').textContent = (n.fun_available ? '检测到厂家FUN配置；实际按键恢复仍需现场验收。' : '未确认厂家FUN服务，请先按部署说明检查或使用Console恢复。') + ' ' + (n.warning || '');
    $('network-actual').textContent = '当前实际地址：' + (n.actual || []).map((p) => p.name + ' ' + p.ip + ' / ' + p.mask).join('；');
    if (fill) {
        for (const p of n.settings?.interfaces || []) {
            for (const k of ['ip', 'mask', 'gateway'])
                input('net-' + p.name + '-' + k).value = p[k] || '';
        }
        select('net-tcp-interface').value = n.settings?.tcp_interface || 'eth1';
        networkDirty = false;
        input('network-verified').checked = false;
    }
    $('network-apply').disabled = !n.supported || n.busy;
    document.querySelectorAll('#network-form input,#network-form select').forEach(e => e.disabled = !n.supported || n.busy);
    $('network-trial').hidden = n.phase !== 'trial';
    $('network-confirm').disabled = n.phase !== 'trial';
    if (input('network-connected').dataset.id !== n.id) {
        input('network-connected').checked = false;
        input('network-connected').dataset.id = n.id || '';
    }
    const links = $('network-links');
    links.replaceChildren();
    if (n.busy) {
        for (const p of n.settings?.interfaces || []) {
            const a = document.createElement('a');
            a.href = location.protocol + '//' + p.ip + ':' + (n.web_port || location.port || '8088') + '/';
            a.textContent = '打开' + p.name + '新地址：' + p.ip;
            a.target = '_blank';
            a.rel = 'noopener';
            links.append(a);
        }
    }
    document.querySelectorAll('#configuration input,#configuration select,#configuration button,#system input,#system select,#system button,#system textarea,#reset-form input,#reset-form button,#level-adjust-form input,#level-adjust-form select,#level-adjust-form button,#sync-time').forEach(e => {
        if (e.closest('#network-panel'))
            return;
        if (n.busy) {
            if (!networkDisabled.has(e))
                networkDisabled.set(e, e.disabled);
            e.disabled = true;
        }
        else if (networkDisabled.has(e)) {
            e.disabled = networkDisabled.get(e);
            networkDisabled.delete(e);
        }
    });
}
async function refreshNetwork(fill) { const n = await api('network'); renderNetwork(n, fill && !networkDirty); }
$('network-form').addEventListener('input', () => networkDirty = true);
$('network-form').addEventListener('change', () => networkDirty = true);
bind('network-refresh', async () => { networkDirty = false; await refreshNetwork(true); });
$('network-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    try {
        if (configDirty)
            throw Error('请先保存或重新加载端口与仪表配置');
        if (!input('network-verified').checked)
            throw Error('请先核对网络地址并确认通讯中断影响');
        const settings = { interfaces: ['eth0', 'eth1'].map(name => ({ name, ip: input('net-' + name + '-ip').value.trim(), mask: input('net-' + name + '-mask').value.trim(), gateway: input('net-' + name + '-gateway').value.trim() })), tcp_interface: select('net-tcp-interface').value };
        $('network-apply').disabled = true;
        renderNetwork(await api('network/apply', 'POST', { settings, confirmed: true }), false);
        networkDirty = false;
        input('network-verified').checked = false;
        message('网络修改已受理。请打开上方新地址，重新登录并确认；不要重复提交。');
    }
    catch (e) {
        message(String(e));
        await refreshNetwork(false);
    }
});
bind('network-confirm', async () => {
    if (!input('network-connected').checked)
        throw Error('请先核对通过新地址连接及通讯结果');
    renderNetwork(await api('network/confirm', 'POST', { id: networkState.id, confirmed: true }), false);
    input('network-connected').checked = false;
    message('确认已受理，请等待状态显示已保存');
});
function renderLicense(s) {
    const bridge = s?.state === 'bridge';
    bridgeNeedsGrant = bridge && !s.file_valid;
    if (bridge)
        $('license-help').textContent = '过渡版按原配置采集。下载本机申请发给厂家，将返回的 .gwlic 校验并保存。看到授权通过后，可直接在下方安装 0.8.1，无需单独重启。';
    $('license-status').textContent = s?.message || '授权状态暂不可用';
    $('license-device').textContent = s?.device_code || '当前未取得设备码';
    $('license-owner').textContent = [s?.customer, s?.license_id].filter(Boolean).join(' / ') || '—';
    $('license-banner').hidden = !s?.required || s?.valid;
    $('license-banner').textContent = s?.message || '';
    $('license-export').disabled = !s?.required || !s?.device_code;
    $('license-import').disabled = !s?.required || !s?.device_code;
}
bind('license-export', async () => { const r = await api('license/request'); const blob = new Blob([JSON.stringify(r, null, 2)], { type: 'application/json' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); document.body.append(a); a.download = 'GT6757X-' + r.device_code + '.gwreq'; a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 1000); });
$('license-form').addEventListener('submit', async (e) => { e.preventDefault(); const b = $('license-import'); b.disabled = true; try {
    const f = input('license-file').files?.[0];
    if (!f || f.size > 8192)
        throw Error('请选择不超过8KiB的厂家授权文件');
    if (!input('license-confirm').checked)
        throw Error('请确认授权文件来源');
    const result = await api('license/import', 'POST', { license: JSON.parse(await f.text()), confirmed: true });
    input('license-confirm').checked = false;
    message(result.message || '已保存，人工重启网关程序后生效');
    await refresh();
}
catch (err) {
    message(String(err));
}
finally {
    b.disabled = false;
} });
let tcpSaved, tcpAuthorized = true, tcpCopy = '';
function renderTCPAccess() {
    if (!tcpSaved)
        return;
    const view = describeTCP(tcpSaved, networkState, tcpAuthorized);
    tcpCopy = view.copy;
    $('tcp-access-interface').textContent = view.selected;
    $('tcp-access-state').textContent = [view.state, view.warning].filter(Boolean).join('；');
    const list = $('tcp-access-addresses');
    list.replaceChildren();
    for (const row of view.rows) {
        const p = document.createElement('p');
        p.textContent = row.label + '：' + row.address;
        list.append(p);
    }
    $('tcp-access-copy').disabled = !view.copy;
}
bind('tcp-access-copy', async () => {
    if (!tcpCopy)
        throw Error('当前没有可复制的访问地址');
    try {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(tcpCopy);
            message('Modbus TCP访问地址已复制');
            return;
        }
    }
    catch { }
    const box = $('tcp-copy-text');
    box.value = tcpCopy;
    $('tcp-copy-fallback').hidden = false;
    box.focus();
    box.select();
    box.setSelectionRange(0, box.value.length);
    let copied = false;
    try {
        copied = document.execCommand('copy');
    }
    catch { }
    if (copied) {
        $('tcp-copy-fallback').hidden = true;
        message('Modbus TCP访问地址已复制');
    }
    else
        message('地址已选中，请长按复制或按Ctrl+C');
});
bind('tcp-copy-close', () => { $('tcp-copy-fallback').hidden = true; });
void api('session').then(async (s) => { csrf = s.csrf; await enter(); }).catch(() => { });
setInterval(() => void (upgrading ? pollUpgrade() : refresh()), 2000);
function renderIndicators(status, simulate, maintenance) {
    const view = describeIndicators(status, simulate, maintenance);
    const grid = $('sys-lamps');
    grid.replaceChildren();
    for (const lamp of view.lamps) {
        const card = document.createElement('section');
        const title = document.createElement('h3');
        title.textContent = lamp.title;
        const state = document.createElement('p');
        state.className = 'indicator-state';
        state.textContent = lamp.state + ' · ' + lamp.period;
        const explanation = document.createElement('p');
        explanation.textContent = lamp.explanation;
        card.append(title, state, explanation);
        grid.append(card);
    }
    $('sys-driver-title').textContent = view.driver.title;
    $('sys-driver-message').textContent = view.driver.message;
    const list = $('sys-reasons');
    list.replaceChildren();
    const active = status.modes[2] !== 0;
    $('sys-reasons-title').textContent = active ? '当前告警原因（' + view.reasons.length + '项）' : '当前告警原因';
    if (active && view.reasons.length) {
        for (const reason of view.reasons) {
            const row = document.createElement('li');
            row.textContent = (reason.severity === 'critical' ? '严重告警 · ' : '一般告警 · ') + reason.message;
            list.append(row);
        }
    }
    else {
        const row = document.createElement('li');
        row.textContent = active ? '正在读取原因，请刷新查看运行事件。' : '当前没有触发 SYS3 的告警。';
        list.append(row);
    }
}
