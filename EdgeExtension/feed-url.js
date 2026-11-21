export function isSubscribablePageUrl(value) {
    if (typeof value !== "string") return false;
    try {
        const url = new URL(value);
        return url.protocol === "http:" || url.protocol === "https:";
    } catch {
        return false;
    }
}

export function subscriptionUrl(pageUrl, useDefaultReader = false) {
    if (!isSubscribablePageUrl(pageUrl)) {
        throw new Error("Open an HTTP or HTTPS page before subscribing.");
    }
    return `${useDefaultReader ? "feed" : "x-winnewswire-feed"}:${pageUrl}`;
}
