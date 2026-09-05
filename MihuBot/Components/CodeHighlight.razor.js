let highlighterPromise;

function loadScript(url) {
    return new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = url;
        script.onload = resolve;
        script.onerror = () => reject(new Error(`Failed to load ${url}`));
        document.head.appendChild(script);
    });
}

async function loadHighlighter() {
    if (!globalThis.hljs) {
        await loadScript("https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js");
    }
    if (!globalThis.hljs.getLanguage("csharp")) {
        await loadScript("https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/languages/csharp.min.js");
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
