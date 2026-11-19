"""Create/read an isolated schema-16 compatibility sample for installer QA."""
import json
import sqlite3
import sys
import time
from pathlib import Path

action, database, evidence = sys.argv[1:]
db = Path(database).resolve()
if 'r24-feedback_20261118' not in db.parts or 'installer' not in db.parts:
    raise ValueError('Only isolated installer fixtures are permitted')
with sqlite3.connect(db) as connection:
    if action == 'seed':
        revision = connection.execute('SELECT Revision FROM configuration').fetchone()[0]
        now = int(time.time() * 1000) - 1000
        connection.execute('INSERT INTO history_tanks(TankId) VALUES(?)', ('qa-preserved-tank',))
        tank = connection.execute('SELECT Id FROM history_tanks WHERE TankId=?', ('qa-preserved-tank',)).fetchone()[0]
        connection.execute('INSERT INTO history_sessions(SessionId) VALUES(?)', ('qa-preserved-session',))
        session = connection.execute('SELECT Id FROM history_sessions WHERE SessionId=?', ('qa-preserved-session',)).fetchone()[0]
        connection.execute('INSERT INTO history_samples(TankKey,AtUtcMs,ConfigurationRevision,CalculationRevision,SessionKey,SourceTimeConfirmed,QualityBits,RawQuality,RawValidity,DiagnosticCode,LevelMicrometres) VALUES(?,?,?,?,?,?,?,?,?,?,?)',
                           (tank, now, revision, 0, session, 0, 0, 0, 1, 'installer-preservation', 1234500))
        connection.execute('INSERT INTO operation_events(AtUtcMs,Category,ActorId,ActorName,Content,Result,Details) VALUES(?,?,?,?,?,?,?)',
                           (now, 'system', 'qa', '安装验证', 'installer-preservation', 'ok', '{}'))
        connection.execute('DROP TABLE IF EXISTS deleted_capacity_tables')
        connection.execute('PRAGMA user_version=16')
    values = {
        'schema': connection.execute('PRAGMA user_version').fetchone()[0],
        'users': connection.execute('SELECT Id,Login,PasswordHash,Enabled FROM users ORDER BY Id').fetchall(),
        'configuration': connection.execute('SELECT Revision,Payload FROM configuration').fetchall(),
        'history': connection.execute("SELECT TankKey,AtUtcMs,LevelMicrometres FROM history_samples WHERE DiagnosticCode='installer-preservation'").fetchall(),
        'events': connection.execute("SELECT AtUtcMs,Content,Result FROM operation_events WHERE Content='installer-preservation'").fetchall(),
    }
Path(evidence).write_text(json.dumps(values, ensure_ascii=False, indent=2), encoding='utf-8')
