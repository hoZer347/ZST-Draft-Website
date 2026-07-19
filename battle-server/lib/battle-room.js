'use strict';

const { BattleStream, getPlayerStreams } = require('pokemon-showdown');

/**
 * One battle in progress, wrapped around the sim's streams.
 *
 * getPlayerStreams gives us a redacted stream per side (p1 never sees p2's
 * hidden info), a spectator stream (public view), and an omniscient stream
 * (everything — used here only to detect the result). This class is transport
 * agnostic: a WebSocket layer, the .NET bridge, or a test drives it entirely
 * through `on(channel, fn)` to receive protocol and `choose(side, decision)` to
 * submit moves. It knows nothing about how players are connected.
 */
class BattleRoom {
  /**
   * @param {object}  opts
   * @param {string}  opts.id       stable room id, e.g. "match-42"
   * @param {string}  opts.format   sim format id, e.g. "gen9customgame"
   * @param {{name:string, team:string}} opts.p1  packed-team player spec
   * @param {{name:string, team:string}} opts.p2
   */
  constructor({ id, format = 'gen9customgame', p1, p2 }) {
    this.id = id;
    this.format = format;
    this.winner = undefined; // undefined while live; string name or '' (tie) once decided
    this.ended = false;

    this._handlers = { p1: new Set(), p2: new Set(), spectator: new Set(), end: new Set() };
    // Per-channel backlog. The sim starts emitting protocol the instant this
    // room is constructed, but a networked subscriber (a browser, a spectator
    // joining mid-battle) attaches several round-trips later and would miss the
    // opening chunks — including the first request, which deadlocks the battle.
    // We record each channel's chunks and replay them to any late subscriber.
    this._log = { p1: [], p2: [], spectator: [] };
    this._streams = getPlayerStreams(new BattleStream());

    this._consume('p1');
    this._consume('p2');
    this._consume('spectator');
    this._watchResult();

    const spec = { formatid: format };
    void this._streams.omniscient.write(
      `>start ${JSON.stringify(spec)}\n` +
      `>player p1 ${JSON.stringify(p1)}\n` +
      `>player p2 ${JSON.stringify(p2)}`
    );
  }

  /**
   * Subscribe to a channel: 'p1' | 'p2' | 'spectator' | 'end'. Returns an
   * unsubscribe fn. A subscriber to a protocol channel is immediately replayed
   * that channel's backlog, so it catches up to the live state regardless of
   * when it joined.
   */
  on(channel, fn) {
    this._handlers[channel].add(fn);
    const backlog = this._log[channel];
    if (backlog) {
      for (const data of backlog) {
        try { fn(data); } catch (e) { console.error(`[BattleRoom ${this.id}] replay error`, e); }
      }
    }
    return () => this._handlers[channel].delete(fn);
  }

  _emit(channel, data) {
    if (this._log[channel]) this._log[channel].push(data);
    for (const fn of this._handlers[channel]) {
      try { fn(data); } catch (e) { console.error(`[BattleRoom ${this.id}] handler error`, e); }
    }
  }

  async _consume(side) {
    for await (const chunk of this._streams[side]) this._emit(side, chunk);
  }

  // The omniscient feed is authoritative for the outcome. We never forward it to
  // a player — it would leak hidden info — only mine it for the winner.
  async _watchResult() {
    for await (const chunk of this._streams.omniscient) {
      for (const line of chunk.split('\n')) {
        if (line.startsWith('|win|')) this.winner = line.slice(5);
        else if (line.startsWith('|tie')) this.winner = '';
      }
    }
    this.ended = true;
    this._emit('end', { winner: this.winner ?? null });
  }

  /** Submit a decision for a side, e.g. "move 1", "move 2 terastallize", "switch 3". */
  choose(side, decision) {
    if (this.ended || (side !== 'p1' && side !== 'p2')) return;
    void this._streams[side].write(String(decision));
  }

  /** End the battle immediately with the other side as winner (disconnect/forfeit). */
  forfeit(side) {
    if (this.ended || (side !== 'p1' && side !== 'p2')) return;
    const winner = side === 'p1' ? 'p2' : 'p1';
    void this._streams.omniscient.write(`>forcewin ${winner}`);
  }
}

module.exports = { BattleRoom };
