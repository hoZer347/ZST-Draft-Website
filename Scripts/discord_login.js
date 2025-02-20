// VV Client ID for user discord API
const CLIENT_ID = "1342220973679579138";

// Get the current URL dynamically to support multiple environments
const REDIRECT_URI = `${window.location.origin}${window.location.pathname}`;
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
	() =>
	{
		insertDiscordLoginUI(); // Inject the UI

		const loginButton = document.getElementById("login");
		const logoutButton = document.getElementById("logout");
		const userInfo = document.getElementById("user-info");

		if (loginButton)
			loginButton.addEventListener("click", () => { window.location.href = AUTH_URL; });

		if (logoutButton)
			logoutButton.addEventListener("click",
				() =>
				{
					window.location.hash = "";
					window.location.reload();
				});

		async function getAccessToken()
		{
			const hash = window.location.hash.substring(1);
			const params = new URLSearchParams(hash);
			return params.get("access_token");
		};

		async function fetchUserInfo(token)
		{
			const response = await fetch("https://discord.com/api/users/@me",
			{
				headers: { Authorization: `Bearer ${token}` },
			});
			return response.json();
		};

		async function handleLogin()
		{
			const token = await getAccessToken();
			if (!token) return;

			const user = await fetchUserInfo(token);
			document.getElementById("username").textContent = user.username;
			document.getElementById("user-id").textContent = user.id;
			document.getElementById("avatar").src = `https://cdn.discordapp.com/avatars/${user.id}/${user.avatar}.png`;

			if (loginButton) loginButton.style.display = "none";
			if (userInfo) userInfo.style.display = "block";
		};

		handleLogin();
	});
