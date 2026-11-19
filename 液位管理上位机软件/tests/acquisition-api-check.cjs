const fs = require('fs');
const path = require('path');
const net = require('net');
const assert = require('assert/strict');
const { spawn } = require('child_process');
const { request } = require('C:/Users/admin/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const root = path.resolve(__dirname, '..');
const output = path.join(root, 'output/p2-verification', 'isolated-' + Date.now());
fs.mkdirSync(output, { recursive: true });
const dotnet = path.join(root, '.build-tools/dotnet/dotnet.exe');
const dll = process.env.LEVEL_HOST_DLL || path.join(root, 'output/engineering/LevelManager.Host.dll');
const sockets = new Set(); let errorGroup = false, requests = 0, writes = 0;
function put32(words, at, value) { words[at] = value >>> 16; words[at + 1] = value & 65535; }
function image(start, count) {
  const header = new Array(30).fill(0); header[2] = 3; header[3] = 64; header[9] = 91; header[27] = 16; header[28] = 3; header[29] = 12;
  if (start === 0) return header.slice(0, count);
  if (start === 6) return header.slice(6, 6 + count);
  if (start >= 1000 && start < 2536) {
    const slot = Math.floor((start - 1000) / 24) + 1;
    const summary = new Array(24).fill(0); summary[0] = 3; summary[1] = 127; summary[3] = 42;
    put32(summary, 4, slot * 10000); summary[6] = 2800; summary[7] = 8320; summary[9] = 1; summary[21] = 1;
    return summary.slice((start - 1000) % 24, (start - 1000) % 24 + count);
  }
  const slot = Math.floor((start - 3000) / 144) + 1, offset = (start - 3000) % 144;
  const detail = new Array(144).fill(0); put32(detail, 0, slot * 10000);
  for (let i = 0; i < 16; i++) detail[6 + i] = i === 0 ? 0 : 2500 + i * 100;
  detail[23] = 65535; detail[26] = 2800; detail[50] = 8320; detail[51] = 1; detail[55] = 1; detail[57] = 42;
  for (let i = 0; i < 4; i++) detail[58 + i] = 65535;
  for (let i = 0; i < 6; i++) { detail[64 + i * 12] = i === 0 && errorGroup ? 3 : 9; detail[67 + i * 12] = 21; }
  return detail.slice(offset, offset + count);
}
const gateway = net.createServer(socket => {
  sockets.add(socket); socket.on('close', () => sockets.delete(socket)); socket.on('error', () => {});
  let bytes = Buffer.alloc(0);
  socket.on('data', data => {
    bytes = Buffer.concat([bytes, data]);
    while (bytes.length >= 7 && bytes.length >= bytes.readUInt16BE(4) + 6) {
      const length = bytes.readUInt16BE(4) + 6, frame = bytes.subarray(0, length); bytes = bytes.subarray(length);
      requests++; const fc = frame[7];
      if (fc !== 4 && fc !== 3) { writes++; socket.destroy(); continue; }
      const words = image(frame.readUInt16BE(8), frame.readUInt16BE(10));
      const reply = Buffer.alloc(9 + words.length * 2); frame.copy(reply, 0, 0, 7); reply.writeUInt16BE(3 + words.length * 2, 4);
      reply[7] = fc; reply[8] = words.length * 2; words.forEach((value, i) => reply.writeUInt16BE(value, 9 + i * 2)); socket.write(reply);
    }
  });
});
let child, api, csrf, base;
async function until(fn, timeout = 12000) { const end = Date.now() + timeout; while (Date.now() < end) { const value = await fn(); if (value) return value; await new Promise(r => setTimeout(r, 100)); } throw Error('verification timed out'); }
async function call(url, method = 'GET', body) {
  const response = await api.fetch(base + url, { method, data: body, headers: method === 'GET' ? {} : { 'X-CSRF-TOKEN': csrf } });
  let value; try { value = await response.json(); } catch { value = await response.text(); }
  return { status: response.status(), value };
}
(async () => {
  await new Promise(resolve => gateway.listen(0, '127.0.0.1', resolve));
  const gatewayPort = gateway.address().port;
  const probe = net.createServer(); await new Promise(resolve => probe.listen(0, '127.0.0.1', resolve)); const port = probe.address().port; await new Promise(resolve => probe.close(resolve));
  base = 'http://127.0.0.1:' + port; api = await request.newContext();
  const log = fs.openSync(path.join(output, 'host.log'), 'w');
  child = spawn(dotnet, [dll, '--data-root', path.join(output, 'data'), '--urls', base], { windowsHide: true, stdio: ['ignore', log, log], env: { ...process.env, DOTNET_ROOT: path.dirname(dotnet) } });
  await until(async () => { try { return (await call('/api/health')).status === 200; } catch { return false; } });
  assert.equal((await call('/api/acquisition')).status, 401);
  csrf = (await call('/api/session')).value.csrfToken;
  assert.equal((await call('/api/auth/setup', 'POST', { login: 'phase2', password: 'Isolated-Only-2026!', displayName: '采集测试管理员' })).status, 200);
  assert.equal((await call('/api/auth/login', 'POST', { login: 'phase2', password: 'Isolated-Only-2026!' })).status, 200);
  csrf = (await call('/api/session')).value.csrfToken;await require('./support/license.cjs').importTestLicense(api,base,csrf);
  let config = (await call('/api/config')).value;
  const channelId = crypto.randomUUID(), tankId = crypto.randomUUID();
  config.channels = [{ id: channelId, name: '本机隔离网关', protocol: 'GatewayTcp', portName: '', baudRate: 38400, dataBits: 8, parity: 'Even', stopBits: 1, host: '127.0.0.1', tcpPort: gatewayPort, unitId: 1, pollIntervalMs: 500, readTimeoutMs: 300, readRetries: 0, enabled: true }];
  config.tanks = [{ id: tankId, code: 'API-T01', instrumentTag: '', medium: '测试', zone: '隔离', channelId, address: 1, shape: 'Vertical', heightMm: 12000, temperatureMode: 'Ats', atsPointCount: 3, enabled: true, hidden: true }];
  config.historyIntervalSeconds = 1; config.readingExpirySeconds = 3;
  const save = await call('/api/config', 'PUT', config); assert.equal(save.status, 200); config = save.value;
  assert.equal((await call('/api/acquisition')).value.running, false); assert.equal(requests, 0, 'saving configuration must not connect');
  const begin = await call('/api/acquisition/start', 'POST', {}); assert.equal(begin.status, 200);
  let live = await until(async () => { const v = (await call('/api/acquisition')).value; return v.readings[0]?.levelMm.value === 1000 ? v : false; });
  assert.equal(live.readings[0].instantTemperatureC.value, 28); assert.equal(live.readings[0].atsPoints[0].temperatureC.value, 0);
  assert(Math.abs(live.readings[0].averageTemperatureC.value - 17.6666666667) < 0.0001); assert(live.running);
  const start = new Date(Date.now() - 120000).toISOString(), end = new Date(Date.now() + 120000).toISOString();
  let archived = await until(async () => { const v = await call('/api/history?start=' + encodeURIComponent(start) + '&end=' + encodeURIComponent(end) + '&tank=' + tankId); assert.equal(v.status, 200); return v.value.rows.some(r => r.levelMm.value === 1000) ? v.value : false; });
  assert(archived.rows.every(r => r.configurationRevision === config.revision));
  errorGroup = true;
  await until(async () => (await call('/api/acquisition')).value.readings[0].levelMm.quality === 'Fault');
  const fault = (await call('/api/acquisition')).value.readings[0]; assert.equal(fault.levelMm.value, null); assert.equal(fault.instantTemperatureC.value, 28);
  errorGroup = false;
  config.channels = config.channels.map(({ isGateway, physicalKey, ...c }) => c); config.tanks[0].address = 2;
  const oldSession = live.sessionId; const changed = await call('/api/config', 'PUT', config); assert.equal(changed.status, 200); config = changed.value;
  const afterBinding = (await call('/api/acquisition')).value; assert.notEqual(afterBinding.sessionId, oldSession); assert.notEqual(afterBinding.readings[0].levelMm.value, 1000);
  live = await until(async () => { const v = (await call('/api/acquisition')).value; return v.readings[0]?.levelMm.value === 2000 ? v : false; });
  assert.equal(live.configurationRevision, config.revision);
  const stop = await call('/api/acquisition/stop', 'POST', {}); assert.equal(stop.status, 200); assert.equal(stop.value.running, false); assert.equal(stop.value.readings[0].levelMm.value, null);
  assert.equal(writes, 0); assert(requests > 0);
  assert.equal((await call('/api/history?start=bad&end=bad')).status, 400);
  const result = { passed: true, groups: ['unauthenticated rejection', 'save does not acquire', 'explicit live start', 'IR26/ATS/zero point', 'hidden tanks still archived', 'partial group failure', 'binding clears old session/value', 'explicit stop invalidates live values', 'zero instrument writes', 'invalid history query rejection'], requests, writes, currentRevision: config.revision };
  fs.writeFileSync(path.join(root, 'output/p2-verification/api-results.json'), JSON.stringify(result, null, 2)); console.log('PASS:', result.groups.length, 'acquisition/history API groups; no instrument writes');
})().catch(e => { console.error(e); process.exitCode = 1; }).finally(async () => {
  if (child && child.exitCode === null) child.kill();
  if (api) await api.dispose(); for (const socket of sockets) socket.destroy(); gateway.close();
});
