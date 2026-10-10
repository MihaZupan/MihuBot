import assert from "node:assert/strict";
import { test } from "node:test";

let sequence = 0;

async function harness({ isMusic = false, tabUrl = "https://youtube.com/watch?v=abcdefghijk", quality = "1080", metadataError = false, isLive = false, shareCookies = false, deferMetadata = false } = {}) {
  const saved = { server: "https://archive.example/", secret: "x".repeat(64), quality, shareCookies };
  const requests = [];
  const elements = new Map();
  const originals = new Map(["document", "chrome", "fetch", "setInterval"].map(key => [key, globalThis[key]]));
  const interval = [];
  let resolveMetadata;
  const metadataReady = deferMetadata ? new Promise(resolve => { resolveMetadata = resolve; }) : Promise.resolve();
  const element = () => ({
    value: "", textContent: "", disabled: true, listeners: new Map(),
    addEventListener(name, listener) { this.listeners.set(name, listener); },
    replaceChildren(...children) { this.children = children; }
  });
  for (const name of ["mode", "quality", "submit", "status", "detection", "settings", "archive", "title", "jobs"]) {
    elements.set(`#${name}`, element());
  }

  globalThis.document = {
    querySelector: selector => elements.get(selector),
    createElement: element
  };
  globalThis.chrome = {
    storage: { local: {
      setAccessLevel: async options => assert.equal(options.accessLevel, "TRUSTED_CONTEXTS"),
      get: async () => ({ ...saved }),
      set: async values => Object.assign(saved, values)
    } },
    tabs: { query: async () => [{ id: 42, url: tabUrl, title: "Video title" }] },
    permissions: { contains: async () => true },
    cookies: {
      getAllCookieStores: async () => [{ id: "profile", tabIds: [42] }],
      getAll: async () => [{ domain: ".youtube.com", path: "/", name: "TEST_COOKIE", value: "dummy-cookie-value", secure: true, httpOnly: true, session: true }]
    },
    runtime: { openOptionsPage: () => {} }
  };
  globalThis.setInterval = fn => interval.push(fn);
  globalThis.fetch = async (url, options) => {
    requests.push({ url: String(url), options });
    if (String(url).includes("Metadata")) {
      await metadataReady;
      return metadataError ? new Response(JSON.stringify({ error: "YouTube unavailable" }), { status: 503 }) :
        new Response(JSON.stringify({ title: "Fetched title", isMusic, isLive }));
    }

    if (options.method === "POST") {
      return new Response(JSON.stringify({ status: "queued" }), { status: 202 });
    }

    return new Response("[]");
  };

  await import(`../popup.js?test=${sequence++}`);
  const wait = async predicate => {
    for (let i = 0; i < 100; i++) {
      if (predicate()) return;
      await new Promise(resolve => setTimeout(resolve, 1));
    }

    throw new Error("Popup did not finish initialization.");
  };
  await wait(() => deferMetadata
    ? requests.some(request => request.url.includes("Metadata"))
    : /single YouTube/.test(elements.get("#status").textContent) ||
      (requests.some(request => request.url.includes("Metadata")) &&
       !elements.get("#detection").textContent.startsWith("Checking")));
  return {
    saved, requests, interval,
    get: name => elements.get(`#${name}`),
    async emit(name, event) { await elements.get(`#${name}`).listeners.get(event)({ preventDefault() {} }); },
    async finishDetection() {
      resolveMetadata();
      await wait(() => !elements.get("#detection").textContent.startsWith("Checking"));
    },
    dispose() {
      for (const [key, value] of originals) {
        if (value === undefined) delete globalThis[key];
        else globalThis[key] = value;
      }
    }
  };
}

test("music defaults to original audio and remembers quality independently of mode", async () => {
  const ui = await harness({ isMusic: true, quality: "720" });
  try {
    assert.equal(ui.get("mode").value, "audio");
    assert.equal(ui.get("quality").value, "720");
    assert.equal(ui.get("quality").disabled, true);
    await ui.emit("archive", "submit");
    const post = ui.requests.find(request => request.options.method === "POST");
    assert.deepEqual(JSON.parse(post.options.body), { url: "https://www.youtube.com/watch?v=abcdefghijk", mode: "audio", maxHeight: null });
    assert.match(ui.get("status").textContent, /close this popup or browser/);
  } finally {
    ui.dispose();
  }
});

test("non-music defaults to video and changing resolution is persisted and submitted", async () => {
  const ui = await harness();
  try {
    assert.equal(ui.get("mode").value, "video");
    assert.equal(ui.get("quality").disabled, false);
    ui.get("quality").value = "2160";
    await ui.emit("quality", "change");
    assert.equal(ui.saved.quality, "2160");
    await ui.emit("archive", "submit");
    assert.equal(JSON.parse(ui.requests.find(request => request.options.method === "POST").options.body).maxHeight, 2160);
  } finally {
    ui.dispose();
  }
});

test("YouTube Music defaults to audio even if video metadata has another category", async () => {
  const ui = await harness({ tabUrl: "https://music.youtube.com/watch?v=abcdefghijk" });
  try {
    assert.equal(ui.get("mode").value, "audio");
    ui.get("mode").value = "video";
    await ui.emit("mode", "change");
    assert.equal(ui.get("quality").disabled, false);
  } finally {
    ui.dispose();
  }
});

test("failed category detection explicitly allows manual archival and best quality", async () => {
  const ui = await harness({ metadataError: true, quality: "invalid" });
  try {
    assert.match(ui.get("detection").textContent, /Category lookup failed: YouTube unavailable/);
    assert.equal(ui.get("submit").disabled, false);
    assert.equal(ui.get("quality").value, "best");
    await ui.emit("archive", "submit");
    assert.equal(JSON.parse(ui.requests.find(request => request.options.method === "POST").options.body).maxHeight, null);
  } finally {
    ui.dispose();
  }
});

test("audio can be queued before detection and a late non-music result preserves the selection and submission status", async () => {
  const ui = await harness({ deferMetadata: true });
  try {
    assert.equal(ui.get("mode").value, "audio");
    assert.equal(ui.get("quality").disabled, true);
    assert.equal(ui.get("submit").disabled, false);
    await ui.emit("archive", "submit");
    const post = ui.requests.find(request => request.url.endsWith("/Jobs") && request.options.method === "POST");
    assert.equal(JSON.parse(post.options.body).mode, "audio");
    assert.equal(JSON.parse(post.options.body).maxHeight, null);
    const submitted = ui.get("status").textContent;
    await ui.finishDetection();
    assert.equal(ui.get("mode").value, "audio");
    assert.equal(ui.get("status").textContent, submitted);
  } finally {
    ui.dispose();
  }
});

test("manual video selection while checking is preserved when music detection finishes", async () => {
  const ui = await harness({ deferMetadata: true, isMusic: true });
  try {
    ui.get("mode").value = "video";
    await ui.emit("mode", "change");
    await ui.emit("archive", "submit");
    await ui.finishDetection();
    assert.equal(ui.get("mode").value, "video");
    const post = ui.requests.find(request => request.url.endsWith("/Jobs") && request.options.method === "POST");
    assert.equal(JSON.parse(post.options.body).maxHeight, 1080);
  } finally {
    ui.dispose();
  }
});

test("late live detection disables further submissions without overwriting the queued result", async () => {
  const ui = await harness({ deferMetadata: true, isLive: true });
  try {
    await ui.emit("archive", "submit");
    await ui.finishDetection();
    assert.equal(ui.get("submit").disabled, true);
    assert.match(ui.get("status").textContent, /Queued on the server/);
    assert.match(ui.get("detection").textContent, /live or still processing/);
  } finally {
    ui.dispose();
  }
});

test("live videos and playlist-only pages cannot be submitted", async () => {
  for (const options of [{ isLive: true }, { tabUrl: "https://www.youtube.com/playlist?list=abc" }]) {
    const ui = await harness(options);
    try {
      assert.equal(ui.get("submit").disabled, true);
      assert.equal(ui.requests.some(request => request.options.method === "POST"), false);
    } finally {
      ui.dispose();
    }
  }
});

test("opt-in cookies accompany metadata and archive POST bodies, never URLs or settings", async () => {
  const ui = await harness({ shareCookies: true });
  try {
    const lookup = ui.requests.find(request => request.url.includes("Metadata"));
    assert.equal(lookup.options.method, "POST");
    assert.equal(JSON.parse(lookup.options.body).cookies[0].value, "dummy-cookie-value");
    await ui.emit("archive", "submit");
    const post = ui.requests.find(request => request.url.endsWith("/Jobs") && request.options.method === "POST");
    assert.equal(JSON.parse(post.options.body).cookies[0].httpOnly, true);
    assert.equal(JSON.parse(post.options.body).cookies[0].expires, null);
    assert.equal(ui.requests.some(request => request.url.includes("dummy-cookie-value")), false);
    assert.equal(JSON.stringify(ui.saved).includes("dummy-cookie-value"), false);
  } finally {
    ui.dispose();
  }
});
