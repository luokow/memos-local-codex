import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

import { acquireStartLock } from "./model-service.mjs";

const execFileAsync = promisify(execFile);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const project = path.join(root, "local-chat", "tests", "QwenLocalChat.Tests", "QwenLocalChat.Tests.csproj");
const assembly = path.join(root, "local-chat", "tests", "QwenLocalChat.Tests", "bin", "Release", "net9.0", "QwenLocalChat.Tests.dll");
const runtime = await fs.mkdtemp(path.join(os.tmpdir(), "memos-cross-language-lock-"));
const lockPath = path.join(runtime, "model-service-start.lock");
const markerPath = path.join(runtime, "model-started.marker");

function childResult(child) {
  return new Promise((resolve, reject) => {
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (chunk) => { stdout += chunk; });
    child.stderr.on("data", (chunk) => { stderr += chunk; });
    child.once("error", reject);
    child.once("exit", (code) => resolve({ code, stdout: stdout.trim(), stderr: stderr.trim() }));
  });
}

try {
  await execFileAsync("dotnet", ["build", project, "-c", "Release", "--nologo"], { cwd: root, windowsHide: true });
  await fs.writeFile(lockPath, JSON.stringify({
    schema_version: 1,
    owner_pid: 2147483647,
    owner_client: "qwen-local",
    acquired_at: "2026-08-05T00:00:00Z",
    config_sha256: "stale",
  }));
  await fs.writeFile(`${lockPath}.recovery`, JSON.stringify({
    schema_version: 1,
    owner_pid: 2147483647,
    owner_client: "memos-codex",
    acquired_at: "2026-08-05T00:00:00Z",
    config_sha256: "stale-recovery",
  }));

  const startAt = Date.now() + 750;
  const csharp = spawn("dotnet", [assembly, "--model-lock-helper", lockPath, markerPath, String(startAt), "400"], {
    cwd: root,
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
  });
  const csharpResult = childResult(csharp);
  const nodeResult = (async () => {
    const delay = startAt - Date.now();
    if (delay > 0) await new Promise((resolve) => setTimeout(resolve, delay));
    const lease = await acquireStartLock({
      lockPath,
      ownerClient: "memos-codex",
      configSha256: "cross-language-fixture",
      timeoutMs: 10_000,
      serviceHealthy: async () => fs.access(markerPath).then(() => true, () => false),
    });
    if (lease === null) return "REUSED";
    try {
      const marker = await fs.open(markerPath, "wx");
      try { await marker.writeFile("1"); await marker.sync(); } finally { await marker.close(); }
      await new Promise((resolve) => setTimeout(resolve, 400));
      return "OWNER";
    } finally {
      await lease.release();
    }
  })();

  const [nodeRole, csharpOutcome] = await Promise.all([nodeResult, csharpResult]);
  assert.equal(csharpOutcome.code, 0, csharpOutcome.stderr || "C# lock helper failed");
  const roles = [nodeRole, csharpOutcome.stdout].sort();
  assert.deepEqual(roles, ["OWNER", "REUSED"], `expected one owner and one reuse, got ${roles.join(", ")}`);
  console.log(JSON.stringify({ ok: true, nodeRole, csharpRole: csharpOutcome.stdout, ownerCount: roles.filter((role) => role === "OWNER").length }));
} finally {
  await fs.rm(runtime, { recursive: true, force: true });
}
