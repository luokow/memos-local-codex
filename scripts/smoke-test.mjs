import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDir, "..");
const serverPath = path.join(scriptDir, "mcp-server.mjs");
const cleanEnv = Object.fromEntries(
  Object.entries(process.env).filter(([, value]) => typeof value === "string"),
);
delete cleanEnv.OPENAI_API_KEY;
delete cleanEnv.ANTHROPIC_API_KEY;
delete cleanEnv.MEMOS_API_KEY;
cleanEnv.MEMOS_HOME = path.join(root, "runtime");
cleanEnv.MEMOS_CONFIG_FILE = path.join(root, "runtime", "config.yaml");

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [serverPath],
  cwd: root,
  env: cleanEnv,
  stderr: "pipe",
});
const client = new Client(
  { name: "memos-local-codex-smoke", version: "0.1.0" },
  { capabilities: {} },
);

function parseText(result) {
  const block = result.content?.find((item) => item.type === "text");
  assert(block?.text, "tool result has no text block");
  return JSON.parse(block.text);
}

const requestOptions = { timeout: 600_000 };
try {
  await client.connect(transport);
  const { tools } = await client.listTools();
  const toolNames = tools.map(({ name }) => name).sort();
  assert.deepEqual(toolNames, [
    "memos_health",
    "memos_list_recent",
    "memos_recall",
    "memos_remember",
  ]);

  const before = parseText(await client.callTool(
    { name: "memos_health", arguments: {} },
    undefined,
    requestOptions,
  ));
  assert.equal(before.local_llm?.healthy, true, "local LLM is not healthy");

  const remembered = parseText(await client.callTool(
    {
      name: "memos_remember",
      arguments: {
        session_id: "local-acceptance-20260801",
        user_message: "请记住：本地记忆验收代号是青柠-7429，所有模型和记忆数据只允许存放在 D 盘。",
        assistant_response: "已确认：验收代号青柠-7429；模型、向量缓存和 MemOS 数据库都只放在 D 盘，不使用云端 API。",
      },
    },
    undefined,
    requestOptions,
  ));
  assert.equal(remembered.stored, true, "memory was not stored");
  assert.equal(remembered.evolution_flushed, true, "memory evolution was not flushed");

  const recalled = parseText(await client.callTool(
    { name: "memos_recall", arguments: { query: "这套本地记忆的验收暗号是什么，文件要放在哪个盘？", top_k: 8 } },
    undefined,
    requestOptions,
  ));
  const recallJson = JSON.stringify(recalled);
  assert.match(recallJson, /青柠-7429/, "semantic recall missed the acceptance code");
  assert.match(recallJson, /D 盘|D:\\\\|D盘/i, "semantic recall missed the storage drive");

  const recent = parseText(await client.callTool(
    { name: "memos_list_recent", arguments: { limit: 10 } },
    undefined,
    requestOptions,
  ));
  assert(Array.isArray(recent.traces) && recent.traces.length > 0, "no recent traces were returned");

  const after = parseText(await client.callTool(
    { name: "memos_health", arguments: {} },
    undefined,
    requestOptions,
  ));
  assert.equal(after.ok, true, "MemOS health did not become OK after real LLM and embedding calls");
  assert(after.llm?.lastOkAt, "LLM health has no successful-call timestamp");
  assert(after.embedder?.lastOkAt, "embedding health has no successful-call timestamp");

  console.log(JSON.stringify({
    connected: true,
    serverVersion: client.getServerVersion(),
    toolNames,
    healthBeforeCalls: before,
    healthAfterCalls: after,
    remembered,
    recallHitCount: recalled.hit_count,
    recallContainsCode: true,
    recallContainsDrive: true,
    recentTraceCount: recent.traces.length,
  }, null, 2));
} finally {
  await client.close();
}
