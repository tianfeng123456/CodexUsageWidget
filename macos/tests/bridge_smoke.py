"""Exercise the packaged macOS bridge over the same protocol as the GUI."""
import json, os, pathlib, queue, subprocess, sys, tempfile, threading, time, sqlite3
from datetime import datetime, timedelta, timezone

exe = pathlib.Path(sys.argv[1]).resolve()
now = datetime.now(timezone.utc)
reset = int((now + timedelta(days=2)).timestamp())
def line(value): return json.dumps(value) + '\n'
def event(n, used, offset=0):
    return line({'timestamp':(now+timedelta(seconds=offset)).isoformat(),'type':'event_msg','payload':{'type':'token_count','info':{'total_token_usage':{'input_tokens':n,'cached_input_tokens':n//2,'output_tokens':10,'reasoning_output_tokens':5}},'rate_limits':{'limit_id':'codex','plan_type':'pro','primary':{'used_percent':used,'window_minutes':10080,'resets_at':reset},'secondary':None}}})
with tempfile.TemporaryDirectory(prefix='codex-widget-test-') as root:
    home=pathlib.Path(root)/'home'; data=pathlib.Path(root)/'data'
    (home/'sessions').mkdir(parents=True); (home/'archived_sessions').mkdir()
    log=home/'sessions'/'rollout-smoke.jsonl'
    log.write_text(line({'timestamp':now.isoformat(),'type':'session_meta','payload':{'id':'smoke-root'}})+event(100,10))
    (home/'session_index.jsonl').write_text(line({'id':'smoke-root','thread_name':'迁移测试 / Migration test','updated_at':now.isoformat()}))
    catalog=sqlite3.connect(home/'state_5.sqlite')
    catalog.execute('CREATE TABLE threads(id TEXT, name TEXT, title TEXT)')
    catalog.execute('INSERT INTO threads VALUES(?,?,?)',('smoke-root','Recovered desktop title','Old prompt'))
    catalog.commit(); catalog.close()
    q=queue.Queue(); messages=[]
    def start():
        p=subprocess.Popen([str(exe)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,bufsize=1)
        def read():
            for s in p.stdout:
                q.put(json.loads(s))
        threading.Thread(target=read,daemon=True).start()
        return p
    p=start()
    def send(command,**kwargs):
        p.stdin.write(line({'command':command,**kwargs}));p.stdin.flush()
    def until(kind,timeout=20):
        deadline=time.monotonic()+timeout
        while time.monotonic()<deadline:
            e=q.get(timeout=max(.01,deadline-time.monotonic())); messages.append(e)
            assert e['type']!='error',e
            if e['type']==kind:return e['data']
        raise AssertionError('timeout '+kind)
    send('init',home=str(home),dataDirectory=str(data))
    assert until('ready')['home']==str(home)
    assert until('quota')['primary']['remainingPercent']==90
    s=until('snapshot'); assert s['summary']['total']['totalTokens']==110,s
    assert s['topTasks'][0]['title']=='Recovered desktop title'
    # Watcher tail read must update quota without updating the statistics index.
    with log.open('a') as f:f.write(event(200,20,1))
    assert until('quota')['primary']['remainingPercent']==80
    send('refresh',period=3)
    s=until('snapshot');assert s['summary']['total']['totalTokens']==210,s
    catalog=sqlite3.connect(home/'state_5.sqlite')
    catalog.execute("UPDATE threads SET name='Renamed desktop title'"); catalog.commit(); catalog.close()
    for period in [0,1,2,3]:
        send('refresh',period=period); s=until('snapshot'); assert s['period']==period
        assert s['topTasks'][0]['title']=='Renamed desktop title'
        assert s['summary']['total']['totalTokens']==210
        assert sum(t['usage']['totalTokens'] for t in s['topTasks'])+s['summary']['otherTasksTotal']['totalTokens']==210
    # Old concurrent snapshots do not count twice.
    with log.open('a') as f:f.write(event(150,15,2))
    send('weekly');days=until('weekly');s=until('snapshot')
    assert len(days)==7,days
    assert s['summary']['total']['totalTokens']==210
    assert abs(days[-1]['consumedPercentagePoints']-20)<.001,days
    send('rebuild');assert until('snapshot')['summary']['total']['totalTokens']==210
    send('quit');p.wait(timeout=10);assert p.returncode==0
    # Restart uses cache without full-history progress events.
    q=queue.Queue();messages=[];p=start()
    send('init',home=str(home),dataDirectory=str(data),period=2);s=until('snapshot')
    assert s['period']==2
    assert s['summary']['total']['totalTokens']==210
    assert not any(x['type']=='progress' for x in messages),messages
    send('quit');p.wait(timeout=10)
    print('PASS: init, quota, event watcher, 4 periods, high-water deduplication, weekly usage, rebuild, cached restart, clean shutdown')
