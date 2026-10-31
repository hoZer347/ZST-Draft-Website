'use strict';

// Room.send drops a refresh's join line once the merging Guest's leave has emptied the room.

exports.handlers = {
	onRoomJoin(room, user, connection) {
		if (user.named && room.userCount === 1 && connection?.inRooms.has(room.roomid)) {
			room.send(`|J|${user.getIdentityWithStatus(room)}`);
		}
	},
};
