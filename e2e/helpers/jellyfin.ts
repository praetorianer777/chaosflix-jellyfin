import fs from "node:fs";
import path from "node:path";
import { APIRequestContext, Page, expect, request } from "@playwright/test";
import { JELLYFIN_URL } from "../playwright.config";

export const ADMIN = { name: "e2e-admin", password: "chaosflix-e2e" };
export const PLUGIN_ID = "c4a05f11-4ccc-4b00-bdea-dbeef1337000";
export const FAKE_API = "http://fake-ccc:3000/public";
export const CONFERENCE_TITLE = "E2E Congress 2025";

export type Client = { name: string; device: string; deviceId: string };

const SUITE_CLIENT: Client = {
	name: "chaosflix-e2e",
	device: "playwright",
	deviceId: "chaosflix-e2e",
};

/**
 * Two clients the way Jellyfin tells them apart: by the DeviceId in the
 * authorization header. Each gets its own session, so what one reports has to
 * travel through the server's user data to reach the other.
 */
export const WEB_CLIENT: Client = {
	name: "Jellyfin Web",
	device: "chromium",
	deviceId: "chaosflix-e2e-web",
};

export const ANDROID_CLIENT: Client = {
	name: "Jellyfin Android",
	device: "pixel-emulator",
	deviceId: "chaosflix-e2e-android",
};

const authHeader = (client: Client, token: string) =>
	`MediaBrowser Client="${client.name}", Device="${client.device}", ` +
	`DeviceId="${client.deviceId}", Version="1.0.0"` +
	(token ? `, Token="${token}"` : "");

export type PluginConfig = {
	PreferredQuality?: "High" | "Standard";
	PreferredFormat?: "Mp4" | "WebM";
	PreferredLanguage?: string;
	ApiBaseUrl?: string;
};

/** Global setup runs in its own process, so the token is handed over on disk. */
export const TOKEN_FILE = path.join(__dirname, "..", ".artifacts", "token");

export function saveToken(token: string): void {
	fs.mkdirSync(path.dirname(TOKEN_FILE), { recursive: true });
	fs.writeFileSync(TOKEN_FILE, token);
}

export function readToken(): string {
	return fs.readFileSync(TOKEN_FILE, "utf8").trim();
}

export async function apiContext(
	token: string = readTokenIfPresent(),
	client: Client = SUITE_CLIENT,
): Promise<APIRequestContext> {
	return request.newContext({
		baseURL: JELLYFIN_URL,
		extraHTTPHeaders: { Authorization: authHeader(client, token) },
	});
}

/**
 * Signs the same user in again as a different device. The suite's shared token
 * belongs to one device, and reusing it would make both "clients" the same
 * session, which is exactly what the cross-client tests must not do.
 */
export async function clientContext(
	client: Client,
): Promise<APIRequestContext> {
	const anonymous = await apiContext("", client);
	return apiContext(await authenticate(anonymous), client);
}

function readTokenIfPresent(): string {
	try {
		return readToken();
	} catch {
		return "";
	}
}

export async function waitForJellyfin(
	api: APIRequestContext,
	timeoutMs = 180_000,
): Promise<void> {
	const deadline = Date.now() + timeoutMs;
	while (Date.now() < deadline) {
		try {
			const response = await api.get("/System/Info/Public");
			if (response.ok()) {
				return;
			}
		} catch {
			// Container is not accepting connections yet.
		}

		await new Promise((resolve) => setTimeout(resolve, 2000));
	}

	throw new Error(
		`Jellyfin at ${JELLYFIN_URL} did not become ready in ${timeoutMs}ms`,
	);
}

/** Runs the startup wizard; a no-op when the instance is already configured. */
export async function completeStartupWizard(
	api: APIRequestContext,
): Promise<void> {
	const info = await (await api.get("/System/Info/Public")).json();
	if (info.StartupWizardCompleted) {
		return;
	}

	await api.post("/Startup/Configuration", {
		data: {
			UICulture: "en-US",
			MetadataCountryCode: "DE",
			PreferredMetadataLanguage: "en",
		},
	});
	await api.get("/Startup/User");
	await api.post("/Startup/User", {
		data: { Name: ADMIN.name, Password: ADMIN.password },
	});
	await api.post("/Startup/RemoteAccess", {
		data: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false },
	});
	await api.post("/Startup/Complete");
}

export async function authenticate(api: APIRequestContext): Promise<string> {
	const response = await api.post("/Users/AuthenticateByName", {
		data: { Username: ADMIN.name, Pw: ADMIN.password },
	});
	expect(
		response.ok(),
		response.status() === 500
			? `authentication failed with 500: the Jellyfin stack is in an unexpected state (leftover volumes from an interrupted run?). Try: docker compose -p <project> down -v. Body: ${await response.text()}`
			: `authentication failed: ${response.status()} ${await response.text()}`,
	).toBeTruthy();
	return (await response.json()).AccessToken as string;
}

export async function setPluginConfig(
	api: APIRequestContext,
	config: PluginConfig,
): Promise<void> {
	const current = await (
		await api.get(`/Plugins/${PLUGIN_ID}/Configuration`)
	).json();
	const response = await api.post(`/Plugins/${PLUGIN_ID}/Configuration`, {
		data: { ...current, ...config },
	});
	expect(
		response.ok(),
		`saving the plugin configuration failed: ${response.status()}`,
	).toBeTruthy();
}

export async function getPluginConfig(
	api: APIRequestContext,
): Promise<PluginConfig> {
	return (await api.get(`/Plugins/${PLUGIN_ID}/Configuration`)).json();
}

export async function chaosflixChannelId(
	api: APIRequestContext,
): Promise<string> {
	const channels = await (await api.get("/Channels")).json();
	const channel = channels.Items.find(
		(item: { Name: string }) => item.Name === "Chaosflix",
	);
	expect(channel, "the Chaosflix channel is not registered").toBeTruthy();
	return channel.Id as string;
}

export async function channelItems(api: APIRequestContext, folderId?: string) {
	const query = folderId ? `&folderId=${encodeURIComponent(folderId)}` : "";
	const channelId = await chaosflixChannelId(api);
	const response = await api.get(
		`/Channels/${channelId}/Items?userId=${await userId(api)}${query}`,
	);
	expect(
		response.ok(),
		`listing channel items failed: ${response.status()}`,
	).toBeTruthy();
	return (await response.json()).Items as Array<{
		Id: string;
		Name: string;
		Type: string;
	}>;
}

/** Starts a scheduled task by key and waits until it is idle again. */
export async function runScheduledTask(
	api: APIRequestContext,
	key: string,
): Promise<void> {
	const task = async () =>
		(
			(await (await api.get("/ScheduledTasks")).json()) as Array<{
				Key: string;
				Id: string;
				State: string;
			}>
		).find((t) => t.Key === key)!;

	const start = await api.post(`/ScheduledTasks/Running/${(await task()).Id}`);
	expect(start.ok(), `starting ${key} failed: ${start.status()}`).toBeTruthy();

	await expect
		.poll(async () => (await task()).State, { timeout: 60_000 })
		.toBe("Idle");
}

export async function serverId(api: APIRequestContext): Promise<string> {
	return (await (await api.get("/System/Info/Public")).json()).Id as string;
}

export async function talkNamed(api: APIRequestContext, name: string) {
	const byYear = (await channelItems(api)).find((i) =>
		i.Name.includes("Browse by Year"),
	)!;
	const years = await channelItems(api, byYear.Id);
	const conferences = await channelItems(api, years[0].Id);
	const talks = await channelItems(api, conferences[0].Id);
	return talks.find((t) => t.Name === name)!;
}

export async function playbackInfo(
	api: APIRequestContext,
	itemId: string,
	deviceProfile?: { MaxStreamingBitrate: number },
) {
	const user = await userId(api);
	const response = await api.post(
		`/Items/${itemId}/PlaybackInfo?userId=${user}`,
		{
			data: {
				UserId: user,
				AutoOpenLiveStream: false,
				...(deviceProfile
					? {
							DeviceProfile: deviceProfile,
							MaxStreamingBitrate: deviceProfile.MaxStreamingBitrate,
						}
					: {}),
			},
		},
	);
	expect(response.ok()).toBeTruthy();
	const body = await response.json();
	return { ...body.MediaSources[0], PlaySessionId: body.PlaySessionId };
}

/**
 * A plain movie library over the fixture videos the compose file mounts at
 * /media, so a test can compare a channel item with an ordinary library item on
 * the same server. Returns the first movie once the scan has produced one.
 */
export async function movieFromFile(api: APIRequestContext, file: string) {
	const folders = await (await api.get("/Library/VirtualFolders")).json();

	if (!folders.some((f: { Name: string }) => f.Name === MOVIE_LIBRARY)) {
		const created = await api.post(
			`/Library/VirtualFolders?name=${encodeURIComponent(MOVIE_LIBRARY)}` +
				"&collectionType=movies&paths=/media&refreshLibrary=true",
			{
				// Without this the scan asks TheMovieDb what these test patterns
				// are and renames them to whatever it matched, which makes the
				// items unfindable and the test dependent on the network.
				data: {
					LibraryOptions: {
						EnableInternetProviders: false,
						PathInfos: [{ Path: "/media" }],
					},
				},
			},
		);
		expect(
			created.ok(),
			`creating the movie library failed: ${created.status()} ${await created.text()}`,
		).toBeTruthy();
	}

	const user = await userId(api);
	// By path, not by name: the scan renames items from whatever metadata it
	// matched, and it does so after the item first appears.
	const movie = async () =>
		(
			(
				await (
					await api.get(
						`/Items?userId=${user}&includeItemTypes=Movie&recursive=true&fields=Path`,
					)
				).json()
			).Items as Array<{ Id: string; Name: string; Type: string; Path: string }>
		).find((item) => item.Path?.endsWith(file));

	await expect
		.poll(async () => !!(await movie()), { timeout: 120_000 })
		.toBeTruthy();

	return (await movie())!;
}

export const MOVIE_LIBRARY = "E2E Movies";

export async function userId(api: APIRequestContext): Promise<string> {
	const me = await (await api.get("/Users/Me")).json();
	return me.Id as string;
}

/**
 * Signs into the web client through its own login form. After a successful
 * login jellyfin-web stays on the login route, so the caller is moved to the
 * home page explicitly.
 */
export async function loginUi(page: Page): Promise<void> {
	await page.goto("/web/#/login.html");

	const name = page.locator("#txtManualName");
	await name.waitFor({ state: "visible", timeout: 30_000 });
	await name.fill(ADMIN.name);
	await page.locator("#txtManualPassword").fill(ADMIN.password);

	const [response] = await Promise.all([
		page.waitForResponse((r) => /authenticatebyname/i.test(r.url()), {
			timeout: 30_000,
		}),
		page
			.locator("form")
			.first()
			.evaluate((form: HTMLFormElement) => form.requestSubmit()),
	]);
	expect(response.status(), "login failed").toBe(200);

	await gotoAuthenticated(page, "/web/#/home.html");
}

/**
 * Opens a channel folder in the client's list view and waits until its cards
 * are on the page. The view renders them after the route itself, and a hash
 * route occasionally leaves the list empty, so the navigation is retried.
 */
export async function gotoList(
	page: Page,
	parentId: string,
	server: string,
): Promise<void> {
	const cards = page.locator(".card, .listItem");

	for (let attempt = 0; attempt < 3; attempt++) {
		await gotoAuthenticated(
			page,
			`/web/#/list?parentId=${parentId}&serverId=${server}`,
		);
		try {
			await expect(cards.first()).toBeVisible({ timeout: 20_000 });
			return;
		} catch {
			await page.reload();
		}
	}

	throw new Error(`list view for ${parentId} stayed empty`);
}

/**
 * Whether the web client is sitting on its login route. The route is the signal
 * rather than the markup: jellyfin-web 12.1 leaves the previous view in the DOM
 * after a hash navigation, so `#loginPage` matches twice and a locator on it
 * throws instead of answering (#75). The route spells the same on both versions
 * (10.11 `#/login.html`, 12.1 `#/login`).
 */
async function onLoginRoute(page: Page): Promise<boolean> {
	if (/#\/login/i.test(page.url())) {
		return true;
	}
	return page
		.locator("#txtManualName")
		.first()
		.isVisible()
		.catch(() => false);
}

/**
 * Navigates inside the web client. Right after signing in the client can bounce
 * back to the login route while it is still connecting, so the navigation is
 * retried until the login form is gone and stays gone.
 */
export async function gotoAuthenticated(
	page: Page,
	route: string,
): Promise<void> {
	for (let attempt = 0; attempt < 5; attempt++) {
		await page.goto(route);

		// Being off the login route once is not enough: the bounce happens after
		// the hash navigation has already taken effect.
		let settled = 0;
		for (let sample = 0; sample < 12 && settled < 3; sample++) {
			await page.waitForTimeout(500);
			settled = (await onLoginRoute(page)) ? 0 : settled + 1;
		}
		if (settled >= 3) {
			return;
		}
	}

	throw new Error(
		`web client kept redirecting to the login page instead of ${route}`,
	);
}

export type PlaySession = {
	ItemId: string;
	MediaSourceId: string;
	PlaySessionId: string;
	CanSeek: boolean;
};

/** Reports playback of an item the way a client does, from a position. */
export async function openSession(
	api: APIRequestContext,
	itemId: string,
	deviceProfile: { MaxStreamingBitrate: number },
	fromTicks: number,
): Promise<PlaySession> {
	const source = await playbackInfo(api, itemId, deviceProfile);
	const session = {
		ItemId: itemId,
		MediaSourceId: source.Id,
		PlaySessionId: source.PlaySessionId,
		CanSeek: true,
	};
	const started = await api.post("/Sessions/Playing", {
		data: { ...session, PositionTicks: fromTicks },
	});
	expect(started.ok(), `reporting playback start: ${started.status()}`).toBe(
		true,
	);
	return session;
}

export async function playUntil(
	api: APIRequestContext,
	session: PlaySession,
	positionTicks: number,
): Promise<void> {
	await api.post("/Sessions/Playing/Progress", {
		data: { ...session, PositionTicks: positionTicks, IsPaused: false },
	});
	await api.post("/Sessions/Playing/Stopped", {
		data: { ...session, PositionTicks: positionTicks },
	});
}

export async function userData(
	api: APIRequestContext,
	itemId: string,
): Promise<{ PlaybackPositionTicks?: number; Played?: boolean }> {
	const item = await (
		await api.get(`/Users/${await userId(api)}/Items/${itemId}`)
	).json();
	return item.UserData ?? {};
}

export async function resumePosition(
	api: APIRequestContext,
	itemId: string,
): Promise<number | undefined> {
	return (await userData(api, itemId)).PlaybackPositionTicks;
}

/** Drops played state and resume position, the way "mark unwatched" does. */
export async function clearWatchState(
	api: APIRequestContext,
	itemId: string,
): Promise<void> {
	const response = await api.delete(
		`/UserPlayedItems/${itemId}?userId=${await userId(api)}`,
	);
	expect(response.ok(), "clearing the watch state failed").toBe(true);
}
