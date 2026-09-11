let highlighterPromise;

function loadScript(url, integrity) {
    return new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = url;
        script.integrity = integrity;
        script.crossOrigin = "anonymous";
        script.onload = resolve;
        script.onerror = () => reject(new Error(`Failed to load ${url}`));
        document.head.appendChild(script);
    });
}

async function loadHighlighter() {
    if (!globalThis.hljs) {
        await loadScript("https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.12.0/highlight.min.js",
            "sha384-wjfDDhOPPdjtva8vWBhWeVprSpmxisEu5aYT3q1JyACqXpdKpo3PWZTMVq24MBix");
    }
    if (!globalThis.hljs.getLanguage("csharp")) {
        await loadScript("https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.12.0/languages/csharp.min.js",
            "sha384-UmTmCSuac/JgV+xJdKhzfTJ6R7AIX7P48kgC024cGCbJSx6vePS4XPyMrzU5LW/v");
    }
    return globalThis.hljs;
}

export async function highlight(element, code) {
    const highlighter = await (highlighterPromise ??= loadHighlighter());
    if (!element.isConnected) {
        return;
    }
    element.textContent = code;
    element.removeAttribute("data-highlighted");
    highlighter.highlightElement(element);
}
