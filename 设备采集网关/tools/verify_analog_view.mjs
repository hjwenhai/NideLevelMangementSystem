import {readFile,writeFile,mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const source=await readFile(path.join(root,'src/internal/web/static/analog.js'),'utf8');
const {describeAnalog}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const checks=[];
function test(name,fn){fn();checks.push({name,passed:true});}
function fixture(){const v={config:{slot:1,enabled:true,analog:true},raw:Array(53).fill(0),groups:Array.from({length:6},()=>({time_ms:1000,time_trusted:true,sequence:1,failures:0,unsupported:false,age_ms:100})),detail_period_ms:30000,collection_paused:false};v.raw.splice(27,4,12345,4000,65036,65535);return v;}
test('four_values_unsigned_mA_signed_percent',()=>{const a=describeAnalog(fixture());assert.deepEqual(a.数据.map(p=>p.数值),[12.345,4,-5,-.01]);assert.deepEqual(a.数据.map(p=>p.单位),['mA','mA','%','%']);assert.deepEqual(a.数据.map(p=>p.网关地址),[3027,3028,3029,3030]);assert(a.数据.every(p=>p.有效));});
test('disabled_no_fake_zero_or_old_values',()=>{const v=fixture();v.config.analog=false;const a=describeAnalog(v);assert.match(a.采集状态,/未启用/);assert(a.数据.every(p=>p.数值===null&&p.原始值===null&&!p.有效));});
test('enabled_waiting_first_sample',()=>{const v=fixture();v.groups[5].time_ms=0;assert.match(describeAnalog(v).采集状态,/等待/);assert(describeAnalog(v).数据.every(p=>p.数值===null));});
test('failed_before_first_success',()=>{const v=fixture();v.groups[5].time_ms=0;v.groups[5].error='timeout';assert.match(describeAnalog(v).采集状态,/采集失败/);});
test('unsupported_without_sample',()=>{const v=fixture();v.groups[5].time_ms=0;v.groups[5].unsupported=true;assert.match(describeAnalog(v).采集状态,/不支持/);assert(describeAnalog(v).数据.every(p=>p.数值===null));});
test('failed_with_window_old_value',()=>{const v=fixture();v.groups[5].error='CRC';const a=describeAnalog(v);assert(a.显示为旧值);assert(a.数据.every(p=>p.有效));assert.match(a.采集状态,/上次成功值/);});
test('window_boundary_and_expired',()=>{const v=fixture();v.groups[5].age_ms=90000;assert(describeAnalog(v).数据.every(p=>p.有效));v.groups[5].age_ms=90001;const a=describeAnalog(v);assert.match(a.采集状态,/过期/);assert(a.显示为旧值&&a.数据.every(p=>!p.有效));});
test('committed_detail_period_used',()=>{const v=fixture();v.detail_period_ms=1000;v.groups[5].age_ms=3001;assert.match(describeAnalog(v).采集状态,/过期/);});
test('untrusted_time_does_not_invalidate_monotonic_sample',()=>{const v=fixture();v.groups[5].time_trusted=false;const a=describeAnalog(v);assert.equal(a.采集时间已确认,false);assert(a.数据.every(p=>p.有效));});
test('instrument_disabled',()=>{const v=fixture();v.config.enabled=false;assert.match(describeAnalog(v).采集状态,/停用/);assert(describeAnalog(v).数据.every(p=>p.数值===null));});
test('maintenance_paused_marks_old',()=>{const v=fixture();v.collection_paused=true;const a=describeAnalog(v);assert.match(a.采集状态,/暂停/);assert(a.显示为旧值&&a.数据.every(p=>!p.有效));});
test('slot32_north_address',()=>{const v=fixture();v.config.slot=32;assert.deepEqual(describeAnalog(v).数据.map(p=>p.网关地址),[7491,7492,7493,7494]);});
test('legitimate_zero_is_sample',()=>{const v=fixture();v.raw.fill(0);assert(describeAnalog(v).数据.every(p=>p.数值===0&&p.有效));});
await mkdir(path.join(root,'tests'),{recursive:true});
await writeFile(path.join(root,'tests/analog-view-verification-20261018.json'),JSON.stringify({date:'2026-10-18',software:'0.3.6',passed:true,checks},null,2)+'\n');
console.log(JSON.stringify({passed:true,checks:checks.length}));
