const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const { setTimeout: delay } = require('node:timers/promises');
const { chromium, request } = require(process.env.R24_PLAYWRIGHT_PATH || 'playwright');

const root = path.resolve(__dirname, '..');
const qa = path.join(root, 'output/r24-lan-tank-graphic_20261119');
const testDirectory = fs.mkdtempSync(path.join(qa, 'browser-'));
const server = path.join(testDirectory, 'server');
const webRoot = path.join(server, 'wwwroot');
const address = process.argv[2];
assert(net.isIPv4(address) && !address.startsWith('127.'), 'Provide a private test adapter IPv4 address.');
const gateway = require('./support/mock-gateway.cjs')();
const checks = [];
let host, browser, api, lanUrl, localUrl, csrf;

function copyTree(source, destination) {
  fs.mkdirSync(destination, { recursive: true });
  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    const from = path.join(source, entry.name);
    const to = path.join(destination, entry.name);
    if (entry.isDirectory()) copyTree(from, to);
    else fs.writeFileSync(to, fs.readFileSync(from));
  }
}

async function call(route, method = 'GET', data) {
  const response = await api.fetch(localUrl + route, { method, data, headers: method === 'GET' ? {} : { 'X-CSRF-TOKEN': csrf } });
  assert(response.ok(), `${route}: ${await response.text()}`);
  return response.json();
}

async function until(read, description) {
  for (let attempt = 0; attempt < 90; attempt++) {
    if (await read()) return;
    await delay(150);
  }
  throw new Error(`Timed out: ${description}`);
}

async function openPage(url) {
  const cookies = (await api.storageState()).cookies.map(cookie => ({ ...cookie, domain: new URL(url).hostname }));
  const context = await browser.newContext({ storageState: { cookies, origins: [] } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => {
    if (response.url().includes('/assets/') && response.status() >= 400) errors.push(`${response.status()} ${response.url()}`);
  });
  await page.goto(url);
  await page.locator('.overview-card').nth(2).waitFor();
  return { context, page, errors };
}

async function checkGraphics(page, count) {
  const graphics = page.locator('svg.tank-graphic');
  await graphics.nth(count - 1).waitFor({ state: 'visible' });
  const ids = await graphics.evaluateAll(tanks => tanks.flatMap(tank => {
    const defs = [...tank.querySelectorAll('defs [id]')].map(def => def.id);
    if (defs.length !== 3 || tank.getBoundingClientRect().width <= 0) throw new Error('Incomplete tank SVG');
    for (const shape of tank.querySelectorAll('[fill^="url("], [clip-path]')) {
      const reference = shape.getAttribute('clip-path') || shape.getAttribute('fill');
      if (!defs.includes(reference.slice(5, -1))) throw new Error('SVG reference points to a different tank');
    }
    return defs;
  }));
  assert.equal(ids.length, count * 3);
  assert.equal(new Set(ids).size, ids.length);
}

async function run() {
  copyTree(path.join(root, 'output/r24-feedback_20261118/payload/app/server'), server);
  copyTree(path.join(qa, 'before-wwwroot'), webRoot);
  const probe = net.createServer();
  await new Promise((resolve, reject) => probe.once('error', reject).listen(0, address, resolve));
  const port = probe.address().port;
  await new Promise(resolve => probe.close(resolve));
  lanUrl = `http://${address}:${port}`;
  localUrl = `http://127.0.0.1:${port}`;
  const log = fs.openSync(path.join(testDirectory, 'host.log'), 'w');
  host = spawn(path.join(server, 'LevelManager.Host.exe'), ['--data-root', path.join(testDirectory, 'data'), '--urls', `${lanUrl};${localUrl}`], { windowsHide: true, stdio: ['ignore', log, log] });
  fs.closeSync(log);
  api = await request.newContext({ timeout: 2500 });
  await until(async () => {
    try { return (await api.get(`${lanUrl}/api/health`)).ok(); } catch { return false; }
  }, 'start isolated r24 host');
  const password = randomUUID();
  csrf = (await call('/api/session')).csrfToken;
  await call('/api/auth/setup', 'POST', { login: 'lanqa', displayName: '浏览器测试管理员', password });
  await call('/api/auth/login', 'POST', { login: 'lanqa', password });
  csrf = (await call('/api/session')).csrfToken;
  await require('./support/license.cjs').importTestLicense(api, localUrl, csrf, { modules: [] });
  const config = await call('/api/config');
  const channel = randomUUID();
  config.channels = [{ id: channel, name: '隔离回环网关', protocol: 'GatewayTcp', host: '127.0.0.1', tcpPort: await gateway.listen(), unitId: 1, portName: '', pollIntervalMs: 500, readTimeoutMs: 400, readRetries: 0, enabled: true }];
  config.tanks = ['Vertical', 'Sphere', 'FloatingRoof'].map((shape, i) => ({ id: randomUUID(), code: `T-10${i + 1}`, instrumentTag: `LT-10${i + 1}`, name: `示例储罐${i + 1}`, medium: '柴油', channelId: channel, address: i + 1, shape, heightMm: 16000, temperatureMode: 'None', enabled: true }));
  await call('/api/config', 'PUT', config);
  await call('/api/acquisition/start', 'POST', {});
  await until(async () => {
    const { readings } = await call('/api/acquisition');
    return readings.length === 3 && readings.every(reading => reading.levelMm.isValid);
  }, 'receive three simulated level readings');

  browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--no-proxy-server'] });
  const before = await openPage(lanUrl);
  assert.deepEqual(await before.page.evaluate(() => [isSecureContext, typeof crypto.randomUUID]), [false, 'undefined']);
  await before.page.waitForFunction(() => document.querySelector('.overview-level')?.textContent.trim() !== '—');
  assert.equal(await before.page.locator('svg.tank-graphic').count(), 0);
  checks.push('旧版使用局域网 HTTP 时读数正常、三张罐图均缺失');
  await before.context.close();

  copyTree(path.join(root, 'src/LevelManager.Host/wwwroot'), webRoot);
  const after = await openPage(lanUrl);
  assert.deepEqual(await after.page.evaluate(() => [isSecureContext, typeof crypto.randomUUID]), [false, 'undefined']);
  await checkGraphics(after.page, 3);
  await after.page.locator('.tank-grid').screenshot({ path: path.join(testDirectory, 'lan-overview.png') });
  checks.push('局域网 HTTP 总览绘制立式罐、球罐和浮顶罐，渐变与裁剪引用各自独立');
  await after.page.locator('.overview-card').first().getByRole('button', { name: '详情 →' }).click();
  await checkGraphics(after.page, 1);
  assert.deepEqual(after.errors, []);
  checks.push('局域网 HTTP 单罐详情罐图可见，无资源加载错误');
  await after.context.close();

  const local = await openPage(localUrl);
  await checkGraphics(local.page, 3);
  assert.deepEqual(local.errors, []);
  checks.push('本机访问的三种罐图仍正常');
  await local.context.close();
}

run().catch(error => {
  console.error(error);
  process.exitCode = 1;
}).finally(async () => {
  if (browser) await browser.close();
  if (api) await api.dispose();
  if (host && host.exitCode === null) host.kill();
  await gateway.close();
  const result = { passed: !process.exitCode, checks, fixture: testDirectory, realDevicesAccessed: false };
  fs.writeFileSync(path.join(qa, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
});
