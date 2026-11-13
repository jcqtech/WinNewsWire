import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import vm from "node:vm";

function script(name) {
    return readFileSync(join(import.meta.dirname, "..", "Shared", "Resources", name), "utf8");
}

function frame(src, lazySrc) {
    return {
        src,
        getAttribute(name) { return name === "src" ? this.src : lazySrc; }
    };
}

function mainScript(frames) {
    const context = vm.createContext({
        document: { addEventListener() {}, querySelectorAll: () => frames }
    });
    vm.runInContext(script("main.js"), context);
    return context;
}

function openFootnote({ articleLeft = 100, articleWidth = 600, referenceLeft = 380 } = {}) {
    const listeners = new Map();
    const resizeListeners = new Set();
    let popover;
    const article = {
        getBoundingClientRect: () => ({ left: articleLeft, right: articleLeft + articleWidth, width: articleWidth })
    };
    const container = {
        style: {},
        matches: () => true,
        getBoundingClientRect: () => ({ left: referenceLeft, width: 20 }),
        insertBefore(element) { popover = element; }
    };
    class Element {
        style = {};
        children = [];
        clientWidth = 16;
        classList = { add() {} };
        appendChild(child) { this.children.push(child); }
        querySelector() { return this.children[1]; }
    }
    class HTMLAnchorElement extends Element {
        parentElement = container;
        hash = "#fn1";
        matches(selector) { return selector === ".footnote"; }
        closest(selector) { return selector === ".articleBody" ? article : container; }
        getBoundingClientRect() { return { left: referenceLeft, width: 20 }; }
    }
    const document = {
        createElement: () => new Element(),
        getElementById: () => ({ innerHTML: "Footnote text" }),
        documentElement: article,
        addEventListener(name, callback) {
            if (!listeners.has(name)) listeners.set(name, []);
            listeners.get(name).push(callback);
        }
    };
    const window = {
        innerWidth: 1400,
        getComputedStyle: () => ({ maxWidth: "704px", marginLeft: "0px", marginRight: "0px" }),
        addEventListener(name, callback) { if (name === "resize") resizeListeners.add(callback); }
    };
    vm.runInNewContext(script("newsfoot.js"), {
        document, window, Element, HTMLAnchorElement,
        Date: class extends Date { static now() { return 100; } }
    });
    listeners.get("click")[0]({ target: new HTMLAnchorElement(), preventDefault() {} });
    return {
        get popover() { return popover; },
        resize(left, width) {
            articleLeft = left;
            articleWidth = width;
            for (const listener of resizeListeners) listener();
        }
    };
}

test("footnotes stay within the article column rather than the whole web view", () => {
    const fixture = openFootnote();
    assert.equal(fixture.popover.style.width, "600px");
    assert.equal(fixture.popover.style.left, "-280px");
});

test("footnotes center on their reference and clamp the popover and arrow at column edges", () => {
    for (const [referenceLeft, left, arrowLeft] of [[100, "0px", "8px"], [1080, "-684px", "680px"], [600, "-342px", "344px"]]) {
        const fixture = openFootnote({ articleWidth: 1000, referenceLeft });
        assert.equal(fixture.popover.style.width, "704px");
        assert.equal(fixture.popover.style.left, left);
        assert.equal(fixture.popover.children[1].style.left, arrowLeft);
    }
});

test("each resize updates the open footnote, including the final rapid resize", () => {
    const fixture = openFootnote();
    fixture.resize(200, 300);
    assert.equal(fixture.popover.style.width, "300px");
    fixture.resize(220, 280);
    assert.equal(fixture.popover.style.width, "280px");
    assert.equal(fixture.popover.style.left, "-160px");
});

test("Optimole iframe placeholders load their HTTP or HTTPS source", () => {
    const frames = [frame(null, "https://example.test/a"), frame("", "http://example.test/b"),
        frame("about:blank", "HTTPS://example.test/c")];
    mainScript(frames).loadLazyIframes();
    assert.deepEqual(frames.map(item => item.src), ["https://example.test/a", "http://example.test/b", "HTTPS://example.test/c"]);
});

test("iframe lazy loading preserves active frames and rejects non-web sources", () => {
    const frames = [frame("https://example.test/current", "https://example.test/other"),
        ...["javascript:alert(1)", "data:text/html,test", "file:///test", "//example.test/video", "/video", null]
            .map(source => frame("about:blank", source))];
    const before = frames.map(item => item.src);
    mainScript(frames).loadLazyIframes();
    assert.deepEqual(frames.map(item => item.src), before);
});

test("page processing resolves lazy iframes before wrapping them", () => {
    const item = frame("about:blank", "https://example.test/video");
    const context = mainScript([item]);
    for (const name of ["stripStyles", "wrapTables", "inlineVideos", "sizeTailwindSVGs",
        "constrainBodyRelativeIframes", "convertImgSrc", "flattenPreElements", "styleLocalFootnotes",
        "removeWpSmiley", "removeInstapaperIgnoreElements", "postRenderProcessing"]) context[name] = () => {};
    context.wrapFrames = () => assert.equal(item.src, "https://example.test/video");
    context.processPage();
});
