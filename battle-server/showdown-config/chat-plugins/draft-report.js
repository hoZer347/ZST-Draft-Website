'use strict';

// Draft League auto-report plugin.
//
// On every finished battle, POST the full battle log to the .NET league server,
// which works out which scheduled match it was (by mapping each side's mons to
// the team that drafted them) and records the result + standings + stats. Games
// that aren't league matches (teambuilder test battles, already-reported matches)
// are acknowledged and ignored server-side, so this fires harmlessly for all
// battles.
//
// Installed into the bundled server's dist/server/chat-plugins/ on start by
// scripts/showdown.js (which also sets DRAFT_REPORT_URL and the secret-file env),
// so this repo file stays the source of truth and survives an `npm install`.

const fs = require('fs');
const http = require('http');
const https = require('https');

function readSecret() {
	try { return fs.readFileSync(process.env.DRAFT_REPORT_SECRET_FILE || '', 'utf8').trim(); }
	catch { return ''; }
}

function postReport(log) {
	const url = process.env.DRAFT_REPORT_URL;
	const secret = readSecret();
	if (!url || !secret) return; // not configured (or .NET hasn't written the secret yet)

	let target;
	try { target = new URL(url); } catch { return; }
	const body = JSON.stringify({ log });
	const lib = target.protocol === 'https:' ? https : http;
	const req = lib.request({
		method: 'POST',
		hostname: target.hostname,
		port: target.port,
		path: target.pathname,
		headers: {
			'Content-Type': 'application/json',
			'Content-Length': Buffer.byteLength(body),
			'X-Report-Secret': secret,
		},
	}, res => { res.resume(); }); // drain the response; we don't need the body
	// A reporting failure must never affect the battle/server.
	req.on('error', () => {});
	req.write(body);
	req.end();
}

exports.handlers = {
	// Signature from RoomBattle.end(): (battle, winnerid, playerids). We only need
	// the battle's room log, which by now includes team preview and the |win| line.
	onBattleEnd(battle) {
		try {
			const log = battle?.room?.getLog?.(-1);
			if (log) postReport(log);
		} catch { /* never throw out of a battle-end hook */ }
	},
};
