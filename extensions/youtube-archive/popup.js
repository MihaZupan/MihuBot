import { api, getYoutubeCookies, heights, parseVideo } from "./common.js";

const mode = document.querySelector("#mode");
const quality = document.querySelector("#quality");
const submit = document.querySelector("#submit");
const status = document.querySelector("#status");
let settings;
let video;
let tabId;
let modeChanged = false;
let refreshing = false;

document.querySelector("#settings").addEventListener("click", () => chrome.runtime.openOptionsPage());
mode.addEventListener("change", () => {
  modeChanged = true;
  quality.disabled = mode.value === "audio";
});
quality.addEventListener("change", async () => {
  try {
    await chrome.storage.local.set({ quality: quality.value });
  } catch (error) {
    status.textContent = `Could not remember quality: ${error.message}`;
  }
});

async function refreshJobs() {
  if (refreshing) {
    return;
  }

  refreshing = true;
  try {
    const jobs = await api(settings, "Jobs");
    const elements = jobs.slice(0, 10).map(job => {
      const row = document.createElement("p");
      row.textContent = `${job.videoId} - ${job.mode}${job.maxHeight ? ` ${job.maxHeight}p` : ""}: ${job.status}` +
        (job.file ? `\n${job.file}` : "") + (job.error ? `\n${job.error}` : "") +
        (job.warning ? `\n${job.warning}` : "");
      row.className = job.status === "failed" || job.warning ? "error" : "";
      return row;
    });
    document.querySelector("#jobs").replaceChildren(...elements);
  } catch (error) {
    document.querySelector("#jobs").textContent = `Cannot load job status: ${error.message}`;
  } finally {
    refreshing = false;
  }
}

document.querySelector("#archive").addEventListener("submit", async event => {
  event.preventDefault();
  submit.disabled = true;
  status.textContent = "Submitting...";
  try {
    await chrome.storage.local.set({ quality: quality.value });
    const cookies = await getYoutubeCookies(settings, tabId);
    const job = await api(settings, "Jobs", {
      method: "POST",
      body: JSON.stringify({
        url: video.url,
        mode: mode.value,
        maxHeight: mode.value === "audio" || quality.value === "best" ? null : Number(quality.value),
        cookies
      })
    });
    status.textContent = job.status === "completed" ? "Already archived." :
      "Queued on the server. You can close this popup or browser; archival continues in the background.";
    await refreshJobs();
  } catch (error) {
    status.textContent = `Archival request failed: ${error.message}`;
  } finally {
    submit.disabled = false;
  }
});

async function initialize() {
  await chrome.storage.local.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
  settings = await chrome.storage.local.get(["server", "secret", "quality", "shareCookies"]);
  if (!settings.server || !settings.secret) {
    status.textContent = "Open Settings to configure the server and shared secret once.";
    return;
  }

  quality.value = heights.includes(Number(settings.quality)) ? settings.quality : "best";
  refreshJobs();
  setInterval(refreshJobs, 3000);
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  tabId = tab?.id;
  video = parseVideo(tab?.url);
  if (!video) {
    status.textContent = "Open a single YouTube video (watch, Shorts, or YouTube Music). Playlists alone are not supported.";
    return;
  }

  document.querySelector("#title").textContent = tab.title || video.url;
  mode.value = video.isMusic ? "audio" : "video";
  quality.disabled = mode.value === "audio";
  status.textContent = "Checking video category...";
  try {
    const cookies = await getYoutubeCookies(settings, tabId);
    const metadata = cookies === undefined
      ? await api(settings, `Metadata?url=${encodeURIComponent(video.url)}`)
      : await api(settings, "Metadata", { method: "POST", body: JSON.stringify({ url: video.url, cookies }) });
    document.querySelector("#title").textContent = metadata.title;
    if (metadata.isLive) {
      status.textContent = "This video is live or still processing. Archive it after it finishes processing.";
      return;
    }

    if (!modeChanged) {
      mode.value = metadata.isMusic || video.isMusic ? "audio" : "video";
      quality.disabled = mode.value === "audio";
    }

    status.textContent = (metadata.isMusic || video.isMusic) && !modeChanged ? "Music detected: audio-only selected by default." : "";
  } catch (error) {
    status.textContent = `Category lookup failed: ${error.message} Choose audio/video manually.`;
  }

  submit.disabled = false;
}

initialize().catch(error => { status.textContent = error.message; });
