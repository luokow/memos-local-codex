import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const cacheDir = path.resolve(scriptDir, "..", "runtime", "transformers-cache");
const transformers = await import("@huggingface/transformers");
transformers.env.cacheDir = cacheDir;
transformers.env.allowLocalModels = true;
transformers.env.allowRemoteModels = false;

const extractor = await transformers.pipeline(
  "feature-extraction",
  "Xenova/all-MiniLM-L6-v2",
  { dtype: "q8", device: "cpu" },
);
const output = await extractor("offline semantic embedding verification", {
  pooling: "mean",
  normalize: true,
});
assert.equal(output?.data?.length, 384);
console.log(JSON.stringify({ ok: true, remoteModelsAllowed: false, dimension: 384, cacheDir }));

