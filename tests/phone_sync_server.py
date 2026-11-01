"""Isolated localhost fixture for the Android instrumentation repair regression."""
import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--output', required=True)
args = parser.parse_args()
output = Path(args.output)
output.mkdir(parents=True, exist_ok=True)
state = {
    'etag': '"v1"', 'uploads': 0, 'conflicts': 0, 'credential_free': False,
    'document': {'schemaVersion': 1, 'papers': [
        {'id': 'server-note', 'type': 'note', 'content': 'server-side addition',
         'updatedAt': int(time.time() * 1000), 'items': [], 'noteCanvasElements': []}
    ], 'syncTombstones': []},
}

class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def respond(self, code, document=None):
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.send_header('ETag', state['etag'])
        self.end_headers()
        self.wfile.write(json.dumps(document or {}).encode('utf-8'))

    def do_GET(self):
        if self.path == '/shutdown':
            self.respond(200)
            threading.Thread(target=self.server.shutdown, daemon=True).start()
        elif self.headers.get('Authorization') != 'Bearer audit-only-token':
            self.respond(401)
        else:
            self.respond(200, state['document'])

    def do_POST(self):
        length = int(self.headers.get('Content-Length', '0'))
        if length > 32 * 1024 * 1024:
            self.respond(413)
            return
        document = json.loads(self.rfile.read(length))
        state['uploads'] += 1
        if self.headers.get('Authorization') != 'Bearer audit-only-token':
            self.respond(401)
            return
        if state['uploads'] == 1:
            # A second device wins the first write race. The phone must fetch and merge again.
            state['etag'] = '"v2"'
            state['document']['papers'][0]['updatedAt'] += 1
            state['conflicts'] += 1
            self.respond(412)
            return
        if self.headers.get('If-Match') != state['etag']:
            state['conflicts'] += 1
            self.respond(412)
            return
        state['credential_free'] = set(document).issubset({'schemaVersion', 'papers', 'syncTombstones'})
        state['document'] = document
        state['etag'] = '"v3"'
        (output / 'phone-upload.json').write_text(json.dumps(document, ensure_ascii=False, indent=2), encoding='utf-8')
        self.respond(200)

server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
(output / 'phone-test-port.txt').write_text(str(server.server_port), encoding='utf-8')
print(f'Phone fixture server: 127.0.0.1:{server.server_port}', flush=True)
try:
    server.serve_forever()
finally:
    server.server_close()
    (output / 'phone-server-results.json').write_text(json.dumps({k: v for k, v in state.items() if k != 'document'}, indent=2), encoding='utf-8')
