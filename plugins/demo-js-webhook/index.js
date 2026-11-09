const http = require('http');
const port = process.env.PORT || 18001;
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ status: 'ok', worker: process.env.LWT_PLUGIN_ID || 'demo-js-webhook', time: new Date() }));
});
server.listen(port, () => console.log(`[JS-Webhook] Service listening on port ${port}`));
