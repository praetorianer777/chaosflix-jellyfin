// Watches the Jellyfin sessions while the Maestro flow drives the app and
// asserts what a browser test cannot see: that the Android client really plays
// the proxied recording, with the right audio stream, without Jellyfin
// re-encoding anything, and that a seek reaches the server.
//
// Runs in parallel with the flow and exits as soon as it has seen everything,
// so the assertions describe a session that is genuinely live.
//
// On "no transcoding": jellyfin-android 2.7 asks for DirectPlay first and falls
// back to HLS when the server answers SupportsDirectPlay=false, which it does
// for every channel item. The session then reports PlayMethod=Transcode while
// ffmpeg only remuxes (IsVideoDirect and IsAudioDirect both true). Re-encoding
// is the regression worth catching, so that is what is asserted.

import fs from "node:fs";
import path from "node:path";
import { api, json, sleep } from "./lib/jellyfin.mjs";

const token = (
	process.env.JELLYFIN_TOKEN ??
	fs.readFileSync(path.join(import.meta.dirname, ".artifacts", "token"), "utf8")
).trim();
const admin = api(token);
const timeoutMs = Number(process.env.ANDROID_E2E_SESSION_TIMEOUT ?? 420_000);
const POLL_MS = 1000;
// A jump this large cannot come from one second of ordinary playback.
const SEEK_TICKS = 50_000_000;

const isAndroidApp = (session) =>
	/android/i.test(String(session.Client ?? "")) &&
	session.Client !== "chaosflix-android-e2e";

const observations = [];
let best = null;
let seekSeen = false;
let previousPosition = null;

const deadline = Date.now() + timeoutMs;
while (Date.now() < deadline && !(best?.PlayState?.PlayMethod && seekSeen)) {
	let sessions = [];
	try {
		sessions = await json(await admin("/Sessions"), "listing sessions");
	} catch (error) {
		console.error(`[warn] ${error.message}`);
	}

	const playing = sessions.find(
		(session) => session.NowPlayingItem && isAndroidApp(session),
	);
	if (playing) {
		const state = playing.PlayState ?? {};
		observations.push({
			at: new Date().toISOString(),
			item: playing.NowPlayingItem.Name,
			playMethod: state.PlayMethod,
			audioStreamIndex: state.AudioStreamIndex,
			positionTicks: state.PositionTicks,
			transcodeReasons: playing.TranscodingInfo?.TranscodeReasons,
			videoDirect: playing.TranscodingInfo?.IsVideoDirect,
			audioDirect: playing.TranscodingInfo?.IsAudioDirect,
		});

		if (state.PlayMethod) {
			best = playing;
		}
		if (typeof state.PositionTicks === "number") {
			if (
				previousPosition !== null &&
				state.PositionTicks - previousPosition > SEEK_TICKS
			) {
				seekSeen = true;
			}
			previousPosition = state.PositionTicks;
		}
	}

	await sleep(POLL_MS);
}

const problems = [];
if (!best) {
	problems.push(
		"no session of the Jellyfin Android app ever reported a playing item",
	);
} else {
	const state = best.PlayState ?? {};
	const transcoding = best.TranscodingInfo;
	const item = best.NowPlayingItem ?? {};
	// /Sessions never inlines the media sources of a channel item, so the stream
	// layout is fetched the same way a client would.
	let sources = item.MediaSources ?? [];
	if (sources.length === 0 && item.Id) {
		try {
			const user = await json(
				await admin("/Users/Me"),
				"reading the admin user",
			);
			const info = await json(
				await admin(`/Items/${item.Id}/PlaybackInfo?userId=${user.Id}`, {
					method: "POST",
					json: { UserId: user.Id, AutoOpenLiveStream: false },
				}),
				"reading playback info",
			);
			sources = info?.MediaSources ?? [];
		} catch (error) {
			console.error(`[warn] ${error.message}`);
		}
	}
	const source =
		sources.find((s) => s.Id === state.MediaSourceId) ?? sources[0];

	if (
		transcoding &&
		!(transcoding.IsVideoDirect && transcoding.IsAudioDirect)
	) {
		problems.push(
			`Jellyfin is re-encoding the recording (video direct: ${transcoding.IsVideoDirect}, ` +
				`audio direct: ${transcoding.IsAudioDirect}, reasons: ${JSON.stringify(transcoding.TranscodeReasons)})`,
		);
	}
	if (state.AudioStreamIndex === undefined || state.AudioStreamIndex === null) {
		problems.push(
			"the session reports no audio stream — the player decoded video only",
		);
	} else if (source) {
		const audio = (source.MediaStreams ?? []).filter(
			(stream) => stream.Type === "Audio",
		);
		if (audio.length === 0) {
			problems.push(
				"the played media source exposes no audio stream (the v0.0.28 regression)",
			);
		} else if (
			!audio.some((stream) => stream.Index === state.AudioStreamIndex)
		) {
			problems.push(
				`AudioStreamIndex ${state.AudioStreamIndex} does not point at an audio stream ` +
					`(audio streams: ${audio.map((s) => s.Index).join(", ")})`,
			);
		}
	}
	if (
		source?.Path &&
		!String(source.Path).includes("/api/ChaosflixStream/proxy/")
	) {
		problems.push(`the media source is not the plugin proxy: ${source.Path}`);
	}
	if (!seekSeen) {
		problems.push(
			"the reported position never jumped, so the seek did not reach the server",
		);
	}
}

console.log(
	JSON.stringify({ observations: observations.slice(-25), problems }, null, 2),
);
if (problems.length > 0) {
	console.error(`\n${problems.length} problem(s):\n- ${problems.join("\n- ")}`);
	process.exit(1);
}
console.log(
	"\nThe Android session streamed the proxied recording without re-encoding, with audio, and the seek was observed.",
);
