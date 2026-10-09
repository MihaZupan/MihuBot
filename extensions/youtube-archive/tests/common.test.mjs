import assert from "node:assert/strict";
import { test } from "node:test";
import { readFile } from "node:fs/promises";
import { api, getYoutubeCookies, parseServer, parseVideo } from "../common.js";

test("canonicalizes individual videos and detects YouTube Music", () => {
  for (const url of [
    "https://www.youtube.com/watch?v=abcdefghijk&list=playlist",
    "https://youtu.be/abcdefghijk?t=10",
    "https://m.youtube.com/shorts/abcdefghijk",
    "https://youtube.com/embed/abcdefghijk",
    "https://www.youtube.com/live/abcdefghijk"
  ]) {
    assert.deepEqual(parseVideo(url), { url: "https://www.youtube.com/watch?v=abcdefghijk", isMusic: false });
  }

  assert.equal(parseVideo("https://music.youtube.com/watch?v=abcdefghijk").isMusic, true);
});

test("rejects playlists, arbitrary hosts, credentials, ports and invalid IDs", () => {
  for (const url of [
    "https://www.youtube.com/playlist?list=abc", "https://youtube.com.evil/watch?v=abcdefghijk",
    "https://www.youtube.com@evil/watch?v=abcdefghijk", "file:///abcdefghijk",
    "https://www.youtube.com/watch?v=short", "https://youtu.be/abcdefghijk/extra",
    "https://www.youtube.com/watch?v=abcdefghijk&v=12345678901",
    "https://user@www.youtube.com/watch?v=abcdefghijk",
    "https://www.youtube.com:8443/watch?v=abcdefghijk"
  ]) {
    assert.equal(parseVideo(url), null, url);
  }
});

test("requires HTTPS except for loopback, preserves reverse proxy base path", () => {
  assert.equal(parseServer("https://bot.example/prefix"), "https://bot.example/prefix/");
  assert.equal(parseServer("http://localhost:5000"), "http://localhost:5000/");
  assert.equal(parseServer("http://[::1]:5000"), "http://[::1]:5000/");
  for (const server of ["http://bot.example", "https://secret@bot.example", "https://bot.example/?token=abc", "https://bot.example/#x"]) {
    assert.throws(() => parseServer(server));
  }
});

test("API sends token only to configured backend and disallows redirects/cookies", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = async (url, options) => {
    assert.equal(url.href, "https://bot.example/prefix/api/YoutubeArchive/Jobs");
    assert.equal(options.headers.Authorization, `Bearer ${"a".repeat(32)}`);
    assert.equal(options.credentials, "omit");
    assert.equal(options.redirect, "error");
    return new Response(JSON.stringify([]), { status: 200 });
  };
  try {
    assert.deepEqual(await api({ server: "https://bot.example/prefix/", secret: "a".repeat(32) }, "Jobs"), []);
  } finally {
    globalThis.fetch = original;
  }
});

test("API surfaces structured errors and authentication failures", async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async () => new Response(JSON.stringify({ error: "queue full" }), { status: 429 });
    await assert.rejects(api({ server: "https://bot.example/", secret: "abc" }, "Jobs"), /queue full/);
    globalThis.fetch = async () => new Response("", { status: 401 });
    await assert.rejects(api({ server: "https://bot.example/", secret: "abc" }, "Jobs"), /Invalid shared secret/);
  } finally {
    globalThis.fetch = original;
  }
});

test("manifest is MV3 with no blanket host access or content scripts", async () => {
  const manifest = JSON.parse(await readFile(new URL("../manifest.json", import.meta.url)));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions, ["activeTab", "storage"]);
  assert.equal(manifest.host_permissions, undefined);
  assert.equal(manifest.content_scripts, undefined);
  assert.deepEqual(manifest.optional_permissions, ["cookies"]);
});

test("cookie export is opt-in, profile-specific, YouTube-only and excludes partitioned cookies", async () => {
  const original = globalThis.chrome;
  let calls = 0;
  globalThis.chrome = {
    permissions: { contains: async () => true },
    cookies: {
      getAllCookieStores: async () => [{ id: "other", tabIds: [1] }, { id: "active", tabIds: [42] }],
      getAll: async filter => {
        calls++;
        assert.deepEqual(filter, { domain: "youtube.com", storeId: "active" });
        const cookie = { domain: ".youtube.com", path: "/", name: "TEST_COOKIE", value: "dummy", secure: true, httpOnly: true, session: true };
        return [cookie, { ...cookie, domain: ".google.com" }, { ...cookie, partitionKey: { topLevelSite: "https://other.example" } }];
      }
    }
  };
  try {
    assert.equal(await getYoutubeCookies({ shareCookies: false }, 42), undefined);
    assert.equal(calls, 0);
    const cookies = await getYoutubeCookies({ shareCookies: true }, 42);
    assert.equal(cookies.length, 1);
    assert.equal(cookies[0].httpOnly, true);
    assert.equal(cookies[0].expires, null);
    await assert.rejects(getYoutubeCookies({ shareCookies: true }, 999), /cookie store/);
    globalThis.chrome.permissions.contains = async () => false;
    await assert.rejects(getYoutubeCookies({ shareCookies: true }, 42), /revoked/);
  } finally {
    if (original === undefined) delete globalThis.chrome;
    else globalThis.chrome = original;
  }
});
