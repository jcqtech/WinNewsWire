import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { preferenceKey, subscriptionUrl } from "../subscription-url.js";

for (const url of [
  "https://example.com/article",
  "http://example.com/feed?name=a%2Bb&second=2#section",
  "https://example.com:8443/feed",
  "https://[::1]:8443/feed",
  "https://example.com/caf%C3%A9"
]) {
  test(`dedicated reader retains the complete page URL: ${url}`, () => {
    assert.equal(subscriptionUrl(url), "x-winnewswire-feed:" + url);
  });
  test(`default reader uses the native feed wrapper: ${url}`, () => {
    assert.equal(subscriptionUrl(url, true), "feed:" + url);
  });
}

for (const url of [
  undefined, null, "", "example.com", "https://example.com\nhttps://example.org",
  "javascript:alert(1)", "file:///C:/private.opml", "chrome://settings",
  "edge://extensions", "about:blank", "data:text/html,hello",
  "winnewswire://showunread", "ftp://example.com/feed"
]) {
  test(`non-web pages cannot invoke the reader: ${String(url)}`, () => {
    assert.throws(() => subscriptionUrl(url), TypeError);
  });
}

test("preference is explicit and does not reinterpret arbitrary stored values", () => {
  assert.equal(preferenceKey, "subscribeToFeedsInDefaultBrowser");
  assert.throws(() => subscriptionUrl("https://example.com", "false"), TypeError);
  assert.equal(subscriptionUrl("HTTPS://EXAMPLE.COM"), "x-winnewswire-feed:https://example.com/");
});

test("the extension needs no page scripts or broad browsing permissions", async () => {
  const manifest = JSON.parse(await readFile(new URL("../manifest.json", import.meta.url), "utf8"));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions, ["activeTab", "storage"]);
  assert.equal(manifest.host_permissions, undefined);
  assert.equal(manifest.content_scripts, undefined);
  assert.equal(manifest.background, undefined);
});
