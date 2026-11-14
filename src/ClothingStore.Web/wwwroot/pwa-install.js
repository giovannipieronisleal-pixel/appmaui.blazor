(() => {
    let deferredInstallPrompt = null;

    const isRunningStandalone = () =>
        window.matchMedia("(display-mode: standalone)").matches ||
        window.navigator.standalone === true;

    if ("serviceWorker" in navigator) {
        window.addEventListener("load", () => {
            navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(() => {
                // Ambiente HTTP local sem certificado confiável ignora o SW silenciosamente.
            });
        });
    }

    window.addEventListener("beforeinstallprompt", (event) => {
        if (isRunningStandalone()) {
            return;
        }
        event.preventDefault();
        deferredInstallPrompt = event;
        document.documentElement.classList.add("pwa-can-install");
    });

    window.addEventListener("appinstalled", () => {
        deferredInstallPrompt = null;
        document.documentElement.classList.remove("pwa-can-install");
    });

    document.addEventListener("click", async (event) => {
        const trigger = event.target && event.target.closest
            ? event.target.closest("[data-pwa-install]")
            : null;
        if (!trigger || !deferredInstallPrompt) {
            return;
        }

        event.preventDefault();
        const promptEvent = deferredInstallPrompt;
        deferredInstallPrompt = null;
        document.documentElement.classList.remove("pwa-can-install");

        try {
            await promptEvent.prompt();
            await promptEvent.userChoice;
        } catch {
            // Ignora cancelamento pelo usuário sem quebrar a página.
        }
    });
})();
