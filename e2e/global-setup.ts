import { execFileSync } from "node:child_process";
import path from "node:path";
import {
	apiContext,
	authenticate,
	chaosflixChannelId,
	completeStartupWizard,
	FAKE_API,
	saveToken,
	setPluginConfig,
	waitForJellyfin,
} from "./helpers/jellyfin";

const here = __dirname;
const run = (command: string, args: string[]) =>
	execFileSync(command, args, {
		cwd: here,
		stdio: "inherit",
		env: process.env,
	});

export default async function globalSetup() {
	if (!process.env.E2E_REUSE_STACK) {
		run(path.join(here, "scripts/build-artifacts.sh"), []);
		// A stack left behind by an interrupted run keeps its volumes, and a
		// half-initialised Jellyfin then fails the suite for reasons that have
		// nothing to do with the code under test — which blocks every push,
		// because the gate runs this (#46).
		run("docker", ["compose", "down", "-v"]);
		run("docker", ["compose", "up", "-d", "--wait"]);
	}

	const anonymous = await apiContext();
	await waitForJellyfin(anonymous);
	await completeStartupWizard(anonymous);

	const token = await authenticate(anonymous);
	saveToken(token);

	const api = await apiContext(token);
	// Production defaults, so the tests see what users see; the playback spec
	// switches to WebM for a talk of its own, because Chromium in Playwright
	// does not play H.264.
	await setPluginConfig(api, {
		ApiBaseUrl: FAKE_API,
		PreferredFormat: "Mp4",
		PreferredQuality: "High",
		PreferredLanguage: "",
	});
	// Fail fast and loudly if the plugin did not load at all.
	await chaosflixChannelId(api);
}
