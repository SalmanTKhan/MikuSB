"""Build first. Starts the local host for ten seconds and checks local listeners."""
import json
import os
import socket
import ssl
import subprocess
import time
import urllib.request
from pathlib import Path

root = Path(__file__).resolve().parents[1]
config = json.loads((root / 'Config/Config.json').read_text(encoding='utf-8-sig'))
ports = [config['GameServer']['Port'], config['HttpServer']['Port'], config['Proxy']['Port']]
log = root / 'docs/host-smoke.local.log'
env = dict(os.environ, MIKUSB_OFFLINE='1')
# Do not start a second host over somebody else's running server.
for port in ports:
    with socket.socket() as s:
        if s.connect_ex(('127.0.0.1', port)) == 0:
            raise RuntimeError(f'Port {port} is already occupied')
with log.open('w', encoding='utf-8') as output:
    p = subprocess.Popen(['dotnet', str(root / 'MikuSB/bin/Debug/net10.0/MikuSB.dll'), '--smoke-test'],
                         cwd=root, env=env, stdout=output, stderr=subprocess.STDOUT)
    try:
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            text = log.read_text(encoding='utf-8', errors='replace')
            if 'Yggdrasil listening' in text:
                break
            if p.poll() is not None:
                raise RuntimeError(f'Host exited {p.returncode}; see {log}')
            time.sleep(0.1)
        else:
            raise TimeoutError('Host startup timed out')
        for port in ports:
            with socket.create_connection(('127.0.0.1', port), timeout=2):
                pass
        # Test only the local self-signed SDK endpoint, without changing trust stores.
        with urllib.request.urlopen(f"https://127.0.0.1:{ports[1]}/", timeout=3,
                                   context=ssl._create_unverified_context()) as response:
            assert response.status == 200, response.status
        code = p.wait(timeout=20)
        if code != 0:
            raise RuntimeError(f'Host exited {code}; see {log}')
        for port in ports:
            with socket.socket() as s:
                assert s.connect_ex(('127.0.0.1', port)) != 0, f'Listener leaked: {port}'
        print('PASS host startup, TCP/SDK/proxy listeners, HTTPS 200, shutdown exit 0, listener cleanup')
    finally:
        if p.poll() is None:
            p.terminate()
            p.wait(timeout=5)
