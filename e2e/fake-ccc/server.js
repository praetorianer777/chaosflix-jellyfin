// Fake media.ccc.de: JSON API, a CDN that redirects to a mirror, and byte-range
// capable media delivery. Files come from /media (built by scripts/build-artifacts.sh).

const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const fixtures = require("./fixtures");

const PORT = Number(process.env.PORT || 3000);
const MEDIA_DIR = process.env.MEDIA_DIR || "/media";

const MIME = {
	".mp4": "video/mp4",
	".webm": "video/webm",
	".png": "image/png",
};

function json(res, body, status = 200) {
	const payload = JSON.stringify(body);
	res.writeHead(status, {
		"Content-Type": "application/json",
		"Content-Length": Buffer.byteLength(payload),
	});
	res.end(payload);
}

function serveMedia(req, res, name) {
	const file = path.join(MEDIA_DIR, path.basename(name));
	let stat;
	try {
		stat = fs.statSync(file);
	} catch {
		res.writeHead(404).end();
		return;
	}

	const type = MIME[path.extname(file)] || "application/octet-stream";
	const range = /^bytes=(\d*)-(\d*)$/.exec(req.headers.range || "");
	let start = 0;
	let end = stat.size - 1;
	let status = 200;
	const headers = { "Content-Type": type, "Accept-Ranges": "bytes" };

	if (range && (range[1] || range[2])) {
		start = range[1] ? Number(range[1]) : stat.size - Number(range[2]);
		end = range[1] && range[2] ? Math.min(Number(range[2]), end) : end;
		if (start > end || start >= stat.size) {
			res.writeHead(416, { "Content-Range": `bytes */${stat.size}` }).end();
			return;
		}

		status = 206;
		headers["Content-Range"] = `bytes ${start}-${end}/${stat.size}`;
	}

	headers["Content-Length"] = end - start + 1;
	res.writeHead(status, headers);

	if (req.method === "HEAD") {
		res.end();
		return;
	}

	fs.createReadStream(file, { start, end }).pipe(res);
}

const server = http.createServer((req, res) => {
	const url = new URL(req.url, `http://${req.headers.host}`);
	const segments = url.pathname.split("/").filter(Boolean);
	console.log(`${req.method} ${req.url}`);

	if (url.pathname === "/public/conferences") {
		return json(res, fixtures.conferenceList());
	}

	if (
		segments[0] === "public" &&
		segments[1] === "conferences" &&
		segments[2]
	) {
		return segments[2] === fixtures.CONFERENCE.acronym ||
			segments[2] === fixtures.ARCHIVE.acronym
			? json(res, fixtures.conferenceDetail(segments[2]))
			: json(res, { error: "not found" }, 404);
	}

	if (url.pathname === "/public/events/search") {
		return json(res, fixtures.search(url.searchParams.get("q")));
	}

	if (segments[0] === "public" && segments[1] === "events" && segments[2]) {
		const event = fixtures.event(segments[2]);
		return event ? json(res, event) : json(res, { error: "not found" }, 404);
	}

	// The real CDN hands out a 302 to whichever mirror is closest.
	if (segments[0] === "cdn" && segments[1]) {
		res.writeHead(302, { Location: `/mirror/${segments[1]}` }).end();
		return;
	}

	if (segments[0] === "mirror" && segments[1]) {
		return serveMedia(req, res, segments[1]);
	}

	if (segments[0] === "static" && segments[1]) {
		return serveMedia(req, res, segments[1]);
	}

	if (url.pathname === "/health") {
		return json(res, { ok: true });
	}

	res.writeHead(404).end();
});

server.listen(PORT, () =>
	console.log(`fake-ccc listening on ${PORT}, media from ${MEDIA_DIR}`),
);
