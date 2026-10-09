export const heights = [360, 480, 720, 1080, 1440, 2160, 4320];

export function parseVideo(value) {
  let url;
  try {
    url = new URL(value);
  } catch {
    return null;
  }

  if (!["https:", "http:"].includes(url.protocol) || url.port || url.username || url.password) {
    return null;
  }

  const segments = url.pathname.replace(/^\/|\/$/g, "").split("/");
  let id;
  if (url.hostname === "youtu.be" && segments.length === 1) {
    id = segments[0];
  } else if (["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com"].includes(url.hostname)) {
    if (url.pathname === "/watch" && url.searchParams.getAll("v").length === 1) {
      id = url.searchParams.get("v");
    } else if (segments.length === 2 && ["shorts", "embed", "live"].includes(segments[0])) {
      id = segments[1];
    }
  }

  return /^[A-Za-z0-9_-]{11}$/.test(id ?? "")
    ? { url: `https://www.youtube.com/watch?v=${id}`, isMusic: url.hostname === "music.youtube.com" }
    : null;
}

export function parseServer(value) {
  const url = new URL(value.trim());
  const local = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
  if ((url.protocol !== "https:" && !(url.protocol === "http:" && local)) ||
      url.username || url.password || url.search || url.hash) {
    throw new Error("Use an HTTPS server URL (HTTP is allowed only on localhost). Do not include credentials, query, or fragment.");
  }

  url.pathname = url.pathname.replace(/\/+$/, "") + "/";
  return url.href;
}

export async function getYoutubeCookies(settings, tabId) {
  if (!settings.shareCookies) {
    return undefined;
  }

  if (!await chrome.permissions.contains({ permissions: ["cookies"], origins: ["https://*.youtube.com/*"] })) {
    throw new Error("YouTube cookie access was revoked. Enable cookie sharing again in Settings.");
  }

  const stores = await chrome.cookies.getAllCookieStores();
  const store = stores.find(candidate => candidate.tabIds.includes(tabId));
  if (!store) {
    throw new Error("Cannot identify the active tab's cookie store.");
  }

  const cookies = await chrome.cookies.getAll({ domain: "youtube.com", storeId: store.id });
  return cookies.filter(cookie => !cookie.partitionKey &&
    (cookie.domain === "youtube.com" || cookie.domain.endsWith(".youtube.com")))
    .map(cookie => ({
      domain: cookie.domain,
      path: cookie.path,
      name: cookie.name,
      value: cookie.value,
      secure: cookie.secure,
      httpOnly: cookie.httpOnly,
      expires: cookie.session ? null : Math.floor(cookie.expirationDate)
    }));
}

export async function api(settings, path, options = {}) {
  const response = await fetch(new URL(`api/YoutubeArchive/${path}`, settings.server), {
    ...options,
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${settings.secret}` },
    credentials: "omit",
    redirect: "error",
    cache: "no-store",
    signal: AbortSignal.timeout(60000)
  });

  if (!response.ok) {
    let message;
    const text = await response.text();
    try {
      const body = JSON.parse(text);
      message = body.error || body.detail || body.title;
    } catch {
      // A reverse proxy may return HTML rather than the API's JSON errors.
    }

    throw new Error(message || (response.status === 401 ? "Invalid shared secret." :
      response.status === 404 ? "Archival API not found. Check the server URL and backend configuration." :
        `Server returned HTTP ${response.status}.`));
  }

  return response.json();
}
