function postRenderProcessing() {
    if (getComputedStyle(document.body).fontFamily.includes("-apple-system"))
        document.body.style.fontFamily = '"Segoe UI Variable", "Segoe UI", sans-serif';
    window.__wnwArticleProcessed = true;
}

function selectedHTML() {
    const selection = window.getSelection();
    const container = document.createElement("div");
    for (let i = 0; i < selection.rangeCount; i++)
        container.appendChild(selection.getRangeAt(i).cloneContents());
    container.querySelectorAll("a[href]").forEach(anchor => anchor.setAttribute("href", anchor.href));
    if (container.childNodes.length === 1 && container.firstChild.nodeName === "P")
        return container.firstChild.innerHTML;
    return container.innerHTML;
}
