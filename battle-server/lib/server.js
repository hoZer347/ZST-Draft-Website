'use strict';

const http = require('http');
const fs = require('fs');
const path = require('path');
const { WebSocketServer } = require('ws');
const { BattleRoom } = require('./battle-room');
const { packDraftTeam } = require('./pack');
const { DRAFT_FORMAT_ID, resolve } = require('./format');

const PUBLIC_DIR = path.join(__dirname, '..', 'public');

// Sample draftish rosters for the in-browser demo match, so the viewer page can
// start a battle without the client having to pack teams (which needs the sim).
const DEMO_TEAMS = {
  p1: ['charizard-megay', 'venusaur', 'blastoise-mega', 'butterfree', 'beedrill-mega', 'pidgeot-mega'],
  p2: ['venusaur-mega', 'charizard', 'gyarados-mega', 'blastoise', 'charizard-megax', 'alakazam-mega'],
};

/**
 * The self-hosted battle server: a thin WebSocket transport over BattleRoom.
 *
 * It owns no game logic, BattleRoom does, and no matchmaking policy; in the
 * real flow the .NET side decides who plays whom and seats them. This layer just
 * (1) holds live rooms by id, (2) fans each side's redacted protocol out to the
 * sockets watching that side, and (3) relays decisions back down. Same-origin to
 * the browser is the whole point: unlike the cross-origin teambuilder iframe, we
 * can read the battle stream and its result.
 *
 * Wire protocol (JSON text frames):
 *   client → server
 *     {type:'create', matchId, p1:{name,team}, p2:{name,team}, format?}
 *     {type:'join',   matchId, side}            side: 'p1'|'p2'|'spectator'
 *     {type:'choose', decision}                 e.g. 'move 1', 'switch 3'
 *     {type:'forfeit'}
 *   server → client
 *     {type:'joined', matchId, side}
 *     {type:'protocol', chunk}                  sim protocol for the joined channel
 *     {type:'end', winner}                      winner name, '' tie, or null
 *     {type:'error', error}
 *
 * A connection watches exactly one (room, side). `choose`/`forfeit` act on that
 * seat, so a spectator socket can't submit decisions.
 */
class BattleServer {
  constructor({ format = DRAFT_FORMAT_ID } = {}) {
    // Fail fast at boot if the league format is misconfigured, rather than
    // starting rule-less battles (see the silent-null note in lib/format.js).
    // resolve() validates and throws on misconfig; we keep the full id STRING
    // (with the @@@rules), not the resolved format's `.id`, which is just the
    // rule-less base.
    resolve();
    this.format = format;
    this.rooms = new Map(); // matchId -> BattleRoom
    this.http = http.createServer((req, res) => this._handleHttp(req, res));
    this.wss = new WebSocketServer({ server: this.http });
    this.wss.on('connection', (ws) => this._handleConnection(ws));
  }

  listen(port = 8080, host = '127.0.0.1') {
    return new Promise((resolve) => this.http.listen(port, host, () => resolve(this.address())));
  }

  address() {
    const a = this.http.address();
    return a && typeof a === 'object' ? { host: a.address, port: a.port } : null;
  }

  close() {
    for (const room of this.rooms.values()) room.forfeit('p1'); // end anything live
    this.rooms.clear();
    // Terminate open sockets first: otherwise http.close() waits forever on the
    // still-open WebSocket upgrade connections and its callback never fires.
    for (const client of this.wss.clients) client.terminate();
    return new Promise((resolve) => this.wss.close(() => this.http.close(() => resolve())));
  }

  // Serves the viewer page and a health/info endpoint. The page is same-origin
  // with the WebSocket, which is the whole reason we self-host: the browser can
  // read the battle stream and its result (unlike the cross-origin PS iframe).
  _handleHttp(req, res) {
    const url = (req.url || '/').split('?')[0];

    if (url === '/health') {
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ ok: true, format: this.format, rooms: this.rooms.size }));
      return;
    }

    // Static viewer. Only the files in public/ are served, and the path is
    // resolved+prefix-checked so a "../" can't escape the directory.
    const rel = url === '/' ? 'index.html' : url.replace(/^\/+/, '');
    const file = path.join(PUBLIC_DIR, rel);
    if (!file.startsWith(PUBLIC_DIR)) { res.writeHead(403).end(); return; }
    fs.readFile(file, (err, body) => {
      if (err) { res.writeHead(404).end(); return; }
      res.writeHead(200, { 'content-type': contentType(file) });
      res.end(body);
    });
  }

  _handleConnection(ws) {
    // Per-socket seat: which room and side this connection is watching/playing.
    const seat = { room: null, side: null, unsub: null };

    ws.on('message', (raw) => {
      let msg;
      try { msg = JSON.parse(raw.toString()); }
      catch { return send(ws, { type: 'error', error: 'Malformed JSON frame' }); }

      try { this._dispatch(ws, seat, msg); }
      catch (e) { send(ws, { type: 'error', error: e.message }); }
    });

    ws.on('close', () => {
      if (seat.unsub) seat.unsub();
      // A player dropping mid-battle forfeits their side so the match resolves
      // rather than hanging on a decision that will never come. Spectators just
      // detach.
      if (seat.room && !seat.room.ended && (seat.side === 'p1' || seat.side === 'p2')) {
        seat.room.forfeit(seat.side);
      }
    });
  }

  _dispatch(ws, seat, msg) {
    switch (msg.type) {
      case 'create':
      case 'create-demo': {
        if (!msg.matchId) throw new Error("'create' needs a matchId");
        if (this.rooms.has(msg.matchId)) throw new Error(`Room '${msg.matchId}' already exists`);

        // The demo packs sample rosters server-side so the viewer page doesn't
        // need the sim to build teams; a real 'create' carries the team specs.
        const p1 = msg.type === 'create-demo'
          ? { name: 'Team Alpha', team: packDraftTeam(DEMO_TEAMS.p1) }
          : msg.p1;
        const p2 = msg.type === 'create-demo'
          ? { name: 'Team Beta', team: packDraftTeam(DEMO_TEAMS.p2) }
          : msg.p2;
        if (!p1 || !p2) throw new Error("'create' needs p1 and p2 team specs");

        const room = new BattleRoom({
          id: msg.matchId,
          format: msg.format || this.format,
          p1,
          p2,
        });
        this.rooms.set(msg.matchId, room);
        // Reap the room once the sim reports a result, so ids don't leak.
        room.on('end', () => this.rooms.delete(msg.matchId));
        send(ws, { type: 'created', matchId: msg.matchId });
        return;
      }

      case 'join': {
        const room = this.rooms.get(msg.matchId);
        if (!room) throw new Error(`No room '${msg.matchId}'`);
        const side = msg.side || 'spectator';
        if (!['p1', 'p2', 'spectator'].includes(side)) throw new Error(`Bad side '${side}'`);

        if (seat.unsub) seat.unsub(); // one seat per socket; re-joining moves it
        seat.room = room;
        seat.side = side;
        // Forward this side's protocol chunks straight to the socket.
        seat.unsub = room.on(side, (chunk) => send(ws, { type: 'protocol', chunk }));
        const endUnsub = room.on('end', ({ winner }) => send(ws, { type: 'end', winner }));
        // Chain the end-unsub onto the seat so a re-join clears both.
        const baseUnsub = seat.unsub;
        seat.unsub = () => { baseUnsub(); endUnsub(); };

        send(ws, { type: 'joined', matchId: msg.matchId, side });
        if (room.ended) send(ws, { type: 'end', winner: room.winner ?? null });
        return;
      }

      case 'choose': {
        if (!seat.room) throw new Error('Join a room before choosing');
        if (seat.side !== 'p1' && seat.side !== 'p2') throw new Error('Spectators cannot choose');
        seat.room.choose(seat.side, String(msg.decision ?? 'default'));
        return;
      }

      case 'forfeit': {
        if (!seat.room) throw new Error('Join a room before forfeiting');
        if (seat.side !== 'p1' && seat.side !== 'p2') throw new Error('Spectators cannot forfeit');
        seat.room.forfeit(seat.side);
        return;
      }

      default:
        throw new Error(`Unknown message type '${msg.type}'`);
    }
  }
}

function send(ws, obj) {
  if (ws.readyState === ws.OPEN) ws.send(JSON.stringify(obj));
}

function contentType(file) {
  switch (path.extname(file)) {
    case '.html': return 'text/html; charset=utf-8';
    case '.js': return 'text/javascript; charset=utf-8';
    case '.css': return 'text/css; charset=utf-8';
    default: return 'application/octet-stream';
  }
}

module.exports = { BattleServer };
