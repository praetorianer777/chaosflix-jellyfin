import { execFileSync } from "node:child_process";

export default async function globalTeardown() {
	if (process.env.E2E_REUSE_STACK || process.env.E2E_KEEP_STACK) {
		return;
	}

	execFileSync("docker", ["compose", "down", "-v"], {
		cwd: __dirname,
		stdio: "inherit",
	});
}
