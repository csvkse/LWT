const http = require('http');
const port = process.env.PORT || 18020;
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({
    function: 'demo-serverless-api',
    executionMode: 'Serverless',
    timestamp: new Date().toISOString()
  }));
});
server.listen(port, () => console.log(`[Serverless-API] Function worker listening on port ${port}`));
