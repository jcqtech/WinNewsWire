import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";
import { isSubscribablePageUrl, subscriptionUrl } from "../feed-url.js";

const source = name => readFileSync(new URL("../" + name, import.meta.url), "utf8");

function worker({ settings = {}, storageError, launchError, helpError, rulesError } = {}) {
    const calls = { tabs: [], errors: [], rules: [], removed: [] };
    const listeners = {};
    const chrome = {
        runtime: {
            getURL: path => "chrome-extension://test/" + path,
            onInstalled: { addListener: listener => { listeners.install = listener; } }
        },
        action: {
            onClicked: { addListener: listener => { listeners.click = listener; } },
            setBadgeText: async value => { calls.badge = value.text; },
            setTitle: async value => { calls.title = value.title; }
        },
        declarativeContent: {
            PageStateMatcher: function (value) { Object.assign(this, value); },
            ShowAction: function () { this.showAction = true; },
            onPageChanged: {
                removeRules: (ids, callback) => { calls.removed.push(ids); callback(); },
                addRules: (rules, callback) => {
                    if (rulesError) chrome.runtime.lastError = { message: rulesError.message };
                    else calls.rules.push(...rules);
                    callback();
                    delete chrome.runtime.lastError;
                }
            }
        },
        storage: {
            local: {
                get: async defaults => {
                    if (storageError) throw storageError;
                    return { ...defaults, ...settings };
                }
            }
        },
        tabs: {
            create: async details => {
                calls.tabs.push(details);
                if (details.url.startsWith("chrome-extension:") ? helpError : launchError) {
                    throw (details.url.startsWith("chrome-extension:") ? helpError : launchError);
                }
            }
        }
    };
    vm.runInNewContext(source("background.js").replace(/^import .*;\r?\n/, ""), {
        chrome, isSubscribablePageUrl, subscriptionUrl,
        console: { error: (...args) => calls.errors.push(args) }
    });
    return { calls, ...listeners };
}

for (const url of [
    "https://example.com/", "http://example.com/feed.xml",
    "https://example.com/?q=a%26b&url=https%3A%2F%2Fother.test#section",
    "HTTPS://example.com/Page", "https://example.com/caf%C3%A9"
]) {
    test("wraps the complete web address: " + url, () => {
        assert.equal(isSubscribablePageUrl(url), true);
        assert.equal(subscriptionUrl(url), "x-winnewswire-feed:" + url);
        assert.equal(subscriptionUrl(url, true), "feed:" + url);
    });
}

test("rejects non-web, missing, and malformed URLs", () => {
    for (const url of [
        undefined, null, "", "not a URL", "https://", "edge://settings",
        "chrome://extensions", "file:///C:/private.xml", "javascript:alert(1)",
        "data:text/html,test", "chrome-extension://test/options.html",
        "feed:https://example.com/", { toString: () => "https://example.com/" }
    ]) {
        assert.equal(isSubscribablePageUrl(url), false);
        assert.throws(() => subscriptionUrl(url), /HTTP or HTTPS/);
    }
});

test("installation replaces persisted rules with HTTP(S)-only action enablement", async () => {
    const fixture = worker();
    await fixture.install();
    assert.deepEqual(fixture.calls.removed, [undefined]);
    assert.equal(fixture.calls.rules.length, 1);
    const rule = fixture.calls.rules[0];
    assert.equal(rule.id, "subscribable-page");
    assert.deepEqual(Array.from(rule.conditions[0].pageUrl.schemes), ["http", "https"]);
    assert.equal(rule.actions[0].showAction, true);
    await fixture.install();
    assert.equal(fixture.calls.removed.length, 2);
});

test("toolbar click targets WinNewsWire by default without navigating the source tab", async () => {
    const fixture = worker();
    await fixture.click({ id: 17, url: "https://example.com/?x=1&y=2#feed" });
    assert.equal(fixture.calls.tabs.length, 1);
    assert.equal(fixture.calls.tabs[0].url, "x-winnewswire-feed:https://example.com/?x=1&y=2#feed");
    assert.equal(fixture.calls.errors.length, 0);
});

test("the saved default-reader preference changes the handoff protocol", async () => {
    const fixture = worker({ settings: { useDefaultReader: true } });
    await fixture.click({ url: "http://example.com/rss" });
    assert.equal(fixture.calls.tabs[0].url, "feed:http://example.com/rss");
});

test("invalid settings never silently select a different reader", async () => {
    const fixture = worker({ settings: { useDefaultReader: "false" } });
    await fixture.click({ url: "https://example.com/" });
    assert.equal(fixture.calls.tabs[0].url, "x-winnewswire-feed:https://example.com/");
});

test("stale clicks on unsupported or unavailable URLs never launch an app", async () => {
    const fixture = worker();
    for (const url of [undefined, "edge://settings", "file:///C:/private.xml", "javascript:alert(1)"]) {
        await fixture.click({ url });
    }
    assert.equal(fixture.calls.tabs.length, 0);
});

test("storage failure is surfaced rather than launching the wrong reader", async () => {
    const fixture = worker({ storageError: new Error("Storage unavailable") });
    await fixture.click({ url: "https://example.com/" });
    assert.equal(fixture.calls.errors.length, 1);
    assert.equal(fixture.calls.tabs.length, 1);
    assert.equal(fixture.calls.tabs[0].url, "chrome-extension://test/options.html#error=Storage%20unavailable");
});

test("handoff failure opens help with safely encoded error text", async () => {
    const fixture = worker({ launchError: new Error("Blocked <app> & protocol #1") });
    await fixture.click({ url: "https://example.com/" });
    assert.equal(fixture.calls.errors.length, 1);
    assert.equal(fixture.calls.tabs[1].url,
        "chrome-extension://test/options.html#error=Blocked%20%3Capp%3E%20%26%20protocol%20%231");
});

test("rule setup failures are visible", async () => {
    const fixture = worker({ rulesError: new Error("Rules unavailable") });
    await fixture.install();
    assert.match(fixture.calls.tabs[0].url, /#error=Rules%20unavailable$/);
});

test("if help cannot open, the toolbar displays a failure indicator", async () => {
    const fixture = worker({
        launchError: new Error("Launch blocked"), helpError: new Error("Tabs blocked")
    });
    await fixture.click({ url: "https://example.com/" });
    assert.equal(fixture.calls.errors.length, 2);
    assert.equal(fixture.calls.badge, "!");
    assert.match(fixture.calls.title, /failed/);
});

async function options({ settings = {}, getError, setError, hash = "" } = {}) {
    const error = { hidden: true, textContent: "" };
    const status = { textContent: "" };
    const fieldset = { disabled: true };
    const button = { disabled: true };
    let submit;
    const form = {
        elements: { reader: { value: "winnewswire" } },
        querySelector: selector => selector === "fieldset" ? fieldset : button,
        addEventListener: (name, listener) => { if (name === "submit") submit = listener; }
    };
    const saved = [];
    const history = [];
    const context = {
        document: { querySelector: selector => ({ "#settings": form, "#error": error, "#status": status })[selector] },
        location: { hash, pathname: "/options.html" },
        history: { replaceState: (...args) => history.push(args) },
        chrome: {
            storage: {
                local: {
                    get: async defaults => {
                        if (getError) throw getError;
                        return { ...defaults, ...settings };
                    },
                    set: async value => {
                        if (setError) throw setError;
                        saved.push(value);
                    }
                }
            }
        }
    };
    await vm.runInNewContext("(async () => {\n" + source("options.js") + "\n})()", context);
    return { form, error, status, fieldset, button, saved, history,
        submit: () => submit({ preventDefault() {} }) };
}

test("settings restore the reader choice and persist changes with confirmation", async () => {
    const fixture = await options({ settings: { useDefaultReader: true } });
    assert.equal(fixture.form.elements.reader.value, "default");
    assert.equal(fixture.fieldset.disabled, false);
    fixture.form.elements.reader.value = "winnewswire";
    await fixture.submit();
    assert.equal(fixture.saved[0].useDefaultReader, false);
    fixture.form.elements.reader.value = "default";
    await fixture.submit();
    assert.equal(fixture.saved[1].useDefaultReader, true);
    assert.equal(fixture.status.textContent, "Settings saved.");
    assert.equal(fixture.button.disabled, false);
});

test("settings remain disabled when the saved choice cannot be read", async () => {
    const fixture = await options({ getError: new Error("Read failed") });
    assert.equal(fixture.fieldset.disabled, true);
    assert.equal(fixture.button.disabled, true);
    assert.equal(fixture.error.hidden, false);
    assert.match(fixture.error.textContent, /Read failed/);
});

test("settings save errors do not claim success and allow retry", async () => {
    const fixture = await options({ setError: new Error("Write failed") });
    await fixture.submit();
    assert.equal(fixture.status.textContent, "");
    assert.equal(fixture.error.hidden, false);
    assert.match(fixture.error.textContent, /Write failed/);
    assert.equal(fixture.button.disabled, false);
});

test("help renders launch errors as text and removes them from the address", async () => {
    const fixture = await options({ hash: "#error=" + encodeURIComponent("<script>alert(1)</script>") });
    assert.equal(fixture.error.textContent, "<script>alert(1)</script>");
    assert.equal(fixture.error.hidden, false);
    assert.equal(fixture.history[0][2], "/options.html");
});

test("malformed help fragments show an error without breaking settings", async () => {
    const fixture = await options({ hash: "#error=%zz" });
    assert.match(fixture.error.textContent, /Could not read/);
    assert.equal(fixture.button.disabled, false);
});

test("the manifest loads existing assets without page scripts or broad site permissions", () => {
    const manifest = JSON.parse(source("manifest.json"));
    assert.equal(manifest.manifest_version, 3);
    assert.equal(manifest.background.type, "module");
    assert.equal(manifest.action.default_state, "disabled");
    assert.deepEqual(manifest.permissions.toSorted(), ["activeTab", "declarativeContent", "storage"].toSorted());
    assert.equal(manifest.content_scripts, undefined);
    assert.equal(manifest.host_permissions, undefined);
    for (const asset of [
        manifest.background.service_worker, manifest.options_ui.page,
        ...Object.values(manifest.icons), ...Object.values(manifest.action.default_icon)
    ]) {
        assert.ok(existsSync(new URL("../" + asset, import.meta.url)), asset);
    }
});
