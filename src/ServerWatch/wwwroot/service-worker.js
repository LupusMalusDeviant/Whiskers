// ServerWatch Service Worker — Offline-Caching + PWA Install
const CACHE_NAME = 'serverwatch-v1';

self.addEventListener('install', event => {
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(clients.claim());
});

// Network-first strategy — always try network, fall back to cache
self.addEventListener('fetch', event => {
    // Only cache GET requests for static assets
    if (event.request.method !== 'GET') return;

    const url = new URL(event.request.url);
    if (url.pathname.includes('_blazor') || url.pathname.includes('_framework')) return;

    event.respondWith(
        fetch(event.request)
            .then(response => {
                // Cache static assets
                if (response.ok && (url.pathname.endsWith('.css') || url.pathname.endsWith('.js') || url.pathname.endsWith('.png'))) {
                    const clone = response.clone();
                    caches.open(CACHE_NAME).then(cache => cache.put(event.request, clone));
                }
                return response;
            })
            .catch(() => caches.match(event.request))
    );
});
