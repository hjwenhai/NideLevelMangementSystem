// Status uses the core's monotonic sample age, never the computer/tablet clock.
export function describeAnalog(v) {
    const group = v.groups[5];
    const sampled = Boolean(group?.time_ms && group.sequence > 0 && v.raw.length >= 31);
    const enabled = Boolean(v.config.enabled && v.config.analog);
    const age = sampled ? group.age_ms : null;
    const period = v.detail_period_ms > 0 ? v.detail_period_ms : 30000;
    const windowMS = period * 3;
    const expired = sampled && (age === null || !Number.isFinite(age) || age > windowMS);
    const valid = enabled && sampled && !group.unsupported && !expired && !v.collection_paused;
    let state = '已采集';
    if (!v.config.enabled)
        state = '仪表已停用';
    else if (!v.config.analog)
        state = '未启用模拟量采集';
    else if (group?.unsupported)
        state = sampled ? '寄存器不支持（以下为旧值）' : '寄存器不支持，暂无数据';
    else if (v.collection_paused)
        state = sampled ? '维护模式，采集暂停（以下为旧值）' : '维护模式，采集暂停，暂无数据';
    else if (!sampled)
        state = group?.error ? '采集失败，尚无成功数据' : '等待首次采集';
    else if (expired)
        state = '数据已过期（以下为旧值）';
    else if (group.error)
        state = '最近采集失败（显示窗口内上次成功值）';
    const show = enabled && sampled;
    const base = 3000 + 144 * (v.config.slot - 1);
    const points = [27, 28, 29, 30].map(address => {
        const raw = show ? v.raw[address] : null;
        const signed = raw !== null && address >= 29 && raw >= 32768 ? raw - 65536 : raw;
        return { 寄存器: 'IR' + address, 网关地址: base + address, 原始值: raw, 数值: signed === null ? null : signed / (address < 29 ? 1000 : 100), 单位: address < 29 ? 'mA' : '%', 有效: valid };
    });
    return { 采集已启用: Boolean(v.config.analog), 采集状态: state, 显示为旧值: show && (!valid || Boolean(group.error)), 目标周期毫秒: period, 参考有效窗口毫秒: windowMS, 数据年龄毫秒: age, 最近成功时间毫秒: group?.time_ms || 0, 采集时间已确认: Boolean(sampled && group.time_trusted), 成功次数: group?.sequence || 0, 累计失败次数: group?.failures || 0, 最近错误: group?.error || '无', 数据: points };
}
