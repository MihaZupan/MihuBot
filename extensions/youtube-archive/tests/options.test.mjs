import assert from "node:assert/strict";
import { test } from "node:test";

let sequence = 0;

async function harness(shareCookies) {
  const saved = { server: "https://archive.example/", secret: "x".repeat(64), shareCookies };
  const requests = [];
  const removed = [];
  let submit;
  const status = { textContent: "" };
  const button = { disabled: false };
  const form = {
    elements: { server: { value: "" }, secret: { value: "" }, shareCookies: { checked: false } },
    addEventListener: (_, handler) => { submit = handler; },
    querySelector: () => button
  };
  const original = new Map(["chrome", "document", "fetch"].map(key => [key, globalThis[key]]));
  globalThis.document = { querySelector: selector => selector === "#settings" ? form : status };
  globalThis.chrome = {
    storage: { local: {
      setAccessLevel: async () => {},
      get: async () => ({ ...saved }),
      set: async values => Object.assign(saved, values)
    } },
    permissions: {
      request: async request => { requests.push(request); return true; },
      remove: async request => { removed.push(request); return true; }
    }
  };
  globalThis.fetch = async () => new Response("[]");
  await import(`../options.js?test=${sequence++}`);
  return {
    form, saved, requests, removed, status,
    submit: () => submit({ preventDefault() {} }),
    dispose() {
      for (const [key, value] of original) {
        if (value === undefined) delete globalThis[key];
        else globalThis[key] = value;
      }
    }
  };
}

test("saving cookie sharing explicitly requests cookie and YouTube host permissions", async () => {
  const ui = await harness(false);
  try {
    ui.form.elements.shareCookies.checked = true;
    await ui.submit();
    assert.deepEqual(ui.requests, [{ origins: ["https://archive.example/*", "https://*.youtube.com/*"], permissions: ["cookies"] }]);
    assert.equal(ui.saved.shareCookies, true);
    assert.deepEqual(ui.removed, []);
    assert.match(ui.status.textContent, /Saved/);
  } finally {
    ui.dispose();
  }
});

test("disabling cookie sharing revokes cookies permission and remembers opt-out", async () => {
  const ui = await harness(true);
  try {
    ui.form.elements.shareCookies.checked = false;
    await ui.submit();
    assert.deepEqual(ui.requests, [{ origins: ["https://archive.example/*"] }]);
    assert.equal(ui.saved.shareCookies, false);
    assert.deepEqual(ui.removed, [{ permissions: ["cookies"] }]);
  } finally {
    ui.dispose();
  }
});
