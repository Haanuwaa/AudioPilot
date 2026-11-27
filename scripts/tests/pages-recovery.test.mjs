import assert from "node:assert/strict";
import test from "node:test";
import { recoverPages } from "../pages-recovery.mjs";

const initial = {
  PAGES_RECOVERY_ATTEMPT: "false",
  GITHUB_RUN_ATTEMPT: "1",
  PAGES_DEPLOYMENT_URL: "https://haanuwaa.github.io/AudioPilot/",
  GITHUB_EVENT_NAME: "push",
  PAGES_RECOVERY_REF: "main",
  GITHUB_REF: "refs/heads/main",
  GITHUB_REPOSITORY: "Haanuwaa/AudioPilot",
  GITHUB_SHA: "a".repeat(40),
  GH_TOKEN: "test-only-token",
};

test("a confirmed backend failure requests one guarded fresh run", async () => {
  for (const event of ["push", "workflow_dispatch"]) {
    const calls = [];
    const result = await recoverPages(
      {
        ...initial,
        GITHUB_EVENT_NAME: event,
      },
      async (url, options) => {
        calls.push({ url, options });
        return calls.length === 1
          ? Response.json({ status: "deployment_failed" })
          : new Response(null, { status: 204 });
      },
    );
    assert.equal(result.requested, true);
    assert.equal(calls.length, 2);
    assert.match(calls[0].url, /\/pages\/deployments\/a{40}$/);
    assert.equal(calls[1].options.method, "POST");
    assert.deepEqual(JSON.parse(calls[1].options.body), {
      ref: "main",
      inputs: { recovery_attempt: true },
    });
    assert.equal(calls[1].options.redirect, "error");
  }
});

test("recovery, rerun, missing creation, and PR contexts make no API calls", async () => {
  for (const edit of [
    { PAGES_RECOVERY_ATTEMPT: "true" },
    { PAGES_RECOVERY_ATTEMPT: undefined },
    { GITHUB_RUN_ATTEMPT: "2" },
    { PAGES_DEPLOYMENT_URL: "" },
    { GITHUB_EVENT_NAME: "pull_request" },
  ]) {
    const result = await recoverPages({ ...initial, ...edit }, () => {
      assert.fail("A guarded context must not make a request.");
    });
    assert.equal(result.requested, false);
  }
});

test("content failures, pending, cancelled, success and unknown states never dispatch", async () => {
  for (const status of [
    "deployment_content_failed",
    "deployment_queued",
    "deployment_cancelled",
    "succeed",
    "future_status",
    undefined,
  ]) {
    let calls = 0;
    const result = await recoverPages(initial, async () => {
      assert.equal(++calls, 1, "An unconfirmed state must not dispatch.");
      return Response.json({ status });
    });
    assert.equal(result.requested, false);
  }
});

test("permission and server failures during status discovery stay failures", async () => {
  for (const status of [401, 403, 404, 503]) {
    let calls = 0;
    await assert.rejects(
      recoverPages(initial, async () => {
        assert.equal(++calls, 1);
        return new Response(null, { status });
      }),
      new RegExp(`HTTP ${status}`),
    );
  }
});

test("an ambiguous dispatch fault is never sent twice", async () => {
  let calls = 0;
  await assert.rejects(
    recoverPages(initial, async () => {
      if (++calls === 1) return Response.json({ status: "deployment_failed" });
      throw new Error("Connection lost after submission");
    }),
    /Connection lost after submission/,
  );
  assert.equal(calls, 2);
});

test("rejected dispatch remains a failure without retries", async () => {
  let calls = 0;
  await assert.rejects(
    recoverPages(initial, async () => {
      if (++calls === 1) return Response.json({ status: "deployment_failed" });
      return new Response(null, { status: 422 });
    }),
    /HTTP 422/,
  );
  assert.equal(calls, 2);
});

test("unapproved refs fail before contacting GitHub", async () => {
  for (const edit of [
    { GITHUB_REF: "refs/heads/other" },
    { PAGES_RECOVERY_REF: "other", GITHUB_REF: "refs/heads/other" },
  ]) {
    await assert.rejects(
      recoverPages({ ...initial, ...edit }, () => {
        assert.fail("An unapproved branch must not contact GitHub.");
      }),
      /approved deployment branch/,
    );
  }
});
