import assert from "node:assert/strict";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  acquireStartLock,
  appendLifecycleEvent,
  buildLaunchArguments,
  canonicalizeConfig,
  ensureModelService,
  loadModelServiceConfig,
  retireRecoveryGuard,
} from "./model-service.mjs";

const fixture = {
  schema_version: 1,
  bind_host: "127.0.0.1",
  port: 18135,
  server_executable: "llama\\bin\\llama-server.exe",
  model_path: "models\\model.gguf",
  model_alias: "fixture-model",
  context_size: 8192,
  gpu_layers: 99,
  parallel_slots: 2,
  reasoning_enabled: true,
  use_jinja: true,
  startup_timeout_seconds: 180,
  auto_start_on_demand: true,
};

test("shared config matches the C# canonical contract", () => {
  assert.equal(
    canonicalizeConfig(fixture),
    "{\"auto_start_on_demand\":true,\"bind_host\":\"127.0.0.1\",\"context_size\":8192,\"gpu_layers\":99,\"model_alias\":\"fixture-model\",\"model_path\":\"models\\\\model.gguf\",\"parallel_slots\":2,\"port\":18135,\"reasoning_enabled\":true,\"schema_version\":1,\"server_executable\":\"llama\\\\bin\\\\llama-server.exe\",\"startup_timeout_seconds\":180,\"use_jinja\":true}",
  );
});

test("shared config rejects public bind hosts and unknown fields", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "memos-model-config-"));
  try {
    await fs.mkdir(path.join(root, "llama", "bin"), { recursive: true });
    await fs.mkdir(path.join(root, "models"), { recursive: true });
    await fs.writeFile(path.join(root, "llama", "bin", "llama-server.exe"), "fixture");
    await fs.writeFile(path.join(root, "models", "model.gguf"), "fixture");
    const configPath = path.join(root, "model-service.json");
    await fs.writeFile(configPath, JSON.stringify({ ...fixture, bind_host: "0.0.0.0", extra: true }));
    await assert.rejects(
      loadModelServiceConfig({ root, configPath }),
      /unknown field.*extra|未知字段.*extra/i,
    );
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("launch arguments come entirely from shared config", () => {
  assert.deepEqual(buildLaunchArguments(fixture, "C:\\root"), [
    "--model", "C:\\root\\models\\model.gguf",
    "--alias", "fixture-model",
    "--host", "127.0.0.1",
    "--port", "18135",
    "--ctx-size", "8192",
    "--n-gpu-layers", "99",
    "--reasoning", "on",
    "--jinja",
    "--parallel", "2",
    "--kv-unified",
  ]);
});

test("atomic lock recovers a dead owner but preserves a live owner", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "memos-model-lock-"));
  const lockPath = path.join(root, "model-service-start.lock");
  try {
    await fs.writeFile(lockPath, JSON.stringify({ schema_version: 1, owner_pid: 2147483647, owner_client: "qwen-local", acquired_at: "2026-08-05T00:00:00Z", config_sha256: "old" }));
    const recovered = await acquireStartLock({ lockPath, ownerClient: "memos-codex", configSha256: "new", timeoutMs: 1000, serviceHealthy: async () => false });
    assert.ok(recovered);
    await recovered.release();
    assert.equal(await fs.stat(lockPath).then(() => true, () => false), false);

    await fs.writeFile(lockPath, JSON.stringify({ schema_version: 1, owner_pid: process.pid, owner_client: "qwen-local", acquired_at: "2026-08-05T00:00:00Z", config_sha256: "live" }));
    await assert.rejects(
      acquireStartLock({ lockPath, ownerClient: "memos-codex", configSha256: "new", timeoutMs: 80, pollMs: 20, serviceHealthy: async () => false }),
      /lock timeout/i,
    );
    assert.equal(await fs.stat(lockPath).then(() => true, () => false), true);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("recovery guard prevents another client from deleting a stale start lock", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "memos-model-recovery-guard-"));
  const lockPath = path.join(root, "model-service-start.lock");
  const recoveryPath = `${lockPath}.recovery`;
  try {
    await fs.writeFile(lockPath, JSON.stringify({ schema_version: 1, owner_pid: 2147483647, owner_client: "qwen-local", acquired_at: "2026-08-05T00:00:00Z", config_sha256: "old" }));
    await fs.writeFile(recoveryPath, JSON.stringify({ schema_version: 1, owner_pid: process.pid, owner_client: "qwen-local", acquired_at: "2026-08-05T00:00:00Z", config_sha256: "recovery" }));

    await assert.rejects(
      acquireStartLock({ lockPath, ownerClient: "memos-codex", configSha256: "new", timeoutMs: 80, pollMs: 20, serviceHealthy: async () => false }),
      (error) => error?.code === "start_lock_timeout",
    );
    assert.equal(await fs.stat(lockPath).then(() => true, () => false), true);
    assert.equal(await fs.stat(recoveryPath).then(() => true, () => false), true);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("stale recovery guard is atomically retired before lock acquisition", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "memos-model-stale-recovery-"));
  const lockPath = path.join(root, "model-service-start.lock");
  const recoveryPath = `${lockPath}.recovery`;
  try {
    await fs.writeFile(recoveryPath, JSON.stringify({ schema_version: 1, owner_pid: 2147483647, owner_client: "qwen-local", acquired_at: "2026-08-05T00:00:00Z", config_sha256: "stale-recovery" }));
    const lease = await acquireStartLock({ lockPath, ownerClient: "memos-codex", configSha256: "new", timeoutMs: 1000, pollMs: 20, serviceHealthy: async () => false });
    assert.ok(lease);
    await lease.release();
    assert.equal(await fs.stat(recoveryPath).then(() => true, () => false), false);
    assert.equal((await fs.readdir(root)).filter((name) => name.startsWith("model-service-start.lock.recovery.retired.")).length, 1);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("late recovery observer preserves a newly created guard", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "memos-model-late-recovery-"));
  const recoveryPath = path.join(root, "model-service-start.lock.recovery");
  const oldRaw = JSON.stringify({ schema_version: 1, owner_pid: 2147483647, owner_client: "qwen-local", acquired_at: "2026-08-05T00:00:00Z", config_sha256: "old" });
  const newRaw = JSON.stringify({ schema_version: 1, owner_pid: process.pid, owner_client: "memos-codex", acquired_at: "2026-08-05T00:00:01Z", config_sha256: "new" });
  try {
    const digest = (await import("node:crypto")).default.createHash("sha256").update(oldRaw, "utf8").digest("hex");
    const retiredPath = `${recoveryPath}.retired.${digest}`;
    await fs.writeFile(retiredPath, oldRaw);
    await fs.writeFile(recoveryPath, newRaw);

    const retired = await retireRecoveryGuard(recoveryPath, { raw: oldRaw });

    assert.equal(retired, false);
    assert.equal(await fs.readFile(recoveryPath, "utf8"), newRaw);
    assert.equal(await fs.readFile(retiredPath, "utf8"), oldRaw);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("on-demand policy refuses a cold start without spawning", async () => {
  let starts = 0;
  await assert.rejects(
    ensureModelService({
      root: "C:\\root",
      reason: "foreground_recall",
      dependencies: {
        loadConfig: async () => ({ config: { ...fixture, auto_start_on_demand: false }, configSha256: "fixture" }),
        probe: async () => ({ healthy: false, identity: "unhealthy" }),
        spawnModel: async () => { starts += 1; },
      },
    }),
    (error) => error?.code === "auto_start_disabled",
  );
  assert.equal(starts, 0);
});

test("post-lock identity mismatch is rejected without spawning", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "memos-model-post-lock-identity-"));
  let probes = 0;
  let starts = 0;
  try {
    await assert.rejects(
      ensureModelService({
        root,
        reason: "foreground_recall",
        dependencies: {
          loadConfig: async () => ({ config: fixture, configSha256: "fixture" }),
          probe: async () => (++probes === 1
            ? { healthy: false, identity: "unhealthy", portOccupied: false }
            : { healthy: true, identity: "mismatch", pid: 4242, errorCode: "model_alias_mismatch" }),
          spawnModel: async () => { starts += 1; },
          appendEvent: async () => {},
        },
      }),
      (error) => error?.code === "model_alias_mismatch",
    );
    assert.equal(starts, 0);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("lifecycle log stores path digests rather than paths or content", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "memos-model-log-"));
  try {
    await appendLifecycleEvent({ root, config: fixture, configSha256: "abc", action: "ensure", reason: "foreground_recall", result: "failed", errorCode: "fixture" });
    const line = await fs.readFile(path.join(root, "runtime", "logs", "model-service-lifecycle.jsonl"), "utf8");
    assert.equal(line.includes(root), false);
    assert.equal(line.includes("models\\model.gguf"), false);
    assert.match(line, /"model_path_sha256":"[0-9a-f]{64}"/);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});
