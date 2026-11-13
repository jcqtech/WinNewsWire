function postRenderProcessing() {
    if (getComputedStyle(document.body).fontFamily.includes("-apple-system"))
        document.body.style.fontFamily = '"Segoe UI Variable", "Segoe UI", sans-serif';
    window.__wnwArticleProcessed = true;
}

function selectedHTML() {
    const selection = window.getSelection();
    const copyDocument = document.implementation.createHTMLDocument("");
    const base = copyDocument.createElement("base");
    base.href = document.baseURI;
    copyDocument.head.append(base);
    // An inert document keeps restored image URLs from starting new downloads.
    const container = copyDocument.createElement("div");
    for (let i = 0; i < selection.rangeCount; i++)
        container.appendChild(selection.getRangeAt(i).cloneContents());
    container.querySelectorAll("a[href]").forEach(anchor => anchor.setAttribute("href", anchor.href));
    container.querySelectorAll("img[data-nnw-src]").forEach(image => {
        image.setAttribute("src", image.getAttribute("data-nnw-src"));
        image.removeAttribute("data-nnw-src");
    });
    if (container.childNodes.length === 1 && container.firstChild.nodeName === "P")
        return container.firstChild.innerHTML;
    return container.innerHTML;
}
