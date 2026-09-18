import { expect, test } from "@playwright/test";
import {
	apiContext,
	FAKE_API,
	getPluginConfig,
	gotoAuthenticated,
	loginUi,
	PLUGIN_ID,
	setPluginConfig,
} from "../helpers/jellyfin";

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
		await loginUi(page);

		// The channel shows up in the client's navigation once the plugin is loaded.
		await expect(
			page.getByRole("link", { name: "Chaosflix" }).first(),
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

	test("settings page loads and saves the configuration", async ({ page }) => {
		const api = await apiContext();
		await setPluginConfig(api, {
			PreferredQuality: "High",
			PreferredLanguage: "",
			ApiBaseUrl: FAKE_API,
		});

		await loginUi(page);
		// The route the plugin's detail page in the dashboard links to.
		await gotoAuthenticated(page, "/web/#/configurationpage?name=Chaosflix");

		await expect(page.locator("#ApiBaseUrl")).toHaveValue(FAKE_API);
		await expect(page.locator("#PreferredQuality")).toHaveValue("High");
		await expect(page.locator("#PreferredLanguage")).toHaveValue("");

		await page.locator("#PreferredQuality").selectOption("Standard");
		await page.locator("#PreferredLanguage").selectOption("eng");
		await page.getByRole("button", { name: /save/i }).click();

		await expect
			.poll(async () => {
				const config = await getPluginConfig(api);
				return `${config.PreferredQuality}/${config.PreferredLanguage}/${config.ApiBaseUrl}`;
			})
			.toBe(`Standard/eng/${FAKE_API}`);

		// Later specs expect the defaults the global setup installed.
		await setPluginConfig(api, {
			PreferredQuality: "High",
			PreferredLanguage: "",
		});
	});
});
