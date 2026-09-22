"""Browser smoke checks against the real production bundle with simulated game data.
Run after npm run build: pip install playwright==1.57.0; playwright install chromium;
python scripts/test-dashboard.py. No live server or player data is accessed.
"""
import copy
import functools
import http.server
import json
import pathlib
import threading
from urllib.parse import urlparse, parse_qs
from playwright.sync_api import sync_playwright, expect

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'TestResults' / 'screenshots'
OUT.mkdir(parents=True, exist_ok=True)
server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), functools.partial(http.server.SimpleHTTPRequestHandler, directory=str(ROOT / 'src/ValheimServerManager/wwwroot')))
threading.Thread(target=server.serve_forever, daemon=True).start()
base = f'http://127.0.0.1:{server.server_port}'
players = [dict(peerId=42, peerKey='9223372036854775806', name='Eirik', platformId='Steam_76561198000000001', companion=True, inventoryAllowed=True, serverCharacter=True, ping=28, connectedAt='2026-09-23T02:00:00Z'), dict(peerId=43, peerKey='43', name='Freya', platformId='Steam_76561198000000002', companion=True, inventoryAllowed=True, serverCharacter=True, ping=34, connectedAt='2026-09-23T02:00:00Z')]
character = dict(id='4567', name='Eirik', biome='BlackForest', health=127, maxHealth=175, stamina=112, maxStamina=150, eitr=48, maxEitr=80, armor=68, weight=196.5, inventoryWidth=8, inventoryHeight=4, skills=[dict(name='Swords', level=64), dict(name='Run', level=72)])
items = [dict(prefab=name.replace(' ', ''), name=name, description='Simulated inspection fixture.', type='Weapon' if i < 3 else 'Material', stack=1 if i < 3 else 18, maxStack=50, quality=3, maxQuality=4, durability=175, maxDurability=200, weight=2, equipped=i < 3, x=i % 8, y=i // 8, variant=0, crafterName='Eirik', crafterId='4567', teleportable=True, iconKey='') for i, name in enumerate(['Silver sword', 'Banded shield', 'Iron helmet', 'Cooked meat', 'Wood', 'Stone', 'Resin', 'Fine wood', 'Leather scraps', 'Surtling core', 'Iron', 'Coal'])]
snapshot = dict(ok=True, capturedAt='2026-09-23T02:05:00Z', character=character, items=items, icons={})
files = {'example.mod/recipes/items.yml': 'recipes:\n  sword:\n    enabled: true\n'}
revision = 'a' * 64
streams, cancelled, errors = [], [], []

def api(route):
    global revision
    url = urlparse(route.request.url)
    path = url.path
    data = []
    if path.endswith('/auth/state'): data = dict(authenticated=True, userName='Admin', steamId='76561198000000000')
    elif path.endswith('/auth/csrf'): data = dict(token='smoke-token')
    elif path.endswith('/status'): data = dict(status='running', uptimeSeconds=7200, agentConnected=True, agentVersion='2.1.3', gameVersion='test fixture', restartRequired=False, players=2)
    elif path.endswith('/manager-update'): data = dict(currentVersion='2.1.3', automaticUpdates=False)
    elif path.endswith('/characters'): data = dict(installed=True, importAvailable=False, serverStatus='running', characters=[])
    elif path.endswith('/players'): data = players
    elif path.endswith('/mods'): data = [dict(id='demo', namespace='Example', name='Crafting', version='1.0.0', source='thunderstore', protected=False, enabled=True)]
    elif path.endswith('/files/content'):
        key = parse_qs(url.query)['path'][0]
        data = dict(path=key, revision=revision, content=files[key], modifiedAt='2026-09-23T02:00:00Z')
    elif path.endswith('/files'):
        if route.request.method == 'POST':
            body = route.request.post_data_json
            assert route.request.headers.get('x-csrf-token') == 'smoke-token'
            if body['operation'] == 'save':
                files[body['path']] = body['content']; revision = 'b' * 64
            elif body['operation'] == 'create': files[body['path']] = body['content']
            route.fulfill(status=204); return
        directories = {'example.mod', 'example.mod/recipes'}
        for key in files:
            directories.update(str(parent) for parent in pathlib.PurePosixPath(key).parents if str(parent) != '.')
        entries = [dict(path=key, name=key.split('/')[-1], kind='directory', size=0) for key in sorted(directories)]
        entries += [dict(path=key, name=key.split('/')[-1], kind='file', size=len(value)) for key, value in files.items()]
        data = dict(roots=['example.mod'], entries=entries)
    route.fulfill(json=data)

def socket(ws):
    def message(raw):
        for part in raw.split('\x1e'):
            if not part: continue
            value = json.loads(part)
            if 'protocol' in value: ws.send('{}\x1e')
            elif value.get('type') == 4:
                assert value['target'] == 'WatchPlayer'
                streams.append((ws, value['invocationId'], value['arguments'][0]))
                emit(len(streams))
            elif value.get('type') == 5: cancelled.append(value['invocationId'])
    ws.on_message(message)

def emit(sequence, health=None):
    ws, invocation, peer = streams[-1]
    sample = copy.deepcopy(snapshot)
    if health is not None: sample['character']['health'] = health
    if peer == '43': sample['character']['name'] = 'Freya'
    frame = dict(sequence=sequence, peerId=peer, receivedAt='2026-09-23T02:05:00Z', status='live', snapshot=sample)
    ws.send(json.dumps(dict(type=2, invocationId=invocation, item=frame)) + '\x1e')

try:
    with sync_playwright() as p:
        browser = p.chromium.launch()
        page = browser.new_page(viewport=dict(width=1600, height=1000), color_scheme='dark')
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.route('**/api/v1/**', api)
        page.route('**/hubs/live/negotiate*', lambda route: route.fulfill(json=dict(negotiateVersion=1, connectionId='mock', connectionToken='mock', availableTransports=[dict(transport='WebSockets', transferFormats=['Text', 'Binary'])])))
        page.route_web_socket('**/hubs/live*', socket)
        page.goto(base)
        page.get_by_role('button', name='Characters', exact=True).click()
        page.get_by_role('button', name='Inspect live character').first.click()
        dialog = page.get_by_role('dialog')
        expect(dialog.get_by_role('meter', name='Health')).to_have_attribute('aria-valuenow', '127')
        assert streams[-1][2] == players[0]['peerKey'], '64-bit identity was rounded'
        dialog.get_by_role('button', name='Slot 1, 1: Silver sword, 1', exact=True).click()
        expect(dialog.get_by_role('heading', name='Silver sword', exact=True)).to_be_visible()
        page.screenshot(path=str(OUT / 'inspection-desktop.png'))
        emit(10, 89)
        expect(dialog.get_by_role('meter', name='Health')).to_have_attribute('aria-valuenow', '89')
        dialog.get_by_role('button', name='Pause', exact=True).click()
        expect(dialog.get_by_text('Paused', exact=True)).to_be_visible()
        page.wait_for_timeout(250)
        assert cancelled, 'Pause did not cancel the stream'
        count = len(streams)
        dialog.get_by_role('button', name='Resume', exact=True).click()
        expect(dialog.get_by_role('meter', name='Health')).to_have_attribute('aria-valuenow', '127')
        assert len(streams) > count
        dialog.get_by_role('tab', name='Skills', exact=True).click()
        expect(dialog.get_by_text('Swords', exact=True)).to_be_visible()
        dialog.get_by_role('tab', name='Inventory', exact=True).click()
        dialog.get_by_role('button', name='Freya Inspection ready').click()
        expect(dialog.get_by_role('heading', name='Freya', exact=True)).to_be_visible()
        expect(dialog.get_by_role('meter', name='Health')).to_have_attribute('aria-valuenow', '127')
        assert streams[-1][2] == '43'
        page.wait_for_timeout(5600)
        expect(dialog.get_by_text('Stale', exact=True)).to_be_visible()
        page.set_viewport_size(dict(width=390, height=844))
        emit(20)
        expect(dialog.get_by_label('Inspect player')).to_be_visible()
        box = dialog.bounding_box()
        assert box and box['x'] >= 0 and box['x'] + box['width'] <= 391
        page.screenshot(path=str(OUT / 'inspection-mobile.png'))
        count = len(cancelled)
        dialog.get_by_role('button', name='Close', exact=True).click()
        page.wait_for_timeout(250)
        assert len(cancelled) > count, 'Closing did not cancel the stream'
        page.set_viewport_size(dict(width=1600, height=1000))
        page.get_by_role('button', name='Files', exact=True).click()
        page.get_by_role('button', name='recipes', exact=True).click()
        page.get_by_role('button', name='items.yml', exact=True).click()
        page.get_by_role('button', name='Open raw editor', exact=True).click()
        editor = page.get_by_role('textbox', name='Edit example.mod/recipes/items.yml')
        expect(editor).to_have_value(files['example.mod/recipes/items.yml'])
        editor.fill('recipes:\n  sword:\n    enabled: false\n')
        page.screenshot(path=str(OUT / 'files-desktop.png'))
        page.get_by_role('button', name='Save for restart', exact=True).click()
        expect(page.get_by_role('button', name='Save for restart', exact=True)).to_be_disabled()
        assert 'false' in files['example.mod/recipes/items.yml']
        assert not errors, errors
        print('PASS: live stats, item details, skills, pause/resume, target switching, stale state, mobile bounds, close cleanup, file tree and save')
        browser.close()
finally:
    server.shutdown()
