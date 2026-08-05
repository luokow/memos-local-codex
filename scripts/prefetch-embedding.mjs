import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDir, "..");
const cacheDir = path.join(root, "runtime", "transformers-cache");
await fs.mkdir(cacheDir, { recursive: true });

const transformers = await import("@huggingface/transformers");
transformers.env.cacheDir = cacheDir;
transformers.env.allowLocalModels = true;
transformers.env.allowRemoteModels = true;

const extractor = await transformers.pipeline(
  "feature-extraction",
  "Xenova/all-MiniLM-L6-v2",
  { dtype: "q8", device: "cpu" },
);
const output = await extractor("MemOS local embedding verification", {
  pooling: "mean",
  normalize: true,
});
if (!output?.data || output.data.length !== 384) {
  throw new Error(`Unexpected embedding dimension: ${output?.data?.length ?? "missing"}`);
}
console.log(JSON.stringify({ ok: true, dimension: output.data.length, cacheDir }));
