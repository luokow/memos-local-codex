import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { bootstrapMemoryCoreFull } from "@memtensor/memos-local-plugin/dist/core/pipeline/index.js";
import { loadConfig } from "@memtensor/memos-local-plugin/dist/core/config/index.js";
import { ensureModelService, loadModelServiceConfig, probeModelService } from "./model-service.mjs";

for (const key of ["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "MEMOS_API_KEY"]) {
  delete process.env[key];
}
process.env.HF_HUB_OFFLINE = "1";
process.env.TRANSFORMERS_OFFLINE = "1";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDir, "..");
const runtimeRoot = path.resolve(process.env.MEMOS_HOME || path.join(root, "runtime"));
const configFile = path.resolve(process.env.MEMOS_CONFIG_FILE || path.join(runtimeRoot, "config.yaml"));
const transformersCache = path.resolve(process.env.MEMOS_TRANSFORMERS_CACHE || path.join(root, "runtime", "transformers-cache"));
const namespace = {
  agentKind: "codex",
  profileId: "local",
  profileLabel: "Codex Local",
  workspaceId: "d-codex",
  workspacePath: "D:\\codex",
};

let core = null;
let ownedLlama = null;
let runtimeConfig = null;
let shuttingDown = false;
const knownSessions = new Map();
const defaultSessionKey = `codex-${randomUUID()}`;

function textResult(value, isError = false) {
  return {
    isError,
    content: [{ type: "text", text: typeof value === "string" ? value : JSON.stringify(value, null, 2) }],
  };
}

async function pathExists(target) {
  try {
    await fsp.access(target, fs.constants.R_OK);
    return true;
  } catch {
    return false;
  }
}

function applyModelEndpoint(config) {
  const host = config.bind_host === "::1" ? "[::1]" : config.bind_host;
  runtimeConfig.llm.endpoint = `http://${host}:${config.port}/v1/chat/completions`;
  runtimeConfig.llm.model = config.model_alias;
  runtimeConfig.llm.fallbackToHost = false;
}

async function ensureLocalLlm(reason) {
  const result = await ensureModelService({ root, reason });
  applyModelEndpoint(result.config);
  if (result.child) ownedLlama = result.child;
  return result;
}

async function localModelHealth() {
  try {
    const loaded = await loadModelServiceConfig({ root });
    const state = await probeModelService(loaded.config, root);
    const host = loaded.config.bind_host === "::1" ? "[::1]" : loaded.config.bind_host;
    return {
      healthy: state.healthy,
      identity: state.identity,
      endpoint: `http://${host}:${loaded.config.port}`,
      config_ok: true,
      config_sha256: loaded.configSha256,
    };
  } catch (error) {
    return { healthy: false, identity: "unavailable", config_ok: false, error_code: error?.code ?? "config_invalid", message: error instanceof Error ? error.message : String(error) };
  }
}

async function configureOfflineEmbedding() {
  if (!(await pathExists(transformersCache))) {
    throw new Error(`Embedding cache is missing; run scripts/prefetch-embedding.mjs first: ${transformersCache}`);
  }
  const transformers = await import("@huggingface/transformers");
  transformers.env.cacheDir = transformersCache;
  transformers.env.allowLocalModels = true;
  transformers.env.allowRemoteModels = false;
}

async function getSession(requested) {
  const key = requested?.trim() || defaultSessionKey;
  const cached = knownSessions.get(key);
  if (cached) return cached;
  const opened = await core.openSession({
    agent: "codex",
    sessionId: key,
    namespace,
    meta: { source: "codex-mcp", workspace: "D:\\codex" },
  });
  knownSessions.set(key, opened);
  return opened;
}

async function remember(args) {
  const userMessage = String(args?.user_message ?? "").trim();
  const assistantResponse = String(args?.assistant_response ?? "").trim();
  if (!userMessage || !assistantResponse) throw new Error("user_message and assistant_response are required");
  if (userMessage.length > 12_000 || assistantResponse.length > 24_000) {
    throw new Error("memory payload exceeds the local safety limit");
  }
  await ensureLocalLlm("foreground_remember");
  const sessionId = await getSession(args?.session_id);
  const started = await core.onTurnStart({
    agent: "codex",
    namespace,
    sessionId,
    turnKey: randomUUID(),
    userText: userMessage,
    contextHints: { source: "codex-mcp" },
    ts: Date.now(),
  });
  const episodeId = started.query.episodeId;
  if (!episodeId) throw new Error("MemOS did not return an episode id");
  const stored = await core.onTurnEnd({
    agent: "codex",
    namespace,
    sessionId: started.query.sessionId ?? sessionId,
    episodeId,
    agentText: assistantResponse,
    toolCalls: [],
    contextHints: { source: "codex-mcp" },
    ts: Date.now(),
  });
  await core.closeEpisode(episodeId);
  return {
    stored: true,
    trace_id: stored.traceId,
    episode_id: stored.episodeId,
    recalled_before_store: started.hits.length,
    evolution_flushed: true,
  };
}

async function recall(args) {
  const query = String(args?.query ?? "").trim();
  if (!query) throw new Error("query is required");
  if (query.length > 4_000) throw new Error("query exceeds 4000 characters");
  await ensureLocalLlm("foreground_recall");
  const top = Math.max(1, Math.min(10, Number(args?.top_k ?? 5)));
  const result = await core.searchMemory({
    agent: "codex",
    namespace,
    query,
    topK: { tier1: top, tier2: top, tier3: top },
  });
  return {
    query,
    hit_count: result.hits.length,
    context: result.injectedContext,
    hits: result.hits,
  };
}

const tools = [
  {
    name: "memos_recall",
    description: "Search the fully local MemOS store before answering when prior preferences, decisions, failures, or reusable skills may matter. Data never leaves localhost.",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", minLength: 1, maxLength: 4000 },
        top_k: { type: "integer", minimum: 1, maximum: 10, default: 5 },
      },
      required: ["query"],
      additionalProperties: false,
    },
  },
  {
    name: "memos_remember",
    description: "Store a completed user/assistant exchange in fully local MemOS, close its episode, and run any eligible full-memory evolution stages. Higher tiers are created only when their criteria are met. Use only for durable decisions, preferences, outcomes, or reusable procedures.",
    inputSchema: {
      type: "object",
      properties: {
        user_message: { type: "string", minLength: 1, maxLength: 12000 },
        assistant_response: { type: "string", minLength: 1, maxLength: 24000 },
        session_id: { type: "string", minLength: 1, maxLength: 200 },
      },
      required: ["user_message", "assistant_response"],
      additionalProperties: false,
    },
  },
  {
    name: "memos_health",
    description: "Report local MemOS, embedding, model, and D-drive data paths without making a cloud request.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
  },
  {
    name: "memos_list_recent",
    description: "List recent local memory traces for inspection. This is read-only.",
    inputSchema: {
      type: "object",
      properties: { limit: { type: "integer", minimum: 1, maximum: 50, default: 10 } },
      additionalProperties: false,
    },
  },
];

async function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  try { await core?.shutdown(); } catch {}
  if (ownedLlama && ownedLlama.exitCode === null) {
    try { ownedLlama.kill(); } catch {}
  }
}

const home = {
  root: runtimeRoot,
  configFile,
  dataDir: path.join(runtimeRoot, "data"),
  dbFile: path.join(runtimeRoot, "data", "memos.db"),
  skillsDir: path.join(runtimeRoot, "skills"),
  logsDir: path.join(runtimeRoot, "logs"),
  daemonDir: path.join(runtimeRoot, "daemon"),
};
await fsp.access(configFile, fs.constants.R_OK);
const bootModel = await loadModelServiceConfig({ root });
const loadedRuntime = await loadConfig(home);
runtimeConfig = structuredClone(loadedRuntime.config);
applyModelEndpoint(bootModel.config);
await configureOfflineEmbedding();
({ core } = await bootstrapMemoryCoreFull({
  agent: "codex",
  namespace,
  home,
  config: runtimeConfig,
  autoRecovery: false,
  pkgVersion: "2.0.12-codex-local",
  hostLlmBridge: null,
  telemetry: null,
  initLogging: true,
}));
await core.init();

const server = new Server(
  { name: "memos-local-codex", version: "0.1.0" },
  { capabilities: { tools: {} } },
);
server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools }));
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  try {
    const { name, arguments: args = {} } = request.params;
    if (name === "memos_recall") return textResult(await recall(args));
    if (name === "memos_remember") return textResult(await remember(args));
    if (name === "memos_health") {
      return textResult({ ...(await core.health()), local_llm: await localModelHealth() });
    }
    if (name === "memos_list_recent") {
      const limit = Math.max(1, Math.min(50, Number(args?.limit ?? 10)));
      return textResult({
        traces: await core.listTraces({
          limit,
          ownerAgentKind: namespace.agentKind,
          ownerProfileId: namespace.profileId,
        }),
      });
    }
    return textResult({ error: `Unknown tool: ${name}` }, true);
  } catch (error) {
    return textResult({ error_code: error?.code ?? "tool_failed", error: error instanceof Error ? error.message : String(error) }, true);
  }
});

process.once("SIGINT", () => void shutdown().finally(() => process.exit(0)));
process.once("SIGTERM", () => void shutdown().finally(() => process.exit(0)));
process.once("exit", () => { if (ownedLlama && ownedLlama.exitCode === null) try { ownedLlama.kill(); } catch {} });

await server.connect(new StdioServerTransport());
