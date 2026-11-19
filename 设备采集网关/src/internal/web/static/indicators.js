export function describeIndicators(s, simulate, maintenance) {
    const modeNames = ['熄灭', '常亮', '慢闪', '快闪', '心跳'];
    const periods = ['持续熄灭', '持续点亮', '亮1秒、灭1秒', '亮0.25秒、灭0.25秒', '亮0.5秒、灭0.5秒'];
    const collection = s.enabled_meters === undefined ? '' : `已启用 ${s.enabled_meters} 台，快速采集正常 ${s.good_meters ?? 0} 台。`;
    const explanations = [
        s.modes[0] === 4 ? '核心程序调度健康检查正常。' : '核心程序调度健康检查异常，请查看运行事件。',
        s.modes[1] === 0 ? (maintenance ? '维护模式，采集已暂停。' : '当前未开展采集，请检查仪表启用及设备授权状态。') :
            s.modes[1] === 1 ? '所有已启用液位计的快速数据有效，且采集质量符合正常条件。' :
                s.modes[1] === 2 ? '部分液位计快速采集正常，其余仪表存在数据缺失、过期、采集失败或故障。' :
                    '当前没有液位计达到完整快速采集正常条件。',
        s.modes[2] === 0 ? '当前没有触发 SYS3 的告警。' :
            s.modes[2] === 1 ? '存在严重告警，请查看下方具体原因及控制结果。' :
                '存在一般告警，请查看下方原因。时间或参数快照异常也可能使此灯慢闪。'
    ];
    const lamps = s.modes.map((mode, i) => ({
        title: `SYS${i + 1} · ${['程序运行', '液位计采集', '告警'][i]}`,
        state: modeNames[mode] || '未知灯态',
        period: periods[mode] || '',
        explanation: (explanations[i] || '') + (i === 1 ? collection : '')
    }));
    const driver = simulate ?
        { title: '实体灯控制 · 模拟运行', message: '网页显示软件计算的灯态，当前使用模拟仪表。' } :
        s.driver_error ?
            { title: '实体灯控制 · 驱动异常', message: s.driver_error + '。请检查设备驱动和运行事件。' } :
            s.driver_enabled ?
                { title: '实体灯控制 · 已启用', message: '程序已打开 SYS1～SYS3 的硬件驱动，并按上述灯态持续控制实体灯。此处根据驱动运行状态显示，灯具实际亮灭需现场观察。' } :
                { title: '实体灯控制 · 未启用', message: '请检查设备部署时的 LEDS 配置和运行事件。网页仍显示软件计算的灯态。' };
    return { lamps, driver, reasons: s.reasons || [] };
}
