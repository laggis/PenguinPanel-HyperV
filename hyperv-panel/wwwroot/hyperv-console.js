(() => {
    const bindings = new WeakMap();

    const specialKeys = {
        Enter: 0x0D,
        Backspace: 0x08,
        Tab: 0x09,
        Escape: 0x1B,
        Delete: 0x2E,
        Insert: 0x2D,
        Home: 0x24,
        End: 0x23,
        PageUp: 0x21,
        PageDown: 0x22,
        ArrowLeft: 0x25,
        ArrowUp: 0x26,
        ArrowRight: 0x27,
        ArrowDown: 0x28
    };

    for (let i = 1; i <= 12; i++) {
        specialKeys[`F${i}`] = 0x6F + i;
    }

    function virtualKeyCode(event) {
        const special = specialKeys[event.key];
        if (special !== undefined) return special;

        const code = event.code || '';
        if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3);
        if (/^Digit[0-9]$/.test(code)) return code.charCodeAt(5);
        if (/^Numpad[0-9]$/.test(code)) return 0x60 + Number(code.slice(6));

        switch (code) {
            case 'Space': return 0x20;
            case 'NumpadAdd': return 0x6B;
            case 'NumpadSubtract': return 0x6D;
            case 'NumpadMultiply': return 0x6A;
            case 'NumpadDivide': return 0x6F;
            case 'NumpadDecimal': return 0x6E;
            default: return undefined;
        }
    }

    function invokeSafe(dotNetRef, method, ...args) {
        if (!dotNetRef) return;
        dotNetRef.invokeMethodAsync(method, ...args).catch(() => {});
    }

    function bind(element, dotNetRef) {
        if (!element || !dotNetRef) return;

        const existing = bindings.get(element);
        if (existing) {
            existing.dotNetRef = dotNetRef;
            return;
        }

        const state = { dotNetRef };

        state.keydown = (event) => {
            if (event.isComposing || event.metaKey) return;

            const target = event.target;
            if (target !== element) return;

            const keyCode = virtualKeyCode(event);

            // Ctrl/Alt combinations that reach the focused console are forwarded
            // to Hyper-V as real modifier key chords. Browser/OS-reserved shortcuts
            // such as Ctrl+L, Ctrl+W or Alt+F4 may still be intercepted before a
            // web page can receive them; the toolbar Send keys menu covers those.
            if ((event.ctrlKey || event.altKey) && keyCode !== undefined) {
                event.preventDefault();
                event.stopPropagation();
                invokeSafe(
                    state.dotNetRef,
                    'HyperVConsoleTypeChord',
                    keyCode,
                    !!event.ctrlKey,
                    !!event.altKey,
                    !!event.shiftKey);
                return;
            }

            if (event.key && event.key.length === 1) {
                event.preventDefault();
                invokeSafe(state.dotNetRef, 'HyperVConsoleTypeText', event.key);
                return;
            }

            if (keyCode !== undefined) {
                event.preventDefault();
                if (event.shiftKey) {
                    invokeSafe(state.dotNetRef, 'HyperVConsoleTypeChord', keyCode, false, false, true);
                } else {
                    invokeSafe(state.dotNetRef, 'HyperVConsoleTypeKey', keyCode);
                }
            }
        };

        state.click = (event) => {
            const rect = element.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            element.focus({ preventScroll: true });
            const x = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
            const y = Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height));
            invokeSafe(state.dotNetRef, 'HyperVConsoleClick', x, y, 1);
        };

        state.contextmenu = (event) => {
            event.preventDefault();
            const rect = element.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            element.focus({ preventScroll: true });
            const x = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width));
            const y = Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height));
            invokeSafe(state.dotNetRef, 'HyperVConsoleClick', x, y, 2);
        };

        element.addEventListener('keydown', state.keydown);
        element.addEventListener('click', state.click);
        element.addEventListener('contextmenu', state.contextmenu);
        bindings.set(element, state);
    }

    function focus(element) {
        if (element) element.focus({ preventScroll: true });
    }

    function unbind(element) {
        if (!element) return;
        const state = bindings.get(element);
        if (!state) return;
        element.removeEventListener('keydown', state.keydown);
        element.removeEventListener('click', state.click);
        element.removeEventListener('contextmenu', state.contextmenu);
        bindings.delete(element);
    }

    window.penguinHyperVConsole = { bind, focus, unbind };
})();
