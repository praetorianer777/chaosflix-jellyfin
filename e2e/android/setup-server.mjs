// Brings the Jellyfin instance into the state the Maestro flows expect:
// wizard completed, admin user created, plugin pointed at the fake CCC API.
// Prints the admin token so the shell can hand it on to verify-session.mjs.

import fs from "node:fs";
import path from "node:path";
import {
	ADMIN,
	api,
	baseUrl,
	FAKE_API,
	json,
	PLUGIN_ID,
	waitFor,
} from "./lib/jellyfin.mjs";

const anonymous = api();

await waitFor(
	`Jellyfin at ${baseUrl}`,
	async () => (await anonymous("/System/Info/Public")).ok,
	{ timeoutMs: 180_000 },
);

const info = await json(await anonymous("/System/Info/Public"), "system info");
if (!info.StartupWizardCompleted) {
	await anonymous("/Startup/Configuration", {
		method: "POST",
		json: {
			UICulture: "en-US",
			MetadataCountryCode: "DE",
			PreferredMetadataLanguage: "en",
		},
	});
	await anonymous("/Startup/User");
	await anonymous("/Startup/User", {
		method: "POST",
		json: { Name: ADMIN.name, Password: ADMIN.password },
	});
	await anonymous("/Startup/RemoteAccess", {
		method: "POST",
		json: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false },
	});
	await anonymous("/Startup/Complete", { method: "POST" });
}

const auth = await json(
	await anonymous("/Users/AuthenticateByName", {
		method: "POST",
		json: { Username: ADMIN.name, Pw: ADMIN.password },
	}),
	"authentication",
);

const admin = api(auth.AccessToken);
const current = await json(
	await admin(`/Plugins/${PLUGIN_ID}/Configuration`),
	"plugin configuration",
);
await json(
	await admin(`/Plugins/${PLUGIN_ID}/Configuration`, {
		method: "POST",
		// MP4/H.264 is what the Android client actually gets in production, and
		// it is the combination ExoPlayer direct-streams.
		json: {
			...current,
			ApiBaseUrl: FAKE_API,
			PreferredFormat: "Mp4",
			PreferredQuality: "High",
			PreferredLanguage: "",
		},
	}),
	"saving the plugin configuration",
);

const channels = await json(await admin("/Channels"), "listing channels");
const channel = channels.Items.find((item) => item.Name === "Chaosflix");
if (!channel) {
	throw new Error(
		"the Chaosflix channel is not registered — did the plugin load?",
	);
}

const artifacts = path.join(import.meta.dirname, ".artifacts");
fs.mkdirSync(artifacts, { recursive: true });
fs.writeFileSync(path.join(artifacts, "token"), auth.AccessToken);
console.log(auth.AccessToken);
