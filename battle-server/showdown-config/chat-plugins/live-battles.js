'use strict';

// Answers /livebattles for server/deploy-live.ps1. The stock battleCount skips a
// battle with a player disconnected, which is just when a restart would hurt.
//
// Installed into the bundled server's dist/server/chat-plugins/ on start by
// scripts/showdown.js, like draft-report.js.

exports.commands = {
	livebattles(target, room, user, connection) {
		const live = [...Rooms.rooms.values()].filter(({battle}) => battle && battle.started && !battle.ended);
		connection.send(`|queryresponse|livebattles|${live.length}`);
	},
};
