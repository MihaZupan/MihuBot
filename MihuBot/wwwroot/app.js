(() => {
    const tooltipSelector = '[data-bs-toggle="tooltip"]';

    // Delegation includes controls added by Blazor after the initial render.
    new bootstrap.Tooltip(document.body, {
        selector: tooltipSelector,
        trigger: "hover"
    });

    document.addEventListener("click", event => {
        const element = event.target.closest(tooltipSelector);
        if (element) {
            bootstrap.Tooltip.getInstance(element)?.hide();
        }
    });

    // Blazor can remove a hovered control without firing mouseleave.
    new MutationObserver(records => {
        for (const record of records) {
            for (const node of record.removedNodes) {
                if (!(node instanceof Element) || node.isConnected) {
                    continue;
                }
                if (node.matches(tooltipSelector)) {
                    bootstrap.Tooltip.getInstance(node)?.dispose();
                }
                for (const element of node.querySelectorAll(tooltipSelector)) {
                    bootstrap.Tooltip.getInstance(element)?.dispose();
                }
            }
        }
    }).observe(document.body, { childList: true, subtree: true });
})();
