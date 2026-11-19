"""Exercise both gateways through the packaged Host API with an isolated SQLite database."""
import copy
import http.cookiejar
import json
import socket
import sqlite3
import subprocess
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from urllib import error, request

root = Path(__file__).resolve().parents[1]
out = root / 'output/independent-channel-qa' / uuid.uuid4().hex
out.mkdir(parents=True)
data = out / 'data'
host_exe = Path(sys.argv[1]).resolve()
sim_exe = root / 'output/GT6757X本机模拟器_r22_20261107_V1.0.3/app/LevelManager.GatewaySimulator.exe'
children = []
simulators = []
uid = lambda: str(uuid.uuid4())


def wait(check):
    end = time.monotonic() + 35
    while time.monotonic() < end:
        try:
            value = check()
            if value:
                return value
        except (ConnectionError, error.URLError, json.JSONDecodeError):
            pass
        time.sleep(.15)
    raise AssertionError('Timed out: ' + str(check))


def start(exe, args, log):
    with (out / log).open('wb') as stream:
        proc = subprocess.Popen([str(exe), *map(str, args)], stdout=stream, stderr=stream,
                                creationflags=subprocess.CREATE_NO_WINDOW)
    children.append(proc)
    return proc


def gateway(count):
    ready, stop = out / f'{count}.json', out / f'{count}.stop'
    p = start(sim_exe, ['--headless', '--port', 0, '--count', count, '--ready', ready, '--stop-file', stop], f'{count}.log')
    simulators.append((p, ready, stop))
    info = wait(lambda: ready.exists() and json.loads(ready.read_bytes()))
    assert info['loopbackOnly'] and info['serialPortsOpened'] == 0
    return dict(id=uid(), name=f'QA-{count}', protocol='GatewayTcp', host='127.0.0.1',
                tcpPort=info['Port'], unitId=1, enabled=True, pollIntervalMs=500)


def tank(channel, slot):
    return dict(id=uid(), code=f"{channel['name']}-{slot}", channelId=channel['id'], address=slot, enabled=True)


def seed_history(config, session):
    # The synthetic unknown row predates the newest 1,000 rows. Never open an existing field DB.
    target = dict(tank=config['tanks'][0], channel=config['channels'][0],
                  configurationRevision=config['revision'], acquisitionSessionId=session)
    base_stamp = int(datetime(2026, 10, 30, tzinfo=timezone.utc).timestamp())
    with sqlite3.connect(data / 'level-manager.db') as db:
        for i in range(1002):
            state = 'ReadbackConfirmed' if i else 'Unknown'
            stamp = base_stamp + i
            row = dict(id=uid(), target=target, command='MeasureI', cycle='Normal', state=state,
                       actorId='qa', actorName='qa', createdAtUtc=datetime.fromtimestamp(stamp, timezone.utc).isoformat(),
                       result='QA fixture')
            db.execute('INSERT INTO control_records(Id,TankId,Cycle,State,AtUtcMs,ConfigurationRevision,Payload) VALUES(?,?,?,?,?,?,?)',
                       (row['id'], target['tank']['id'], 'Normal', state, stamp*1000, config['revision'], json.dumps(row)))
            if i == 0:
                oldest = row['id']
    return oldest


def run():
    field, demo = gateway(1), gateway(11)
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        url = f'http://127.0.0.1:{sock.getsockname()[1]}'
    start(host_exe, ['--data-root', data, '--urls', url], 'host.log')
    opener = request.build_opener(request.ProxyHandler({}), request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    token = ''

    def api(path, method='GET', body=None, expected=200):
        req = request.Request(url+path, method=method, headers={'Content-Type':'application/json','X-CSRF-TOKEN':token},
                              data=json.dumps(body).encode() if body is not None else None)
        try:
            reply = opener.open(req, timeout=5)
        except error.HTTPError as exc:
            reply = exc
        with reply:
            content = reply.read()
            assert reply.status == expected, (path, reply.status, content.decode())
            return json.loads(content)

    assert wait(lambda: api('/api/health'))['independentChannelAdd']
    token = api('/api/session')['csrfToken']
    account = dict(login='qauser', displayName='QA', password=uid())
    api('/api/auth/setup','POST',account)
    api('/api/auth/login','POST',{'login':account['login'],'password':account['password']})
    token = api('/api/session')['csrfToken']
    cfg = api('/api/config')
    cfg.update(channels=[field], tanks=[tank(field,1)], historyIntervalSeconds=1)
    cfg = api('/api/config','PUT',cfg)
    api('/api/acquisition/start','POST',{})

    def ready_view(count):
        view = api('/api/acquisition')
        return view if len(view['readings']) == count and all(r['levelMm']['isValid'] for r in view['readings']) else None

    initial = wait(lambda: ready_view(1))
    session = initial['sessionId']
    oldest = seed_history(cfg, session)
    original_id = cfg['tanks'][0]['id']
    lookup = '/api/controls?tank=' + original_id
    assert not any(row['id']==oldest for row in api(lookup))
    assert api(lookup+'&unresolved=true')[0]['id']==oldest
    cfg['channels'].append(demo)
    staged = '--combined' not in sys.argv
    if staged:
        cfg = api('/api/config','PUT',cfg)
        assert len(api('/api/acquisition')['channels'])==1
        cfg['tanks'].append(tank(demo,1))
        cfg = api('/api/config','PUT',cfg)
        assert wait(lambda: ready_view(2))['sessionId']==session
    cfg['tanks'].extend(tank(demo,i) for i in range(2 if staged else 1,12))
    cfg = api('/api/config','PUT',cfg)
    view = wait(lambda: ready_view(12))
    base_cycles = initial['channels'][0]['completedCycles']
    def progressed():
        current = ready_view(12)
        return current and current['sessionId']==session and any(
            c['channelId']==field['id'] and c['completedCycles']>base_cycles+1 for c in current['channels'])
    wait(progressed)
    assert view['sessionId']==session and len(view['channels'])==2
    for change,status in [('move',409),('port',409),('remove',409),('sameChannel',409),('duplicate',400)]:
        bad = copy.deepcopy(cfg)
        if change=='move': bad['tanks'][0]['address']=2
        if change=='port': bad['channels'][0]['tcpPort']=1
        if change=='remove': bad['tanks'].pop(0)
        if change=='sameChannel': bad['tanks'].append(tank(field,2))
        if change=='duplicate': bad['channels'][1]['tcpPort']=field['tcpPort']
        api('/api/config','PUT',bad,status)
    assert api('/api/config')['revision']==cfg['revision']
    assert [(r['id'],r['state']) for r in api(lookup+'&unresolved=true')]==[(oldest,'Unknown')]
    assert api('/api/acquisition')['sessionId']==session
    return dict(passed=True,staged=staged,validTanks=12,originalSessionPreserved=True,oldRecordOutsideRecent1000Found=True,
                unrelatedRecordUnchanged=True,protectedEditsRejected=5,originalCyclesContinued=True)


try:
    result = run()
    for proc, ready, stop in simulators:
        stop.touch()
        assert proc.wait(10)==0
        stats=json.loads(Path(str(ready)+'.stopped.json').read_bytes())
        assert stats['Requests']>0 and stats['Writes']==0
    result['gatewayWrites']=0
    (out/'result.json').write_text(json.dumps(result,indent=2))
    print(out, result)
finally:
    for child in reversed(children):
        if child.poll() is None:
            child.terminate()
            child.wait(10)
