import { preferenceKey, subscriptionUrl } from "./subscription-url.js";

const pageLabel = document.getElementById("page");
const subscribe = document.getElementById("subscribe");
const defaultReader = document.getElementById("default-reader");
const status = document.getElementById("status");
let pageUrl;
let savedPreference = false;
let preferenceReady = false;

function disableSubscription() {
  subscribe.removeAttribute("href");
  subscribe.setAttribute("aria-disabled", "true");
}

function updateSubscription() {
  if (!preferenceReady) {
    disableSubscription();
    return;
  }
  subscribe.href = subscriptionUrl(pageUrl, savedPreference);
  subscribe.textContent = savedPreference ? "Open in default feed reader" : "Open in WinNewsWire";
  subscribe.setAttribute("aria-disabled", "false");
}

async function initialize() {
  try {
    const settings = await chrome.storage.local.get(preferenceKey);
    const storedPreference = settings[preferenceKey] ?? false;
    if (typeof storedPreference !== "boolean") {
      throw new TypeError("Choose whether to use the system's default feed reader.");
    }
    savedPreference = storedPreference;
    preferenceReady = true;
    defaultReader.checked = savedPreference;
    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    const active = tabs.find(tab => tab.active);
    pageUrl = active?.url;
    subscriptionUrl(pageUrl, savedPreference);
    const page = new URL(pageUrl);
    pageLabel.textContent = page.hostname + page.pathname;
    updateSubscription();
    status.textContent = "Feed discovery is performed by the reader, not by this extension.";
  } catch (error) {
    disableSubscription();
    status.textContent = error instanceof Error ? error.message : "The current page could not be read.";
  }
}

defaultReader.addEventListener("change", async () => {
  const requested = defaultReader.checked;
  defaultReader.disabled = true;
  disableSubscription();
  try {
    await chrome.storage.local.set({ [preferenceKey]: requested });
  } catch (error) {
    defaultReader.checked = savedPreference;
    try { updateSubscription(); } catch { disableSubscription(); }
    status.textContent = "Could not save the reader preference: " + (error instanceof Error ? error.message : String(error));
    defaultReader.disabled = false;
    return;
  }
  savedPreference = requested;
  preferenceReady = true;
  try {
    updateSubscription();
    status.textContent = "Reader preference saved for this browser.";
  } catch (error) {
    disableSubscription();
    status.textContent = error instanceof Error ? error.message : "The current page cannot be subscribed.";
  } finally {
    defaultReader.disabled = false;
  }
});

subscribe.addEventListener("click", event => {
  if (subscribe.getAttribute("aria-disabled") === "true") {
    event.preventDefault();
    return;
  }
  status.textContent = "Opening the reader. No feed is added until you confirm in the app.";
});

await initialize();
