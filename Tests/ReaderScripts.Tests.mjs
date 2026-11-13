import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";
import vm from "node:vm";

function script(name) {
    return readFileSync(join(import.meta.dirname, "..", "Shared", "Resources", name), "utf8");
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
    vm.runInNewContext(script("newsfoot.js"), { document, window, Element, HTMLAnchorElement });
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
