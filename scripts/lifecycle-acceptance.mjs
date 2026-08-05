import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { loadModelServiceConfig, probeModelService } from "./model-service.mjs";

const mode = process.argv[2] ?? "read-only";
assert(["read-only", "recall", "remember-isolated"].includes(mode), "usage: node scripts/lifecycle-acceptance.mjs [read-only|recall|remember-isolated]");

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDir, "..");
const cleanEnv = Object.fromEntries(Object.entries(process.env).filter(([, value]) => typeof value === "string"));
for (const key of ["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "MEMOS_API_KEY"]) delete cleanEnv[key];
let isolatedRuntime = null;
if (mode === "remember-isolated") {
  isolatedRuntime = await fs.mkdtemp(path.join(os.tmpdir(), "memos-lifecycle-acceptance-"));
  await fs.copyFile(path.join(root, "runtime", "config.yaml"), path.join(isolatedRuntime, "config.yaml"));
  cleanEnv.MEMOS_HOME = isolatedRuntime;
  cleanEnv.MEMOS_CONFIG_FILE = path.join(isolatedRuntime, "config.yaml");
  cleanEnv.MEMOS_TRANSFORMERS_CACHE = path.join(root, "runtime", "transformers-cache");
}

const transport = new StdioClientTransport({
  command: process.execPath,
  args: [path.join(scriptDir, "mcp-server.mjs")],
  cwd: root,
  env: cleanEnv,
  stderr: "pipe",
});
const client = new Client({ name: "model-lifecycle-acceptance", version: "1.0.0" }, { capabilities: {} });
const execFileAsync = promisify(execFile);

async function inspectWindowsProcess(pid) {
  if (!pid) return null;
  const command = `Get-CimInstance Win32_Process -Filter 'ProcessId=${Number(pid)}' | Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress`;
  const { stdout } = await execFileAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], { windowsHide: true });
  return JSON.parse(stdout);
}

function parseText(result) {
  const block = result.content?.find((item) => item.type === "text");
  assert(block?.text, "tool result has no text block");
  const value = JSON.parse(block.text);
  if (result.isError) throw new Error(`${value.error_code ?? "tool_failed"}: ${value.error ?? block.text}`);
  return value;
}

const requestOptions = { timeout: 600_000 };
try {
  await client.connect(transport);
  const loaded = await loadModelServiceConfig({ root });
  const before = await probeModelService(loaded.config, root);
  const health = parseText(await client.callTool({ name: "memos_health", arguments: {} }, undefined, requestOptions));
  const recent = parseText(await client.callTool({ name: "memos_list_recent", arguments: { limit: 3 } }, undefined, requestOptions));
  const afterReadOnly = await probeModelService(loaded.config, root);

  if (mode === "read-only") {
    assert.equal(before.healthy, false, "read-only acceptance must begin with the model stopped");
    assert.equal(afterReadOnly.healthy, false, "health/list_recent must not start the model");
  }

  let recall = null;
  let afterRecall = null;
  let startedProcess = null;
  let remembered = null;
  if (mode === "recall" || mode === "remember-isolated") {
    let query = "Qwen Local 共享模型配置和按需启动的已有决定";
    if (mode === "remember-isolated") {
      const marker = `隔离验收-${Date.now()}-${Math.random().toString(16).slice(2)}`;
      remembered = parseText(await client.callTool({
        name: "memos_remember",
        arguments: {
          session_id: marker,
          user_message: `请记住临时验收标记 ${marker}，它只用于隔离数据库验收。`,
          assistant_response: `已记住临时验收标记 ${marker}。`,
        },
      }, undefined, requestOptions));
      assert.equal(remembered.stored, true, "a real isolated remember must store one trace");
      query = `临时验收标记 ${marker} 是什么？`;
    }
    recall = parseText(await client.callTool({
      name: "memos_recall",
      arguments: { query, top_k: 3 },
    }, undefined, requestOptions));
    afterRecall = await probeModelService(loaded.config, root);
    assert.equal(afterRecall.healthy, true, "a real recall must start the model when the policy is enabled");
    assert.notEqual(afterRecall.identity, "mismatch", "the started model must match the shared identity");
    startedProcess = await inspectWindowsProcess(afterRecall.pid);
    assert.equal(path.resolve(startedProcess.ExecutablePath).toLowerCase(), path.resolve(root, loaded.config.server_executable).toLowerCase(), "the listener must use the configured server executable");
    assert.match(startedProcess.CommandLine, new RegExp(`--alias\\s+${loaded.config.model_alias.replace(/[.*+?^${}()|[\\]\\]/g, "\\$&")}`), "the process must use the configured alias");
    assert.doesNotMatch(startedProcess.CommandLine, /--reasoning-budget\b/i, "the new process must not inherit an obsolete hidden reasoning budget");
  }

  console.log(JSON.stringify({
    mode,
    before,
    healthLocalLlm: health.local_llm,
    recentTraceCount: recent.traces?.length ?? 0,
    isolatedRuntime,
    remembered,
    afterReadOnly,
    recallHitCount: recall?.hit_count ?? null,
    afterRecall,
    startedProcess,
  }, null, 2));
} finally {
  try {
    await client.close();
  } finally {
    if (isolatedRuntime) await fs.rm(isolatedRuntime, { recursive: true, force: true });
  }
}
