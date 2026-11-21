const form = document.querySelector("#settings");
const errorMessage = document.querySelector("#error");
const status = document.querySelector("#status");
const fieldset = form.querySelector("fieldset");
const save = form.querySelector("button");

function showError(message) {
    errorMessage.textContent = message;
    errorMessage.hidden = false;
}

if (location.hash.startsWith("#error=")) {
    try {
        showError(decodeURIComponent(location.hash.slice("#error=".length)));
    } catch {
        showError("Could not read the subscription error. Try subscribing again.");
    }
    history.replaceState(null, "", location.pathname);
}

try {
    const settings = await chrome.storage.local.get({ useDefaultReader: false });
    form.elements.reader.value = settings.useDefaultReader === true ? "default" : "winnewswire";
    fieldset.disabled = false;
    save.disabled = false;
} catch (error) {
    showError("Could not load settings: " + error.message);
}

form.addEventListener("submit", async event => {
    event.preventDefault();
    save.disabled = true;
    status.textContent = "";
    try {
        await chrome.storage.local.set({ useDefaultReader: form.elements.reader.value === "default" });
        errorMessage.hidden = true;
        status.textContent = "Settings saved.";
    } catch (error) {
        showError("Could not save settings: " + error.message);
    } finally {
        save.disabled = false;
    }
});
