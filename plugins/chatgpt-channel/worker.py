import os, time
from http.server import HTTPServer, BaseHTTPRequestHandler
import json

port = int(os.environ.get("PORT", "18010"))
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        data = {"channel": "ChatGPT Multi-Model Gateway", "status": "online", "time": time.time()}
        self.wfile.write(json.dumps(data).encode('utf-8'))

httpd = HTTPServer(('127.0.0.1', port), Handler)
print(f"[ChatGPT-Channel] Gateway running on port {port}...")
httpd.serve_forever()
