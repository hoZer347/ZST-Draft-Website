'use strict';

// Starts the battle server. Port/host from env, with dev defaults.
//
//   node scripts/serve.js            # 127.0.0.1:8787
//   PORT=9001 node scripts/serve.js
//
// Open http://127.0.0.1:8787/ for the in-browser viewer. GET /health returns
// { ok, format, rooms }. Battles are driven over WebSocket (see lib/server.js).

const { BattleServer } = require('../lib/server');

const port = Number(process.env.PORT || 8787);
const host = process.env.HOST || '127.0.0.1';

const server = new BattleServer();
server.listen(port, host).then((addr) => {
  console.log(`[battle-server] listening on ws://${addr.host}:${addr.port}`);
  console.log(`[battle-server] format: ${server.format}`);
});

// Clean shutdown so the port frees immediately on Ctrl-C / nodemon restarts.
for (const sig of ['SIGINT', 'SIGTERM']) {
  process.on(sig, async () => { await server.close(); process.exit(0); });
}
