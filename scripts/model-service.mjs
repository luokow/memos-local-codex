import crypto from "node:crypto";
import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import { spawn, execFile } from "node:child_process";
import { promisify } from "node:util";

const execFileAsync = promisify(execFile);

const fields = new Set([
  "schema_version", "bind_host", "port", "server_executable", "model_path", "model_alias",
  "context_size", "gpu_layers", "parallel_slots", "reasoning_enabled", "use_jinja",
  "startup_timeout_seconds", "auto_start_on_demand",
]);

function sorted(value) {
  if (Array.isArray(value)) return value.map(sorted);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, sorted(value[key])]));
  }
  return value;
}

export function canonicalizeConfig(config) {
  return JSON.stringify(sorted(config));
}

export function configSha256(config) {
  return crypto.createHash("sha256").update(canonicalizeConfig(config), "utf8").digest("hex");
}

export function resolveConfiguredPath(root, configuredPath) {
  return path.isAbsolute(configuredPath) ? path.normalize(configuredPath) : path.resolve(root, configuredPath);
}

function integerInRange(value, minimum, maximum) {
  return Number.isInteger(value) && value >= minimum && value <= maximum;
}

export async function validateModelServiceConfig(config, root, { checkFiles = true } = {}) {
  if (!config || typeof config !== "object" || Array.isArray(config)) throw new Error("model-service config must be a JSON object");
  const unknown = Object.keys(config).filter((key) => !fields.has(key));
  if (unknown.length) throw new Error(`model-service config has unknown field(s): ${unknown.join(", ")}`);
  if (config.schema_version !== 1) throw new Error(`unsupported model-service schema_version: ${config.schema_version}`);
  if (config.bind_host !== "127.0.0.1" && config.bind_host !== "::1") throw new Error("model-service bind_host must be loopback");
  if (!integerInRange(config.port, 1024, 65535)) throw new Error("model-service port must be between 1024 and 65535");
  for (const key of ["server_executable", "model_path", "model_alias"]) {
    if (typeof config[key] !== "string" || !config[key].trim()) throw new Error(`model-service ${key} must be a non-empty string`);
  }
  if (!integerInRange(config.context_size, 2048, 32768)) throw new Error("model-service context_size must be between 2048 and 32768");
  if (!integerInRange(config.gpu_layers, 0, 999)) throw new Error("model-service gpu_layers must be between 0 and 999");
  if (!integerInRange(config.parallel_slots, 1, 8)) throw new Error("model-service parallel_slots must be between 1 and 8");
  if (typeof config.reasoning_enabled !== "boolean") throw new Error("model-service reasoning_enabled must be boolean");
  if (typeof config.use_jinja !== "boolean") throw new Error("model-service use_jinja must be boolean");
  if (!integerInRange(config.startup_timeout_seconds, 30, 600)) throw new Error("model-service startup_timeout_seconds must be between 30 and 600");
  if (typeof config.auto_start_on_demand !== "boolean") throw new Error("model-service auto_start_on_demand must be boolean");
  if (checkFiles) {
    const server = resolveConfiguredPath(root, config.server_executable);
    const model = resolveConfiguredPath(root, config.model_path);
    await fsp.access(server, fs.constants.R_OK).catch(() => { throw new Error(`missing local LLM server: ${server}`); });
    await fsp.access(model, fs.constants.R_OK).catch(() => { throw new Error(`missing local LLM model: ${model}`); });
  }
  return config;
}

export async function loadModelServiceConfig({ root, configPath = process.env.MODEL_SERVICE_CONFIG_PATH || path.join(root, "runtime", "model-service.json"), checkFiles = true }) {
  let parsed;
  try {
    parsed = JSON.parse(await fsp.readFile(configPath, "utf8"));
  } catch (error) {
    throw new Error(`cannot read model-service config ${configPath}: ${error instanceof Error ? error.message : String(error)}`);
  }
  await validateModelServiceConfig(parsed, root, { checkFiles });
  return { config: parsed, configPath: path.resolve(configPath), configSha256: configSha256(parsed) };
}

export function buildLaunchArguments(config, root) {
  const args = [
    "--model", resolveConfiguredPath(root, config.model_path),
    "--alias", config.model_alias,
    "--host", config.bind_host,
    "--port", String(config.port),
    "--ctx-size", String(config.context_size),
    "--n-gpu-layers", String(config.gpu_layers),
    "--reasoning", config.reasoning_enabled ? "on" : "off",
  ];
  if (config.use_jinja) args.push("--jinja");
  args.push("--parallel", String(config.parallel_slots));
  if (config.parallel_slots > 1) args.push("--kv-unified");
  return args;
}

export class ModelServiceError extends Error {
  constructor(code, message) {
    super(message);
    this.name = "ModelServiceError";
    this.code = code;
  }
}

function pidAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try { process.kill(pid, 0); return true; } catch { return false; }
}

async function readLock(lockPath) {
  try {
    const raw = await fsp.readFile(lockPath, "utf8");
    const document = JSON.parse(raw);
    return document?.schema_version === 1 ? { raw, document } : null;
  } catch { return null; }
}

export async function retireRecoveryGuard(recoveryPath, observedRecovery) {
  const retiredDigest = crypto.createHash("sha256").update(observedRecovery.raw, "utf8").digest("hex");
  const retiredPath = `${recoveryPath}.retired.${retiredDigest}`;
  let linked = false;
  try {
    await fsp.link(recoveryPath, retiredPath);
    linked = true;
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    if (error?.code !== "EEXIST") throw error;
  }

  let sourceStat;
  let retiredStat;
  let retiredRaw;
  try {
    [sourceStat, retiredStat, retiredRaw] = await Promise.all([
      fsp.stat(recoveryPath, { bigint: true }),
      fsp.stat(retiredPath, { bigint: true }),
      fsp.readFile(retiredPath, "utf8"),
    ]);
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
  const sameFile = sourceStat.dev === retiredStat.dev && sourceStat.ino === retiredStat.ino;
  if (!sameFile || retiredRaw !== observedRecovery.raw) {
    if (linked && sameFile) await fsp.unlink(retiredPath).catch(() => {});
    return false;
  }
  await fsp.unlink(recoveryPath);
  return true;
}

export async function acquireStartLock({
  lockPath,
  ownerClient,
  configSha256,
  timeoutMs,
  serviceHealthy,
  pollMs = 250,
  onRecover = async () => {},
}) {
  if (!new Set(["qwen-local", "memos-codex"]).has(ownerClient)) throw new ModelServiceError("invalid_lock_client", "unknown model-service lock client");
  await fsp.mkdir(path.dirname(lockPath), { recursive: true });
  const recoveryPath = `${lockPath}.recovery`;
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const recoveryInProgress = await fsp.access(recoveryPath).then(() => true, () => false);
    if (recoveryInProgress) {
      if (await serviceHealthy()) return null;
      const observedRecovery = await readLock(recoveryPath);
      if (observedRecovery && !pidAlive(observedRecovery.document.owner_pid)) {
        if (await retireRecoveryGuard(recoveryPath, observedRecovery)) continue;
      }
      await new Promise((resolve) => setTimeout(resolve, pollMs));
      continue;
    }
    const content = JSON.stringify({
      schema_version: 1,
      owner_pid: process.pid,
      owner_client: ownerClient,
      acquired_at: new Date().toISOString(),
      config_sha256: configSha256,
    });
    try {
      const handle = await fsp.open(lockPath, "wx");
      try { await handle.writeFile(content, "utf8"); await handle.sync(); } finally { await handle.close(); }
      let released = false;
      return {
        async release() {
          if (released) return;
          released = true;
          const current = await fsp.readFile(lockPath, "utf8").catch(() => null);
          if (current === content) await fsp.unlink(lockPath).catch(() => {});
        },
      };
    } catch (error) {
      if (error?.code !== "EEXIST") throw error;
      if (await serviceHealthy()) return null;
      const observed = await readLock(lockPath);
      if (observed && !pidAlive(observed.document.owner_pid)) {
        const recoveryContent = JSON.stringify({
          schema_version: 1,
          owner_pid: process.pid,
          owner_client: ownerClient,
          acquired_at: new Date().toISOString(),
          config_sha256: configSha256,
        });
        let recoveryHandle;
        try {
          recoveryHandle = await fsp.open(recoveryPath, "wx");
          await recoveryHandle.writeFile(recoveryContent, "utf8");
          await recoveryHandle.sync();
        } catch (recoveryError) {
          await recoveryHandle?.close().catch(() => {});
          if (recoveryError?.code !== "EEXIST") throw recoveryError;
          await new Promise((resolve) => setTimeout(resolve, pollMs));
          continue;
        }
        try {
          const current = await readLock(lockPath);
          if (current?.raw === observed.raw && !pidAlive(current.document.owner_pid)) {
            await fsp.unlink(lockPath);
            await onRecover(observed.document);
          }
        } finally {
          await recoveryHandle.close().catch(() => {});
          const currentRecovery = await fsp.readFile(recoveryPath, "utf8").catch(() => null);
          if (currentRecovery === recoveryContent) await fsp.unlink(recoveryPath).catch(() => {});
        }
        continue;
      }
      await new Promise((resolve) => setTimeout(resolve, pollMs));
    }
  }
  throw new ModelServiceError("start_lock_timeout", `model-service lock timeout: ${lockPath}`);
}

function endpointRoot(config) {
  const host = config.bind_host === "::1" ? "[::1]" : config.bind_host;
  return `http://${host}:${config.port}`;
}

async function listenerPid(port) {
  try {
    const { stdout } = await execFileAsync("netstat.exe", ["-ano", "-p", "tcp"], { windowsHide: true });
    for (const raw of stdout.split(/\r?\n/)) {
      const line = raw.trim();
      if (!/LISTENING/i.test(line)) continue;
      const parts = line.split(/\s+/);
      if (parts.length < 5) continue;
      const local = parts[1];
      if (!local.endsWith(`:${port}`)) continue;
      if (!local.startsWith("127.0.0.1:") && !local.startsWith("[::1]:")) continue;
      const pid = Number(parts.at(-1));
      if (Number.isInteger(pid) && pid > 0) return pid;
    }
  } catch { }
  return null;
}

async function processPath(pid) {
  if (!pid) return null;
  try {
    const command = `(Get-Process -Id ${Number(pid)} -ErrorAction Stop).Path`;
    const { stdout } = await execFileAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], { windowsHide: true });
    return stdout.trim() || null;
  } catch { return null; }
}

export async function probeModelService(config, root) {
  let healthy = false;
  try {
    const response = await fetch(`${endpointRoot(config)}/health`, { signal: AbortSignal.timeout(1500) });
    healthy = response.ok;
  } catch { }
  const pid = await listenerPid(config.port);
  if (!healthy) return { healthy: false, identity: "unhealthy", pid, portOccupied: pid !== null };

  const actualPath = await processPath(pid);
  if (actualPath) {
    const expected = resolveConfiguredPath(root, config.server_executable).toLowerCase();
    if (path.resolve(actualPath).toLowerCase() !== expected) return { healthy: true, identity: "mismatch", pid, errorCode: "server_path_mismatch" };
  }

  let aliasVerified = false;
  try {
    const response = await fetch(`${endpointRoot(config)}/v1/models`, { signal: AbortSignal.timeout(1500) });
    if (response.ok) {
      const payload = await response.json();
      const aliases = Array.isArray(payload?.data) ? payload.data.map((item) => item?.id).filter(Boolean) : [];
      if (aliases.length && !aliases.includes(config.model_alias)) return { healthy: true, identity: "mismatch", pid, errorCode: "model_alias_mismatch" };
      aliasVerified = aliases.includes(config.model_alias);
    }
  } catch { }
  return { healthy: true, identity: actualPath && aliasVerified ? "verified" : "partial", pid };
}

function pathHash(value) {
  return crypto.createHash("sha256").update(path.resolve(value).toUpperCase(), "utf8").digest("hex");
}

export async function appendLifecycleEvent({ root, config, configSha256: digest, action, reason, result, pid = null, errorCode = null }) {
  const logPath = path.join(root, "runtime", "logs", "model-service-lifecycle.jsonl");
  await fsp.mkdir(path.dirname(logPath), { recursive: true });
  const record = {
    schema_version: 1,
    event_id: crypto.randomUUID().replaceAll("-", ""),
    timestamp: new Date().toISOString(),
    client: "memos-codex",
    action,
    reason,
    result,
    pid,
    port: config.port,
    server_path_sha256: pathHash(resolveConfiguredPath(root, config.server_executable)),
    model_path_sha256: pathHash(resolveConfiguredPath(root, config.model_path)),
    config_sha256: digest,
    error_code: errorCode,
  };
  await fsp.appendFile(logPath, `${JSON.stringify(record)}\n`, "utf8");
}

export async function spawnModelProcess(config, root) {
  const executable = resolveConfiguredPath(root, config.server_executable);
  const logPath = path.join(root, "runtime", "logs", "llama-server.log");
  await fsp.mkdir(path.dirname(logPath), { recursive: true });
  const logHandle = fs.openSync(logPath, "a");
  try {
    return spawn(executable, buildLaunchArguments(config, root), {
      cwd: path.dirname(executable),
      windowsHide: true,
      stdio: ["ignore", logHandle, logHandle],
    });
  } finally {
    fs.closeSync(logHandle);
  }
}

export async function ensureModelService({ root, reason, ownerClient = "memos-codex", dependencies = {} }) {
  const loadConfig = dependencies.loadConfig ?? (() => loadModelServiceConfig({ root }));
  const probe = dependencies.probe ?? ((config) => probeModelService(config, root));
  const spawnModel = dependencies.spawnModel ?? ((config) => spawnModelProcess(config, root));
  const appendEvent = dependencies.appendEvent ?? ((event) => appendLifecycleEvent({ root, ...event }));
  let loaded = await loadConfig();
  let state = await probe(loaded.config);
  if (state.identity === "mismatch") throw new ModelServiceError(state.errorCode ?? "service_identity_mismatch", "healthy local model service identity does not match shared config");
  if (state.healthy) {
    await appendEvent({ ...loaded, action: "reuse", reason, result: state.identity === "partial" ? "partial" : "success", pid: state.pid });
    return { reused: true, pid: state.pid, identity: state.identity, config: loaded.config, configSha256: loaded.configSha256 };
  }
  if (!loaded.config.auto_start_on_demand) throw new ModelServiceError("auto_start_disabled", "shared config disables on-demand model startup");

  const lockPath = path.join(root, "runtime", "locks", "model-service-start.lock");
  const lease = await acquireStartLock({
    lockPath,
    ownerClient,
    configSha256: loaded.configSha256,
    timeoutMs: loaded.config.startup_timeout_seconds * 1000,
    serviceHealthy: async () => (await probe(loaded.config)).healthy,
    onRecover: async () => appendEvent({ ...loaded, action: "recover_lock", reason, result: "success" }),
  });
  if (lease === null) {
    state = await probe(loaded.config);
    if (!state.healthy || state.identity === "mismatch") throw new ModelServiceError("service_identity_mismatch", "service became healthy but identity validation failed");
    return { reused: true, pid: state.pid, identity: state.identity, config: loaded.config, configSha256: loaded.configSha256 };
  }

  try {
    loaded = await loadConfig();
    state = await probe(loaded.config);
    if (state.identity === "mismatch") throw new ModelServiceError(state.errorCode ?? "service_identity_mismatch", "healthy local model service identity does not match shared config after lock acquisition");
    if (state.healthy) return { reused: true, pid: state.pid, identity: state.identity, config: loaded.config, configSha256: loaded.configSha256 };
    if (!loaded.config.auto_start_on_demand) throw new ModelServiceError("auto_start_disabled", "shared config disables on-demand model startup");
    if (state.portOccupied) throw new ModelServiceError("port_occupied", `port ${loaded.config.port} became occupied before model startup`);
    const child = await spawnModel(loaded.config);
    const deadline = Date.now() + loaded.config.startup_timeout_seconds * 1000;
    while (Date.now() < deadline) {
      if (child?.exitCode !== undefined && child.exitCode !== null) throw new ModelServiceError("server_exited", `local model server exited with code ${child.exitCode}`);
      state = await probe(loaded.config);
      if (state.identity === "mismatch") throw new ModelServiceError(state.errorCode ?? "service_identity_mismatch", "started service identity does not match shared config");
      if (state.healthy) {
        await appendEvent({ ...loaded, action: "start", reason, result: state.identity === "partial" ? "partial" : "success", pid: child?.pid ?? state.pid });
        return { reused: false, pid: child?.pid ?? state.pid, identity: state.identity, child, config: loaded.config, configSha256: loaded.configSha256 };
      }
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    throw new ModelServiceError("startup_timeout", "local model service did not become healthy before timeout");
  } catch (error) {
    await appendEvent({ ...loaded, action: "ensure", reason, result: "failed", errorCode: error?.code ?? "ensure_failed" }).catch(() => {});
    throw error;
  } finally {
    await lease.release();
  }
}
