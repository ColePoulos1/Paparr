import { spawnSync } from "node:child_process";
import { mkdtemp, readFile, readdir, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createClient } from "@hey-api/openapi-ts";
import process from "node:process";
import { log } from "node:console";

const ui = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repo = resolve(ui, "../..");
const check = process.argv.includes("--check");
const temporary = await mkdtemp(join(tmpdir(), "paparr-contracts-"));
try {
  const schema = join(temporary, "openapi.json");
  const result = spawnSync("dotnet", [
    "run", "--project", join(repo, "src/Paparr.API"),
    "--configuration", "Release", "--no-launch-profile",
    ...(process.argv.includes("--no-build") ? ["--no-build"] : []),
    "--", "--export-openapi", schema,
  ], { cwd: repo, stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error("API schema export failed.");

  const output = join(temporary, "generated");
  await createClient({
    input: schema,
    output,
    plugins: ["@hey-api/typescript"],
  });
  const destination = join(ui, "src/generated");
  const files = (await readdir(output)).sort();
  if (check) {
    const existing = await readdir(destination).catch(() => []);
    if (existing.sort().join() !== files.join())
      throw new Error("Generated API file list changed. Run npm run api:generate.");
  } else await mkdir(destination, { recursive: true });
  for (const file of files) {
    const content = (await readFile(join(output, file), "utf8")).replaceAll("\r\n", "\n");
    if (check) {
      const saved = (await readFile(join(destination, file), "utf8")).replaceAll("\r\n", "\n");
      if (saved !== content)
        throw new Error(`API contracts changed (${file}). Run npm run api:generate.`);
    } else await writeFile(join(destination, file), content);
  }
  log(check ? "API contracts match the backend." : "API contracts generated.");
} finally {
  await rm(temporary, { recursive: true, force: true });
}
