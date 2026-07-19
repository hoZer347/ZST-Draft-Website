'use strict';

// Static file server for our self-hosted Showdown client
// (battle-server/client/play.pokemonshowdown.com), exposed at play.loomhozer.ca
// through the Cloudflare tunnel. Sprites/dex data still load from the official
// CDN (see Config.routes in client/config/config.js); we only serve the client
// itself + our tier-patched data files.
//
// gzips compressible responses (the tier data is ~15 MB raw, ~1.7 MB gzipped)
// and caches the compressed bytes in memory, so it stays fast over a home
// connection without a CDN.
//
//   PORT=8791 node scripts/serve-client.js

const http = require('http');
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const ROOT = path.join(__dirname, '..', 'client', 'play.pokemonshowdown.com');
const PORT = Number(process.env.PORT || 8791);

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.png': 'image/png', '.gif': 'image/gif', '.jpg': 'image/jpeg', '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2', '.map': 'application/json',
  '.wav': 'audio/wav', '.mp3': 'audio/mpeg', '.txt': 'text/plain; charset=utf-8',
};
const COMPRESSIBLE = new Set(['.html', '.js', '.css', '.json', '.svg', '.map', '.txt']);

// path -> { mtimeMs, raw, gz } — files don't change at runtime, so cache the
// gzipped bytes after the first hit instead of recompressing 15 MB per request.
const cache = new Map();

function load(file) {
  const stat = fs.statSync(file); // throws if missing → caught by caller
  const hit = cache.get(file);
  if (hit && hit.mtimeMs === stat.mtimeMs) return hit;
  const raw = fs.readFileSync(file);
  const ext = path.extname(file);
  const gz = COMPRESSIBLE.has(ext) ? zlib.gzipSync(raw, { level: 6 }) : null;
  const entry = { mtimeMs: stat.mtimeMs, raw, gz, ext };
  cache.set(file, entry);
  return entry;
}

http.createServer((req, res) => {
  let url = decodeURIComponent((req.url || '/').split('?')[0]);
  if (url.endsWith('/')) url += 'index-old.html';
  const file = path.join(ROOT, url);
  if (!file.startsWith(ROOT)) { res.writeHead(403).end(); return; }

  let entry;
  try { entry = load(file); }
  catch {
    // Extensionless paths (e.g. /teambuilder) are client-side routes → serve the
    // SPA (index-old.html). Missing assets (with an extension) are a real 404.
    if (!path.extname(url)) {
      try { entry = load(path.join(ROOT, 'index-old.html')); }
      catch { res.writeHead(404).end(); return; }
    } else {
      res.writeHead(404).end(); return;
    }
  }

  const headers = {
    'content-type': MIME[entry.ext] || 'application/octet-stream',
    // No-cache while we're actively patching the client — otherwise Cloudflare
    // caches our edited files for an hour and changes don't show. Raise this to a
    // long max-age once the client is finalised.
    'cache-control': 'no-cache',
  };
  const acceptsGzip = /\bgzip\b/.test(req.headers['accept-encoding'] || '');
  if (entry.gz && acceptsGzip) {
    headers['content-encoding'] = 'gzip';
    headers['vary'] = 'Accept-Encoding';
    res.writeHead(200, headers);
    res.end(entry.gz);
  } else {
    res.writeHead(200, headers);
    res.end(entry.raw);
  }
}).listen(PORT, '127.0.0.1', () => console.log(`[serve-client] http://127.0.0.1:${PORT} -> ${ROOT} (gzip on)`));
