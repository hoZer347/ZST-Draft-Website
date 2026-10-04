'use strict';

// Draft League chat plugin: let any NAMED user talk, with no registration.
//
// Coaches sign in by name only. The teambuilder auto-login sends `/trn <name>,0,`
// and the server runs `noguestsecurity`, so there is no login server, no password
// and no account: every coach is an "unregistered" user as far as Showdown is
// concerned. Stock chat.js then blocks them from the two places a league actually
// chats:
//
//   - battle rooms ("You must be registered to chat in temporary rooms"), because
//     a battle room is not `persist`ent;
//   - private messages ("You must be registered to send private messages", and
//     "That user is unregistered and cannot be PMed" on the receiving side).
//
// Both checks are `user.registered || user.autoconfirmed`, so marking a named user
// autoconfirmed on login clears them. Guests are left alone: they have no name, and
// chat.js turns them away earlier with "You must choose a name before you can talk."
//
// `autoconfirmed` (rather than `registered`) is the narrower flag: it grants the
// chat/PM permissions and nothing that implies a real account exists. It is reset on
// every rename by updateGroup/checkName, and loginfilter runs after both, so this
// re-applies on each login, merge and name change.
//
// Installed into the bundled server's dist/server/chat-plugins/ on start by
// scripts/showdown.js, like draft-report.js.

exports.loginfilter = function (user) {
	try {
		if (!user || !user.named || user.autoconfirmed) return;
		// A punished user keeps their punishment: namelocked/locked users are still
		// stopped by the checks above this one in chat.js.
		user.autoconfirmed = user.id;
	} catch { /* never break a login over this */ }
};
