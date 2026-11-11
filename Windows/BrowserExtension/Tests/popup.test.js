import test from "node:test";
import assert from "node:assert/strict";

let nextModule = 0;

class Element {
  attributes = new Map();
  handlers = new Map();
  textContent = "";
  checked = false;
  disabled = false;
  set href(value) { this.attributes.set("href", value); }
  get href() { return this.attributes.get("href"); }
  setAttribute(key, value) { this.attributes.set(key, value); }
  getAttribute(key) { return this.attributes.get(key); }
  removeAttribute(key) { this.attributes.delete(key); }
  addEventListener(name, handler) { this.handlers.set(name, handler); }
}

async function popup({ url = "https://example.com/article?private=query", preference = false, failSave = false, failTabs = false } = {}, run) {
  const previousDocument = globalThis.document;
  const previousChrome = globalThis.chrome;
  const elements = Object.fromEntries(["page", "subscribe", "default-reader", "status"].map(id => [id, new Element()]));
  elements.subscribe.setAttribute("aria-disabled", "true");
  const writes = [];
  globalThis.document = { getElementById: id => elements[id] };
  globalThis.chrome = {
    tabs: {
      query: async options => {
        assert.deepEqual(options, { active: true, currentWindow: true });
        if (failTabs) throw new Error("Tab access denied.");
        return [{ active: true, url }];
      }
    },
    storage: {
      local: {
        get: async key => ({ [key]: preference }),
        set: async values => {
          if (failSave) throw new Error("Storage unavailable.");
          writes.push(values);
        }
      }
    }
  };
  try {
    await import(new URL(`../popup.js?case=${nextModule++}`, import.meta.url));
    await run(elements, writes);
  } finally {
    if (previousDocument === undefined) delete globalThis.document;
    else globalThis.document = previousDocument;
    if (previousChrome === undefined) delete globalThis.chrome;
    else globalThis.chrome = previousChrome;
  }
}

test("popup exposes a dedicated handoff without displaying query data", async () => {
  await popup({}, async elements => {
    assert.equal(elements.subscribe.href, "x-winnewswire-feed:https://example.com/article?private=query");
    assert.equal(elements.page.textContent, "example.com/article");
    assert.equal(elements.subscribe.getAttribute("aria-disabled"), "false");
    assert.equal(elements["default-reader"].checked, false);
  });
});

test("reader preference saves before changing the handoff", async () => {
  await popup({}, async (elements, writes) => {
    elements["default-reader"].checked = true;
    await elements["default-reader"].handlers.get("change")();
    assert.deepEqual(writes, [{ subscribeToFeedsInDefaultBrowser: true }]);
    assert.equal(elements.subscribe.href, "feed:https://example.com/article?private=query");
    assert.equal(elements["default-reader"].disabled, false);
  });
});

test("a failed write restores the previous reader and reports the error", async () => {
  await popup({ failSave: true }, async elements => {
    elements["default-reader"].checked = true;
    await elements["default-reader"].handlers.get("change")();
    assert.equal(elements["default-reader"].checked, false);
    assert.equal(elements.subscribe.href, "x-winnewswire-feed:https://example.com/article?private=query");
    assert.match(elements.status.textContent, /Could not save.*Storage unavailable/u);
  });
});

test("unsupported pages cannot launch a reader even after a successful preference save", async () => {
  await popup({ url: "chrome://settings", preference: true }, async (elements, writes) => {
    assert.equal(elements.subscribe.href, undefined);
    assert.equal(elements["default-reader"].checked, true);
    elements["default-reader"].checked = false;
    await elements["default-reader"].handlers.get("change")();
    assert.equal(writes.length, 1);
    assert.equal(elements.subscribe.href, undefined);
    assert.match(elements.status.textContent, /only for HTTP and HTTPS/u);
    assert.doesNotMatch(elements.status.textContent, /Could not save/u);
  });
});

test("denied tab access and invalid stored preferences do not enable a handoff", async () => {
  await popup({ failTabs: true }, async elements => {
    let prevented = false;
    elements.subscribe.handlers.get("click")({ preventDefault: () => { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(elements.subscribe.href, undefined);
    assert.match(elements.status.textContent, /Tab access denied/u);
  });
  await popup({ preference: "false", failSave: true }, async elements => {
    elements["default-reader"].checked = true;
    await elements["default-reader"].handlers.get("change")();
    assert.equal(elements.subscribe.href, undefined);
    assert.equal(elements["default-reader"].checked, false);
  });
});
