export const preferenceKey = "subscribeToFeedsInDefaultBrowser";

export function subscriptionUrl(pageUrl, useDefaultReader = false) {
  if (typeof useDefaultReader !== "boolean") {
    throw new TypeError("Choose whether to use the system's default feed reader.");
  }
  if (typeof pageUrl !== "string" || !pageUrl.trim() || /[\u0000-\u0020\u007f]/u.test(pageUrl)) {
    throw new TypeError("The active tab must have a valid web page URL.");
  }
  const page = new URL(pageUrl);
  if (page.protocol !== "http:" && page.protocol !== "https:") {
    throw new TypeError("Subscriptions are available only for HTTP and HTTPS pages.");
  }
  return `${useDefaultReader ? "feed:" : "x-winnewswire-feed:"}${page.href}`;
}
