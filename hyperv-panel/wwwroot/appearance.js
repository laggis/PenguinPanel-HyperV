(() => {
    const root = document.documentElement;
    const key = "penguinPanel.appearance";
    const scheme = window.matchMedia("(prefers-color-scheme: dark)");
    const modes = ["system", "light", "dark"];
    let preference = "system";

    function apply(value) {
        preference = modes.includes(value) ? value : "system";
        root.dataset.appearance = preference;
        root.dataset.scheme = preference === "system" ? (scheme.matches ? "dark" : "light") : preference;
    }

    try { apply(localStorage.getItem(key)); }
    catch { apply("system"); }

    scheme.addEventListener("change", () => apply(preference));
    window.addEventListener("storage", event => {
        if (event.storageArea === localStorage && (event.key === key || event.key === null)) {
            apply(event.newValue);
        }
    });
    document.addEventListener("click", event => {
        if (!event.target.closest?.("[data-appearance-toggle]")) return;
        apply(modes[(modes.indexOf(preference) + 1) % modes.length]);
        try {
            if (preference === "system") localStorage.removeItem(key);
            else localStorage.setItem(key, preference);
        } catch { /* The choice still applies to the current page. */ }
    });
})();
