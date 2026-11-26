import { appendFile } from "node:fs/promises";
import { pathToFileURL } from "node:url";

export async function recoverPages(env, request = fetch) {
  const skip = (message) => ({ requested: false, message });
  if (env.PAGES_RECOVERY_ATTEMPT !== "false")
    return skip("A recovery run cannot request another recovery.");
  if (env.GITHUB_RUN_ATTEMPT !== "1")
    return skip(
      "Start a fresh manual workflow run instead of rerunning this attempt.",
    );
  if (!env.PAGES_DEPLOYMENT_URL)
    return skip("Pages did not confirm creation of a deployment.");
  if (!["push", "workflow_dispatch"].includes(env.GITHUB_EVENT_NAME))
    return skip("This event cannot request a production deployment.");

  const ref = env.PAGES_RECOVERY_REF;
  if (ref !== "main" || env.GITHUB_REF !== `refs/heads/${ref}`)
    throw new Error(
      "Pages recovery must remain on its approved deployment branch.",
    );
  if (
    !/^[\w-]+\/[\w.-]+$/.test(env.GITHUB_REPOSITORY ?? "") ||
    [".", ".."].includes(env.GITHUB_REPOSITORY.split("/")[1]) ||
    !/^[a-f0-9]{40}$/i.test(env.GITHUB_SHA ?? "")
  )
    throw new Error("Invalid GitHub recovery context.");
  if (!env.GH_TOKEN) throw new Error("A GitHub workflow token is required.");

  const repository = env.GITHUB_REPOSITORY.split("/")
    .map(encodeURIComponent)
    .join("/");
  const base = `https://api.github.com/repos/${repository}`;
  const headers = {
    Accept: "application/vnd.github+json",
    Authorization: `Bearer ${env.GH_TOKEN}`,
    "X-GitHub-Api-Version": "2026-03-10",
  };
  const statusResponse = await request(
    `${base}/pages/deployments/${env.GITHUB_SHA}`,
    {
      headers,
      redirect: "error",
      signal: AbortSignal.timeout(15000),
    },
  );
  if (!statusResponse.ok)
    throw new Error(
      `Pages status check failed (HTTP ${statusResponse.status}); no recovery was requested.`,
    );
  const deployment = await statusResponse.json();
  if (deployment?.status !== "deployment_failed")
    return skip(
      `Pages did not confirm a terminal backend failure (status ${JSON.stringify(deployment?.status ?? null)}).`,
    );

  const dispatch = await request(
    `${base}/actions/workflows/website.yml/dispatches`,
    {
      method: "POST",
      headers: { ...headers, "Content-Type": "application/json" },
      body: JSON.stringify({ ref, inputs: { recovery_attempt: true } }),
      redirect: "error",
      signal: AbortSignal.timeout(15000),
    },
  );
  if (!dispatch.ok)
    throw new Error(
      `Fresh workflow request failed (HTTP ${dispatch.status}); it was not resent.`,
    );
  return {
    requested: true,
    message: `Requested one fresh website run on ${ref}. The original deployment remains failed; the new run cannot request another recovery.`,
  };
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  try {
    const result = await recoverPages(process.env);
    console.log(result.message);
    if (process.env.GITHUB_STEP_SUMMARY) {
      try {
        await appendFile(
          process.env.GITHUB_STEP_SUMMARY,
          `### Pages recovery\n\n${result.message}\n`,
        );
      } catch (error) {
        console.warn(`Could not write the recovery summary: ${error.message}`);
      }
    }
  } catch (error) {
    console.error(`Pages recovery failed: ${error.message}`);
    process.exitCode = 1;
  }
}
