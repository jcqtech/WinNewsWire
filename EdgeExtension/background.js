import { isSubscribablePageUrl, subscriptionUrl } from "./feed-url.js";

async function reportError(error) {
    console.error("Subscribe to Feed:", error);
    try {
        await chrome.tabs.create({
            url: chrome.runtime.getURL("options.html") + "#error=" + encodeURIComponent(error.message)
        });
    } catch (reportingError) {
        console.error("Could not display the subscription error:", reportingError);
        await chrome.action.setBadgeText({ text: "!" });
        await chrome.action.setTitle({ title: "Subscribe to Feed failed. Open the extension's Options for help." });
    }
}

// Rule-event APIs still require callbacks, unlike most Manifest V3 APIs.
function updateRules(method, rules) {
    return new Promise((resolve, reject) => {
        chrome.declarativeContent.onPageChanged[method](rules, () => {
            const error = chrome.runtime.lastError;
            if (error) reject(new Error(error.message));
            else resolve();
        });
    });
}

async function installRules() {
    await updateRules("removeRules", undefined);
    await updateRules("addRules", [{
        id: "subscribable-page",
        conditions: [new chrome.declarativeContent.PageStateMatcher({
            pageUrl: { schemes: ["http", "https"] }
        })],
        actions: [new chrome.declarativeContent.ShowAction()]
    }]);
}

async function subscribe(tab) {
    if (!isSubscribablePageUrl(tab.url)) return;
    const settings = await chrome.storage.local.get({ useDefaultReader: false });
    // Keep the source page intact. Edge handles the external-protocol confirmation.
    await chrome.tabs.create({ url: subscriptionUrl(tab.url, settings.useDefaultReader === true) });
}

chrome.runtime.onInstalled.addListener(() => installRules().catch(reportError));
chrome.action.onClicked.addListener(tab => subscribe(tab).catch(reportError));
