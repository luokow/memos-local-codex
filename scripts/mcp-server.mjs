import fs from "node:fs";
import fsp from "node:fs/promises";
import path from "node:path";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { bootstrapMemoryCoreFull } from "@memtensor/memos-local-plugin/dist/core/pipeline/index.js";

for (const key of ["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "MEMOS_API_KEY"]) {
  delete process.env[key];
}
process.env.HF_HUB_OFFLINE = "1";
process.env.TRANSFORMERS_OFFLINE = "1";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDir, "..");
const runtimeRoot = path.join(root, "runtime");
const configFile = path.join(runtimeRoot, "config.yaml");
const transformersCache = path.join(runtimeRoot, "transformers-cache");
const llamaExe = path.join(root, "llama", "bin", "llama-server.exe");
const modelFile = path.join(root, "models", "Qwen3.5-9B-Uncensored-HauhauCS-Aggressive-Q4_K_M.gguf");
const llamaLog = path.join(runtimeRoot, "logs", "llama-server.log");
const llamaHealthUrl = "http://127.0.0.1:18135/health";
const reuseLlmOnly = process.env.MEMOS_REUSE_LLM_ONLY === "1";
const namespace = {
  agentKind: "codex",
  profileId: "local",
  profileLabel: "Codex Local",
  workspaceId: "d-codex",
  workspacePath: "D:\\codex",
};

let core = null;
let ownedLlama = null;
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

async function localHealth() {
  try {
    const response = await fetch(llamaHealthUrl, { signal: AbortSignal.timeout(1500) });
    return response.ok;
  } catch {
    return false;
  }
}

async function ensureLocalLlm() {
  if (await localHealth()) return { reused: true };
  if (reuseLlmOnly) {
    throw new Error(`Local LLM is unavailable at ${llamaHealthUrl}; MEMOS_REUSE_LLM_ONLY forbids starting another model process`);
  }
  if (!(await pathExists(llamaExe))) throw new Error(`Missing local LLM server: ${llamaExe}`);
  if (!(await pathExists(modelFile))) throw new Error(`Missing local LLM model: ${modelFile}`);
  await fsp.mkdir(path.dirname(llamaLog), { recursive: true });
  const logHandle = fs.openSync(llamaLog, "a");
  ownedLlama = spawn(
    llamaExe,
    [
      "--model", modelFile,
      "--alias", "qwen3.5:9b-uncensored-local",
      "--host", "127.0.0.1",
      "--port", "18135",
      "--ctx-size", "8192",
      "--n-gpu-layers", "99",
      "--reasoning", "off",
      "--jinja",
    ],
    { cwd: path.dirname(llamaExe), windowsHide: true, stdio: ["ignore", logHandle, logHandle] },
  );
  fs.closeSync(logHandle);

  const deadline = Date.now() + 120_000;
  while (Date.now() < deadline) {
    if (ownedLlama.exitCode !== null) {
      throw new Error(`Local LLM server exited with code ${ownedLlama.exitCode}; see ${llamaLog}`);
    }
    if (await localHealth()) return { reused: false, pid: ownedLlama.pid };
    await new Promise((resolve) => setTimeout(resolve, 750));
  }
  throw new Error(`Local LLM server did not become healthy; see ${llamaLog}`);
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

await fsp.access(configFile, fs.constants.R_OK);
await ensureLocalLlm();
await configureOfflineEmbedding();
const home = {
  root: runtimeRoot,
  configFile,
  dataDir: path.join(runtimeRoot, "data"),
  dbFile: path.join(runtimeRoot, "data", "memos.db"),
  skillsDir: path.join(runtimeRoot, "skills"),
  logsDir: path.join(runtimeRoot, "logs"),
  daemonDir: path.join(runtimeRoot, "daemon"),
};
({ core } = await bootstrapMemoryCoreFull({
  agent: "codex",
  namespace,
  home,
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
      return textResult({ ...(await core.health()), local_llm: { healthy: await localHealth(), endpoint: llamaHealthUrl } });
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
    return textResult({ error: error instanceof Error ? error.message : String(error) }, true);
  }
});

process.once("SIGINT", () => void shutdown().finally(() => process.exit(0)));
process.once("SIGTERM", () => void shutdown().finally(() => process.exit(0)));
process.once("exit", () => { if (ownedLlama && ownedLlama.exitCode === null) try { ownedLlama.kill(); } catch {} });

await server.connect(new StdioServerTransport());
