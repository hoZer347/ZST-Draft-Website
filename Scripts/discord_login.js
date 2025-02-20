////////////////////////////////////////////////////////////////////////////////////////////////////
//
// discord_login.js
// - Used for providing and caching a discord login
//
////////////////////////////////////////////////////////////////////////////////////////////////////


// VV Client ID for user discord API
const CLIENT_ID = "1342220973679579138";

// Get the current URL dynamically to support multiple environments
const REDIRECT_URI =
	window.location.hostname === "localhost"
		? "http://localhost:8000/"
		: "https://hozer347.github.io/ZST-Draft-Website/";

const AUTH_URL = `https://discord.com/api/oauth2/authorize?client_id=${CLIENT_ID}&redirect_uri=${encodeURIComponent(REDIRECT_URI)}&response_type=token&scope=identify`;

// Function to inject the login UI
function insertDiscordLoginUI()
{
	const container = document.createElement("div");
	container.id = "discord-auth-container";
	container.innerHTML = `
		<h2>Login with Discord</h2>
		<button id="login" style="background-color: #5865F2; color: white; padding: 10px; border: none; border-radius: 5px; cursor: pointer;">
			Login with Discord
		</button>
		<div id="user-info" style="display: none; margin-top: 20px;">
			<h3>Welcome, <span id="username"></span>!</h3>
			<img id="avatar" src="" alt="Avatar" width="100">
			<p>Your Discord ID: <span id="user-id"></span></p>
			<button id="logout">Logout</button>
		</div>
	`;
	document.body.prepend(container); // Insert at the top of the body
};

document.addEventListener("DOMContentLoaded",
	async () =>
	{
		insertDiscordLoginUI(); // Inject the UI

		const loginButton = document.getElementById("login");
		const logoutButton = document.getElementById("logout");
		const userInfo = document.getElementById("user-info");

		if (loginButton)
			loginButton.addEventListener("click",
				() =>
				{
					window.location.href = AUTH_URL;
				});

		if (logoutButton)
			logoutButton.addEventListener("click",
				() =>
				{
					localStorage.removeItem("discord_token"); // Clear cached token
					window.location.hash = "";
					window.location.reload();
				});

		async function getAccessToken()
		{
			// Check if token is in URL (first-time login)
			const hash = window.location.hash.substring(1);
			const params = new URLSearchParams(hash);
			let token = params.get("access_token");

			if (token)
			{
				localStorage.setItem("discord_token", token); // Save token in localStorage
				window.history.replaceState({}, document.title, window.location.pathname); // Clean URL
			}
			else token = localStorage.getItem("discord_token"); // Retrieve token from cache
			
			return token;
		};

		async function fetchUserInfo(token)
		{
			if (!token) return null;

			try
			{
				const response = await fetch("https://discord.com/api/users/@me",
				{
					headers: { Authorization: `Bearer ${token}` },
				});

				if (!response.ok)
				{
					localStorage.removeItem("discord_token"); // Remove invalid token
					return null;
				};

				return response.json();
			}
			catch
			{
				return null;
			};
		};

		async function handleLogin()
		{
			const token = await getAccessToken();
			if (!token) return;

			const user = await fetchUserInfo(token);
			if (!user) return;

			document.getElementById("username").textContent = user.username;
			document.getElementById("user-id").textContent = user.id;
			document.getElementById("avatar").src = `https://cdn.discordapp.com/avatars/${user.id}/${user.avatar}.png`;

			if (loginButton) loginButton.style.display = "none";
			if (userInfo) userInfo.style.display = "block";
		};

		await handleLogin();
	});
