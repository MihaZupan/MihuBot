import { api, parseServer } from "./common.js";

const form = document.querySelector("#settings");
const status = document.querySelector("#status");
await chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
const saved = await chrome.storage.local.get(["server", "secret", "shareCookies"]);
form.elements.server.value = saved.server || "";
form.elements.secret.value = saved.secret || "";
form.elements.shareCookies.checked = saved.shareCookies === true;

form.addEventListener("submit", async event => {
  event.preventDefault();
  status.textContent = "";
  const button = form.querySelector("button");
  button.disabled = true;
  try {
    const server = parseServer(form.elements.server.value);
    const secret = form.elements.secret.value;
    if (secret.length < 32 || secret.length > 2048) {
      throw new Error("Use a shared secret between 32 and 2048 characters.");
    }

    const url = new URL(server);
    const origin = `${url.protocol}//${url.hostname}/*`;
    const shareCookies = form.elements.shareCookies.checked;
    const permissions = shareCookies
      ? { origins: [origin, "https://*.youtube.com/*"], permissions: ["cookies"] }
      : { origins: [origin] };
    if (!await chrome.permissions.request(permissions)) {
      throw new Error("Server access permission was not granted.");
    }

    await api({ server, secret }, "Jobs");
    await chrome.storage.local.set({ server, secret, shareCookies });
    if (!shareCookies) {
      await chrome.permissions.remove({ permissions: ["cookies"] });
    }

    status.textContent = "Saved. Open a YouTube video and click the extension's toolbar button.";
  } catch (error) {
    status.textContent = error.message;
  } finally {
    button.disabled = false;
  }
});
