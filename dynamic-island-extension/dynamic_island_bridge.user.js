// ==UserScript==
// @name         Dynamic Island Facebook & Zalo Message Bridge
// @namespace    http://tampermonkey.net/
// @version      1.0.0
// @description  Chuyển tiếp tin nhắn Facebook và Zalo trực tiếp đến Dynamic Island trên Windows
// @author       Dynamic Island Team
// @match        https://*.facebook.com/*
// @match        https://*.messenger.com/*
// @match        https://chat.zalo.me/*
// @grant        GM_xmlhttpRequest
// @connect      127.0.0.1
// @run-at       document-start
// ==/UserScript==

(function() {
    'use strict';

    const isZalo = window.location.hostname.includes("zalo");
    let lastSentKey = "";
    let lastSentTime = 0;

    function sendToIsland(sender, message) {
        if (!sender && !message) return;
        sender = (sender || (isZalo ? "Zalo" : "Facebook")).trim();
        message = (message || "").trim();

        const key = `${sender}:::${message}`;
        const now = Date.now();
        if (key === lastSentKey && (now - lastSentTime) < 2500) return;
        lastSentKey = key;
        lastSentTime = now;

        const payload = JSON.stringify({
            app: isZalo ? "Zalo" : "Facebook",
            sender: sender,
            message: message,
            time: "Vừa xong"
        });

        if (typeof GM_xmlhttpRequest !== "undefined") {
            GM_xmlhttpRequest({
                method: "POST",
                url: "http://127.0.0.1:5005/api/message",
                headers: { "Content-Type": "application/json; charset=utf-8" },
                data: payload,
                onerror: () => {}
            });
        } else {
            fetch("http://127.0.0.1:5005/api/message", {
                method: "POST",
                headers: { "Content-Type": "application/json; charset=utf-8" },
                body: payload,
                mode: "cors"
            }).catch(() => {});
        }

        // Image Beacon fallback (bypasses all browser CORS/preflights)
        try {
            const img = new Image();
            img.src = `http://127.0.0.1:5005/api/message?app=${encodeURIComponent(isZalo ? "Zalo" : "Facebook")}&sender=${encodeURIComponent(sender)}&message=${encodeURIComponent(message)}&t=${now}`;
        } catch (e) {}
    }

    // 1. Hook Notification API
    const OrigNotification = window.Notification;
    if (OrigNotification) {
        const ProxyNotification = function(title, options) {
            try {
                sendToIsland(title, options?.body || "");
            } catch (e) {}

            try {
                return new OrigNotification(title, options);
            } catch (e) {
                return {
                    addEventListener: () => {},
                    removeEventListener: () => {},
                    dispatchEvent: () => {},
                    close: () => {}
                };
            }
        };

        try {
            Object.defineProperty(ProxyNotification, "permission", {
                get: () => "granted",
                configurable: true
            });
            ProxyNotification.requestPermission = async () => "granted";
        } catch (e) {}

        window.Notification = ProxyNotification;
    }

    // 2. Fallback Title Observer
    let lastSeenTitle = document.title;
    const titleObserver = new MutationObserver(() => {
        const currentTitle = document.title;
        if (currentTitle !== lastSeenTitle) {
            lastSeenTitle = currentTitle;
            const match = currentTitle.match(/^\(\d+\)\s*(.+?)(?:\s+đã gửi tin nhắn|\s+sent a message|:\s*(.*))?$/i);
            if (match) {
                const sender = match[1]?.trim();
                const msg = match[2]?.trim() || "Có tin nhắn mới";
                if (sender && !sender.toLowerCase().includes("facebook")) {
                    sendToIsland(sender, msg);
                }
            }
        }
    });

    const titleEl = document.querySelector('title');
    if (titleEl) {
        titleObserver.observe(titleEl, { subtree: true, characterData: true, childList: true });
    }

    console.log("[Dynamic Island UserScript] Active.");
})();
