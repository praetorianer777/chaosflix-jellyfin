import { expect, test } from "@playwright/test";
import {
	apiContext,
	chaosflixChannelId,
	FAKE_API,
	getPluginConfig,
	gotoAuthenticated,
	loginUi,
	PLUGIN_ID,
	runScheduledTask,
	setPluginConfig,
} from "../helpers/jellyfin";

/** A talk the fake CCC API serves; see e2e/fake-ccc/fixtures.js. */
const TALK_GUID = "e2e-0000-0000-0000-000000000001";

test.describe("plugin installation", () => {
	test("is loaded and active", async () => {
		const api = await apiContext();

		const plugins = await (await api.get("/Plugins")).json();
		const chaosflix = plugins.find(
			(p: { Id: string }) => p.Id === PLUGIN_ID.replace(/-/g, ""),
		);

		expect(
			chaosflix,
			`Chaosflix missing from ${plugins.map((p: { Name: string }) => p.Name).join(", ")}`,
		).toBeTruthy();
		expect(chaosflix.Status).toBe("Active");
	});

	test("web client lists the Chaosflix channel", async ({ page }) => {
		const api = await apiContext();
		const channelId = await chaosflixChannelId(api);

		await loginUi(page);

		// The channel shows up in the client's navigation once the plugin is
		// loaded. Matched by accessible name and by the id the link points at, so
		// the assertion does not depend on where a given jellyfin-web version puts
		// the entry or what it wraps it in.
		await expect(
			page
				.getByRole("link", { name: "Chaosflix" })
				.and(page.locator(`[href*="${channelId}"]`))
				.first(),
		).toBeVisible({ timeout: 30_000 });
	});

	test("sync task runs to completion", async () => {
		const api = await apiContext();

		const all = await (await api.get("/ScheduledTasks")).json();
		const sync = all.find((t: { Key: string }) => t.Key === "ChaosflixSync");
		const start = await api.post(`/ScheduledTasks/Running/${sync.Id}`);
		expect(start.ok()).toBeTruthy();

		await expect
			.poll(
				async () => {
					const tasks = await (await api.get("/ScheduledTasks")).json();
					return tasks.find((t: { Key: string }) => t.Key === "ChaosflixSync")
						.State;
				},
				{ timeout: 60_000 },
			)
			.toBe("Idle");

		const tasks = await (await api.get("/ScheduledTasks")).json();
		const task = tasks.find((t: { Key: string }) => t.Key === "ChaosflixSync");
		expect(task.LastExecutionResult.Status).toBe("Completed");
	});

	test("configuration survives a round trip through the API", async () => {
		const api = await apiContext();

		await setPluginConfig(api, {
			PreferredQuality: "Standard",
			PreferredLanguage: "eng",
		});
		expect(await getPluginConfig(api)).toMatchObject({
			PreferredQuality: "Standard",
			PreferredLanguage: "eng",
			ApiBaseUrl: FAKE_API,
		});

		await setPluginConfig(api, {
			PreferredQuality: "High",
			PreferredLanguage: "",
		});
	});

	test("status endpoint reports the plugin's state", async () => {
		const api = await apiContext();
		await setPluginConfig(api, { ApiBaseUrl: FAKE_API });

		const status = await (await api.get("/api/ChaosflixStatus")).json();
		expect(status.ApiBaseUrl).toBe(FAKE_API);
		expect(status.ApiBaseUrlIsDefault).toBe(false);
		expect(status.Sync.Registered).toBe(true);
		expect(status.ApiCache.Capacity).toBeGreaterThan(0);
		expect(status.ProbeCache.Capacity).toBeGreaterThan(0);

		const check = await (
			await api.post("/api/ChaosflixStatus/CheckApi")
		).json();
		expect(check.Ok, `the fake API did not answer: ${check.Error}`).toBe(true);
		expect(check.Url).toBe(`${FAKE_API}/conferences`);
		expect(check.StatusCode).toBe(200);
		expect(check.ConferenceCount).toBeGreaterThan(0);
		expect(check.ElapsedMs).toBeGreaterThanOrEqual(0);

		// The sync task fills the conference cache; clearing empties it again.
		await runScheduledTask(api, "ChaosflixSync");
		const filled = await (await api.get("/api/ChaosflixStatus")).json();
		expect(filled.ApiCache.Conferences).toBeGreaterThan(0);
		expect(filled.Sync.LastResult).toBe("Completed");
		expect(filled.Sync.DurationSeconds).toBeGreaterThanOrEqual(0);

		const cleared = await (
			await api.post("/api/ChaosflixStatus/ClearCaches")
		).json();
		expect(cleared.ApiCache.Entries).toBe(0);
		expect(cleared.ProbeCache.Entries).toBe(0);
	});

	test("the talk check resolves a talk end to end without leaking a signature", async () => {
		const api = await apiContext();

		const response = await api.post(
			`/api/ChaosflixStatus/CheckTalk?id=${encodeURIComponent(TALK_GUID)}`,
		);
		expect(response.ok()).toBeTruthy();
		const body = await response.text();
		const result = JSON.parse(body);

		expect(
			result.Ok,
			`failed at ${result.FailedStage}: ${JSON.stringify(result.Stages)}`,
		).toBe(true);
		expect(result.Stages.map((s: { Name: string }) => s.Name)).toEqual([
			"API lookup",
			"Recording choice",
			"Signed proxy url",
			"Proxy",
			"Probe",
		]);

		// The signature is what keeps the proxy from being a general purpose relay,
		// so nothing the page can render may carry a usable one.
		const proxyUrl = result.Stages.find(
			(s: { Name: string }) => s.Name === "Signed proxy url",
		).Detail;
		expect(proxyUrl).toContain("t=<redacted>");
		expect(body).not.toMatch(/[?&]t=[0-9a-f]{64}/);
	});

	test("the status endpoint refuses an unauthenticated caller", async () => {
		const anonymous = await apiContext("");

		expect((await anonymous.get("/api/ChaosflixStatus")).status()).toBe(401);
		expect(
			(await anonymous.post("/api/ChaosflixStatus/ClearCaches")).status(),
		).toBe(401);
	});

	test("settings page shows the plugin's state", async ({ page }) => {
		const api = await apiContext();
		await setPluginConfig(api, { ApiBaseUrl: FAKE_API });

		await loginUi(page);
		await gotoAuthenticated(page, "/web/#/configurationpage?name=Chaosflix");

		const status = page.locator("#ChaosflixStatus");
		await expect(status).toContainText(FAKE_API, { timeout: 30_000 });
		await expect(status).toContainText("API cache");
		await expect(status).toContainText("Probe cache");

		await page.getByRole("button", { name: /check endpoint now/i }).click();
		await expect(page.locator("#ChaosflixApiCheck")).toContainText("Reachable");
		await expect(page.locator("#ChaosflixApiCheck")).toContainText("ms");

		await page.locator("#ChaosflixTalkId").fill(TALK_GUID);
		await page.getByRole("button", { name: /check this talk/i }).click();
		await expect(page.locator("#ChaosflixTalkCheck")).toContainText(
			"resolved end to end",
			{ timeout: 60_000 },
		);
		await expect(page.locator("#ChaosflixTalkCheck")).not.toContainText(
			/[?&]t=[0-9a-f]{64}/,
		);
	});

	test("settings page loads and saves the configuration", async ({ page }) => {
		const api = await apiContext();
		await setPluginConfig(api, {
			PreferredQuality: "High",
			PreferredLanguage: "",
			ConferenceFilter: "",
			ApiBaseUrl: FAKE_API,
		});

		await loginUi(page);
		// The route the plugin's detail page in the dashboard links to.
		await gotoAuthenticated(page, "/web/#/configurationpage?name=Chaosflix");

		await expect(page.locator("#ApiBaseUrl")).toHaveValue(FAKE_API);
		await expect(page.locator("#PreferredQuality")).toHaveValue("High");
		await expect(page.locator("#PreferredLanguage")).toHaveValue("");
		await expect(page.locator("#ConferenceFilter")).toHaveValue("");

		await page.locator("#PreferredQuality").selectOption("Standard");
		await page.locator("#PreferredLanguage").selectOption("eng");
		await page.locator("#ConferenceFilter").fill("congress, 38c3");
		await page.getByRole("button", { name: /save/i }).click();

		await expect
			.poll(async () => {
				const config = await getPluginConfig(api);
				return `${config.PreferredQuality}/${config.PreferredLanguage}/${config.ConferenceFilter}/${config.ApiBaseUrl}`;
			})
			.toBe(`Standard/eng/congress, 38c3/${FAKE_API}`);

		// Later specs expect the defaults the global setup installed — the filter
		// above would hide the fixture conference from every one of them.
		await setPluginConfig(api, {
			PreferredQuality: "High",
			PreferredLanguage: "",
			ConferenceFilter: "",
		});
	});
});
