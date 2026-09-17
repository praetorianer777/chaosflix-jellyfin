import fs from "node:fs";
import path from "node:path";
import { APIRequestContext, Page, expect, request } from "@playwright/test";
import { JELLYFIN_URL } from "../playwright.config";

export const ADMIN = { name: "e2e-admin", password: "chaosflix-e2e" };
export const PLUGIN_ID = "c4a05f11-4ccc-4b00-bdea-dbeef1337000";
export const FAKE_API = "http://fake-ccc:3000/public";
export const CONFERENCE_TITLE = "E2E Congress 2025";

const AUTH_HEADER =
	'MediaBrowser Client="chaosflix-e2e", Device="playwright", DeviceId="chaosflix-e2e", Version="1.0.0"';

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

export async function apiContext(token: string = readTokenIfPresent()): Promise<APIRequestContext> {
	return request.newContext({
		baseURL: JELLYFIN_URL,
		extraHTTPHeaders: {
			Authorization: token ? `${AUTH_HEADER}, Token="${token}"` : AUTH_HEADER,
		},
	});
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
		`authentication failed: ${response.status()} ${await response.text()}`,
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
		((await (await api.get("/ScheduledTasks")).json()) as Array<{
			Key: string;
			Id: string;
			State: string;
		}>).find((t) => t.Key === key)!;

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

export async function playbackInfo(api: APIRequestContext, itemId: string) {
	const user = await userId(api);
	const response = await api.post(
		`/Items/${itemId}/PlaybackInfo?userId=${user}`,
		{
			data: { UserId: user, AutoOpenLiveStream: false },
		},
	);
	expect(response.ok()).toBeTruthy();
	const body = await response.json();
	return { ...body.MediaSources[0], PlaySessionId: body.PlaySessionId };
}

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
		page.waitForResponse((r) => /authenticatebyname/i.test(r.url()), { timeout: 30_000 }),
		page.locator("form").first().evaluate((form: HTMLFormElement) => form.requestSubmit()),
	]);
	expect(response.status(), "login failed").toBe(200);

	await gotoAuthenticated(page, "/web/#/home.html");
}

/**
 * Navigates inside the web client. Right after signing in the client can bounce
 * back to the login route while it is still connecting, so the navigation is
 * retried until the login form is gone.
 */
export async function gotoAuthenticated(page: Page, route: string): Promise<void> {
	for (let attempt = 0; attempt < 5; attempt++) {
		await page.goto(route);
		await page.waitForTimeout(1500);
		const onLoginPage = await page
			.locator("#loginPage")
			.isVisible()
			.catch(() => false);
		if (!onLoginPage) {
			return;
		}
	}

	throw new Error(`web client kept redirecting to the login page instead of ${route}`);
}
