import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const appDirectory = fileURLToPath(new URL("../MihuBot/", import.meta.url));
const tooltipSource = await readFile(path.join(appDirectory, "wwwroot", "app.js"), "utf8");
const highlightSource = await readFile(path.join(appDirectory, "Components", "CodeHighlight.razor.js"), "utf8");

function tooltipHarness() {
    const listeners = new Map();
    const instances = [];
    let observer;
    let observedRoot;
    class Element {
        constructor({ tooltip = false, connected = false, children = [] } = {}) {
            this.tooltip = tooltip;
            this.isConnected = connected;
            this.children = children;
            this.hidden = 0;
            this.disposed = 0;
            this.instance = {
                hide: () => this.hidden++,
                dispose: () => this.disposed++
            };
        }
        matches() { return this.tooltip; }
        querySelectorAll() { return this.children; }
        closest() { return this.tooltip ? this : null; }
    }
    const body = new Element({ connected: true });
    vm.runInNewContext(tooltipSource, {
        Element,
        document: { body, addEventListener: (name, handler) => listeners.set(name, handler) },
        bootstrap: {
            Tooltip: class {
                constructor(element, options) { instances.push({ element, options }); }
                static getInstance(element) { return element.instance; }
            }
        },
        MutationObserver: class {
            constructor(callback) { observer = callback; }
            observe(root) { observedRoot = root; }
        }
    });
    return { Element, body, instances, listeners, observer, observedRoot };
}

test("tooltips delegate to dynamically rendered controls and hide without disabling future hovers", () => {
    const harness = tooltipHarness();
    assert.equal(harness.instances.length, 1);
    assert.equal(harness.instances[0].element, harness.body);
    assert.equal(harness.instances[0].options.selector, '[data-bs-toggle="tooltip"]');
    assert.equal(harness.instances[0].options.trigger, "hover");
    const control = new harness.Element({ tooltip: true, connected: true });
    harness.listeners.get("click")({ target: control });
    assert.equal(control.hidden, 1);
    assert.equal(control.disposed, 0);
    harness.listeners.get("click")({ target: new harness.Element() });
});

test("tooltip cleanup handles removed controls and subtrees but not moved nodes", () => {
    const harness = tooltipHarness();
    assert.equal(harness.observedRoot, harness.body);
    const control = new harness.Element({ tooltip: true });
    const nested = new harness.Element({ tooltip: true });
    const container = new harness.Element({ children: [nested] });
    const moved = new harness.Element({ tooltip: true, connected: true });
    harness.observer([{ removedNodes: [control, container, moved, { nodeType: 3 }] }]);
    assert.equal(control.disposed, 1);
    assert.equal(nested.disposed, 1);
    assert.equal(moved.disposed, 0);
});

function highlightHarness({ fail = false } = {}) {
    const scripts = [];
    const highlighted = [];
    let csharpLoaded = false;
    const highlighter = {
        getLanguage: language => language === "csharp" && csharpLoaded,
        highlightElement: element => highlighted.push(element)
    };
    const context = vm.createContext({
        document: {
            createElement: () => ({}),
            head: {
                appendChild(script) {
                    scripts.push(script);
                    queueMicrotask(() => {
                        if (fail) {
                            script.onerror();
                        } else {
                            context.hljs = highlighter;
                            if (script.src.includes("/languages/csharp.")) {
                                csharpLoaded = true;
                            }
                            script.onload();
                        }
                    });
                }
            }
        }
    });
    vm.runInContext(highlightSource.replace("export async function", "async function"), context);
    return { highlight: context.highlight, scripts, highlighted };
}

function codeElement(connected = true) {
    return {
        isConnected: connected,
        textContent: "",
        removedAttributes: [],
        removeAttribute(name) { this.removedAttributes.push(name); }
    };
}

test("concurrent highlighting loads core and C# once with integrity and preserves literal source", async () => {
    const harness = highlightHarness();
    const first = codeElement();
    const second = codeElement();
    const source = 'string value = "<script>not HTML</script>";';
    await Promise.all([harness.highlight(first, source), harness.highlight(second, "int x = 1;")]);
    assert.equal(harness.scripts.length, 2);
    assert.match(harness.scripts[0].src, /\/highlight\.min\.js$/);
    assert.match(harness.scripts[1].src, /\/languages\/csharp\.min\.js$/);
    for (const script of harness.scripts) {
        assert.match(script.integrity, /^sha384-[A-Za-z0-9+/]{64}$/);
        assert.equal(script.crossOrigin, "anonymous");
    }
    assert.equal(first.textContent, source);
    assert.deepEqual(first.removedAttributes, ["data-highlighted"]);
    assert.equal(harness.highlighted.length, 2);
    await harness.highlight(first, "int x = 2;");
    assert.equal(first.textContent, "int x = 2;");
    assert.equal(harness.scripts.length, 2);
    assert.equal(harness.highlighted.length, 3);
});

test("highlighting ignores detached controls and surfaces CDN failures", async () => {
    const harness = highlightHarness();
    const element = codeElement(false);
    await harness.highlight(element, "int x;");
    assert.equal(element.textContent, "");
    assert.equal(harness.highlighted.length, 0);
    const failed = highlightHarness({ fail: true });
    await assert.rejects(failed.highlight(codeElement(), "int x;"), /Failed to load https:/);
    assert.equal(failed.highlighted.length, 0);
});
