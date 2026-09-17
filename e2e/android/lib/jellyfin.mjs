// Minimal Jellyfin API client for the Android suite. Deliberately dependency
// free: the Android path must work without running `npm ci` in e2e/.

export const ADMIN = { name: "e2e-admin", password: "chaosflix-e2e" };
export const PLUGIN_ID = "c4a05f11-4ccc-4b00-bdea-dbeef1337000";
export const FAKE_API = "http://fake-ccc:3000/public";

const AUTH =
	'MediaBrowser Client="chaosflix-android-e2e", Device="runner", DeviceId="chaosflix-android-e2e", Version="1.0.0"';

export const baseUrl =
	process.env.JELLYFIN_URL ??
	`http://127.0.0.1:${process.env.JELLYFIN_PORT ?? "8098"}`;

export function api(token) {
	const headers = {
		Authorization: token ? `${AUTH}, Token="${token}"` : AUTH,
		"Content-Type": "application/json",
	};
	return async (path, init = {}) => {
		const response = await fetch(`${baseUrl}${path}`, {
			...init,
			headers: { ...headers, ...(init.headers ?? {}) },
			body: init.json === undefined ? init.body : JSON.stringify(init.json),
		});
		return response;
	};
}

export async function json(response, what) {
	if (!response.ok) {
		throw new Error(
			`${what} failed: ${response.status} ${await response.text()}`,
		);
	}
	const text = await response.text();
	return text ? JSON.parse(text) : null;
}

export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export async function waitFor(
	label,
	predicate,
	{ timeoutMs = 120_000, intervalMs = 2000 } = {},
) {
	const deadline = Date.now() + timeoutMs;
	let last;
	while (Date.now() < deadline) {
		try {
			const value = await predicate();
			if (value) {
				return value;
			}
			last = value;
		} catch (error) {
			last = error.message;
		}
		await sleep(intervalMs);
	}
	throw new Error(
		`timed out waiting for ${label} (last: ${JSON.stringify(last)})`,
	);
}
