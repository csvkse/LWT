import os, time
from http.server import HTTPServer, BaseHTTPRequestHandler
import json

port = int(os.environ.get("PORT", "18002"))
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        data = {"service": os.environ.get("LWT_PLUGIN_ID", "demo-py-api"), "time": time.time(), "status": "active"}
        self.wfile.write(json.dumps(data).encode('utf-8'))

httpd = HTTPServer(('127.0.0.1', port), Handler)
print(f"[Python-API] Listening on port {port}...")
httpd.serve_forever()
