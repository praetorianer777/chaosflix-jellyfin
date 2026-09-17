import { defineConfig, devices } from "@playwright/test";

export const JELLYFIN_PORT = process.env.JELLYFIN_PORT ?? "8097";
export const JELLYFIN_URL = `http://127.0.0.1:${JELLYFIN_PORT}`;

export default defineConfig({
	testDir: "./tests",
	globalSetup: "./global-setup.ts",
	globalTeardown: "./global-teardown.ts",
	// The stack is shared state (one Jellyfin, one library); tests run in order.
	workers: 1,
	fullyParallel: false,
	forbidOnly: !!process.env.CI,
	retries: process.env.CI ? 1 : 0,
	timeout: 90_000,
	expect: { timeout: 20_000 },
	reporter: process.env.CI
		? [["github"], ["html", { open: "never" }]]
		: [["list"]],
	use: {
		baseURL: JELLYFIN_URL,
		trace: "retain-on-failure",
		video: "retain-on-failure",
		screenshot: "only-on-failure",
	},
	projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
