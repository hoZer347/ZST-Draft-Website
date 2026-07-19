'use strict';

// Static file server for our self-hosted Showdown client
// (battle-server/client/play.pokemonshowdown.com), exposed at play.loomhozer.ca
// through the Cloudflare tunnel. Sprites/dex data still load from the official
// CDN (see Config.routes in client/config/config.js); we only serve the client
// itself + our tier-patched data files.
//
//   PORT=8791 node scripts/serve-client.js

const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', 'client', 'play.pokemonshowdown.com');
const PORT = Number(process.env.PORT || 8791);

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.png': 'image/png', '.gif': 'image/gif', '.jpg': 'image/jpeg', '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2', '.map': 'application/json',
  '.wav': 'audio/wav', '.mp3': 'audio/mpeg', '.txt': 'text/plain; charset=utf-8',
};

http.createServer((req, res) => {
  let url = decodeURIComponent((req.url || '/').split('?')[0]);
  if (url.endsWith('/')) url += 'index-new.html';
  const file = path.join(ROOT, url);
  if (!file.startsWith(ROOT)) { res.writeHead(403).end(); return; }

  fs.readFile(file, (err, body) => {
    if (err) {
      // Extensionless paths (e.g. /teambuilder) are client-side routes → the
      // client SPA is index-new.html. Missing assets (with an extension) are a real
      // 404.
      if (!path.extname(url)) {
        fs.readFile(path.join(ROOT, 'index-new.html'), (e2, idx) => {
          if (e2) { res.writeHead(404).end(); return; }
          res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
          res.end(idx);
        });
      } else {
        res.writeHead(404).end();
      }
      return;
    }
    res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream' });
    res.end(body);
  });
}).listen(PORT, '127.0.0.1', () => console.log(`[serve-client] http://127.0.0.1:${PORT} -> ${ROOT}`));
