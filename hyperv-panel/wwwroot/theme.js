(function () {
    const storageKey = "penguinPanel.theme";
    const supported = new Set(["modern", "legacy"]);

    function normalize(value) {
        return supported.has(value) ? value : "modern";
    }

    function apply(value) {
        const theme = normalize(value);
        document.documentElement.classList.remove("theme-modern", "theme-legacy");
        document.documentElement.classList.add("theme-" + theme);
        document.documentElement.dataset.panelTheme = theme;
        if (document.body) {
            document.body.classList.remove("theme-modern", "theme-legacy");
            document.body.classList.add("theme-" + theme);
        }
        return theme;
    }

    let initial = "modern";
    try { initial = normalize(localStorage.getItem(storageKey)); } catch { }
    apply(initial);

    window.penguinPanelTheme = {
        get: function () {
            try { return normalize(localStorage.getItem(storageKey)); }
            catch { return "modern"; }
        },
        set: function (value) {
            const theme = apply(value);
            try { localStorage.setItem(storageKey, theme); } catch { }
            return theme;
        },
        apply: apply
    };
})();
