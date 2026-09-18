// Deterministic stand-in for the media.ccc.de dataset used by the e2e tests.
// Recording URLs point at /cdn/... which 302-redirects to /mirror/..., mirroring
// how the real CDN hands clients off to a mirror.

// Must match the fixture videos built by scripts/build-artifacts.sh; the Android
// suite raises it because driving a real player through the UI needs more than
// five seconds of material.
const SECONDS = Number(process.env.FIXTURE_SECONDS || 5);

// Jellyfin keeps a resume position only for items longer than five minutes
// (MinResumeDurationSeconds), so the resume tests need a talk of their own that
// clears that threshold. Must match FIXTURE_LONG_SECONDS in build-artifacts.sh.
const LONG_SECONDS = Number(process.env.FIXTURE_LONG_SECONDS || 330);

const CONFERENCE = {
	acronym: "e2e-congress",
	title: "E2E Congress 2025",
	slug: "congress/2025",
	description: "Fixture conference for the end-to-end tests",
	logo_url: "http://fake-ccc:3000/static/logo.png",
	url: "http://fake-ccc:3000/public/conferences/e2e-congress",
	event_last_released_at: "2025-12-30T12:00:00Z",
};

function recording(
	folder,
	mimeType,
	file,
	{
		highQuality = true,
		language = "deu",
		width = 1920,
		seconds = SECONDS,
	} = {},
) {
	return {
		size: 1,
		length: seconds,
		mime_type: mimeType,
		language,
		filename: file,
		state: "released",
		folder,
		high_quality: highQuality,
		width,
		height: Math.round((width * 9) / 16),
		recording_url: `http://fake-ccc:3000/cdn/${file}`,
		url: `http://fake-ccc:3000/public/recordings/${file}`,
		updated_at: "2025-12-30T12:00:00Z",
	};
}

const EVENTS = [
	{
		guid: "e2e-0000-0000-0000-000000000001",
		title: "Three stream talk",
		subtitle: "Video, audio description and audio",
		slug: "three-stream-talk",
		description:
			"A talk whose MP4 carries an extra video stream for the visually impaired.",
		original_language: "deu",
		persons: ["Alice Hacker", "Bob Builder"],
		tags: ["Security", "2025", "Stage HUFF"],
		view_count: 4200,
		date: "2025-12-27T11:00:00Z",
		release_date: "2025-12-28T11:00:00Z",
		duration: SECONDS,
		thumb_url: "http://fake-ccc:3000/static/thumb.png",
		poster_url: "http://fake-ccc:3000/static/poster.png",
		frontend_link: "http://fake-ccc:3000/v/three-stream-talk",
		conference_title: CONFERENCE.title,
		recordings: [
			recording("h264-hd", "video/mp4", "three-stream.mp4"),
			recording("webm-hd", "video/webm", "three-stream.webm"),
			recording("h264-sd", "video/mp4", "two-stream.mp4", {
				highQuality: false,
				width: 1024,
			}),
			// media.ccc.de publishes a translation as a recording of its own, so
			// picking another language means picking another version (#69).
			recording("h264-hd-translated", "video/mp4", "two-stream.mp4", {
				language: "eng",
			}),
		],
		related: [{ event_guid: "e2e-0000-0000-0000-000000000002", weight: 9 }],
	},
	{
		guid: "e2e-0000-0000-0000-000000000002",
		title: "Two stream talk",
		slug: "two-stream-talk",
		description: "A plain video plus audio recording.",
		original_language: "eng",
		persons: ["Carol Coder"],
		tags: ["Ethics"],
		view_count: 150,
		date: "2025-12-28T15:00:00Z",
		release_date: "2025-12-29T15:00:00Z",
		duration: SECONDS,
		thumb_url: "http://fake-ccc:3000/static/thumb.png",
		conference_title: CONFERENCE.title,
		recordings: [
			recording("h264-hd", "video/mp4", "two-stream.mp4", { language: "eng" }),
			recording("webm-hd", "video/webm", "two-stream.webm", {
				language: "eng",
			}),
		],
		related: [],
	},
	{
		guid: "e2e-0000-0000-0000-000000000003",
		title: "Long talk",
		slug: "long-talk",
		description:
			"A talk long enough for Jellyfin to keep a resume position for it.",
		original_language: "eng",
		persons: ["Dave Debugger"],
		tags: ["Science"],
		// Below the 100 views the Recommended folder asks for, so this talk does
		// not reshuffle the existing ordering assertions.
		view_count: 42,
		date: "2025-12-29T09:00:00Z",
		release_date: "2025-12-29T18:00:00Z",
		duration: LONG_SECONDS,
		thumb_url: "http://fake-ccc:3000/static/thumb.png",
		conference_title: CONFERENCE.title,
		// MP4 only: a WebM of this length costs far more to encode than the
		// resume tests get out of it, and they never decode the video.
		recordings: [
			recording("h264-hd", "video/mp4", "long-talk.mp4", {
				language: "eng",
				seconds: LONG_SECONDS,
			}),
		],
		related: [],
	},
];

// The list endpoint omits per-event detail, exactly like the real API.
const listEvent = (event) => {
	const { recordings, related, ...rest } = event;
	return rest;
};

module.exports = {
	CONFERENCE,
	EVENTS,
	conferenceList: () => ({
		conferences: [{ ...CONFERENCE, events: undefined }],
	}),
	conferenceDetail: () => ({ ...CONFERENCE, events: EVENTS.map(listEvent) }),
	event: (guid) => EVENTS.find((e) => e.guid === guid),
	search: (query) => ({
		events: EVENTS.filter((e) =>
			e.title.toLowerCase().includes(String(query || "").toLowerCase()),
		).map(listEvent),
	}),
};
