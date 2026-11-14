const CACHE_NAME = "youlink-store-pwa-v1";
const OFFLINE_URL = "/offline.html";
const PRECACHE_ASSETS = [
    OFFLINE_URL,
    "/app.css",
    "/favicon.svg",
    "/icons/icon-192.png",
    "/icons/icon-512.png"
];

self.addEventListener("install", (event) => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then((cache) => cache.addAll(PRECACHE_ASSETS))
            .then(() => self.skipWaiting())
    );
});

self.addEventListener("activate", (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(
                keys.filter((key) => key !== CACHE_NAME).map((key) => caches.delete(key))
            ))
            .then(() => self.clients.claim())
    );
});

self.addEventListener("fetch", (event) => {
    if (event.request.method !== "GET") {
        return;
    }

    const url = new URL(event.request.url);
    if (url.origin !== self.location.origin) {
        return;
    }

    // Nunca interceptar SignalR do Blazor Server, framework ou endpoints de API/mídia dinâmica.
    if (url.pathname.startsWith("/_blazor") ||
        url.pathname.startsWith("/_framework/") ||
        url.pathname.startsWith("/api/") ||
        url.pathname.startsWith("/uploads/")) {
        return;
    }

    if (event.request.mode === "navigate") {
        event.respondWith(
            fetch(event.request).catch(async () => {
                const cache = await caches.open(CACHE_NAME);
                return (await cache.match(OFFLINE_URL)) || Response.error();
            })
        );
        return;
    }

    if (url.pathname === "/app.css" ||
        url.pathname === "/favicon.svg" ||
        url.pathname.startsWith("/icons/")) {
        event.respondWith(
            caches.match(event.request).then((cached) => {
                const networkFetch = fetch(event.request).then((response) => {
                    if (response && response.ok) {
                        const clone = response.clone();
                        caches.open(CACHE_NAME).then((cache) => cache.put(event.request, clone));
                    }
                    return response;
                }).catch(() => cached || Response.error());
                return cached || networkFetch;
            })
        );
    }
});
