// Injected into the MAIN world of Facebook and Zalo
(function() {
  const isZalo = window.location.hostname.includes("zalo");
  const isFacebook = window.location.hostname.includes("facebook") || window.location.hostname.includes("messenger");

  let lastSentKey = "";
  let lastSentTime = 0;

  function dispatchToIsland(sender, message) {
    if (!sender && !message) return;
    sender = (sender || (isZalo ? "Zalo" : "Facebook")).trim();
    message = (message || "").trim();

    const key = `${sender}:::${message}`;
    const now = Date.now();
    if (key === lastSentKey && (now - lastSentTime) < 3000) {
      return; // Deduplicate rapid identical triggers within 3 seconds
    }
    lastSentKey = key;
    lastSentTime = now;

    const payload = {
      app: isZalo ? "Zalo" : "Facebook",
      sender: sender,
      message: message,
      time: "Vừa xong"
    };

    // 1. Post message to Extension Isolated World Content Script
    try {
      window.postMessage({
        type: "DYNAMIC_ISLAND_INTERCEPT",
        payload: payload
      }, "*");
    } catch (e) {}

    // 2. Direct fetch with PNA / CORS headers enabled on Dynamic Island
    try {
      fetch("http://127.0.0.1:5005/api/message", {
        method: "POST",
        headers: { "Content-Type": "application/json; charset=utf-8" },
        body: JSON.stringify(payload),
        mode: "cors"
      }).catch(() => {});
    } catch (e) {}

    // 3. Fallback Image beacon (bypasses CORS/preflight completely in all browsers)
    try {
      const img = new Image();
      img.src = `http://127.0.0.1:5005/api/message?app=${encodeURIComponent(payload.app)}&sender=${encodeURIComponent(payload.sender)}&message=${encodeURIComponent(payload.message)}&t=${now}`;
    } catch (e) {}
  }

  function parseTitle(title) {
    if (!title) return null;
    const prefixMatch = title.match(/^\((\d+\+?)\)\s*(.+)$/);
    if (!prefixMatch) return null;

    const count = prefixMatch[1];
    let rest = prefixMatch[2].trim();

    if (rest.toLowerCase() === "facebook" || rest.toLowerCase() === "zalo" || rest.toLowerCase() === "messenger") {
      return {
        sender: isZalo ? "Zalo" : "Facebook",
        message: `Bạn có ${count} tin nhắn mới`
      };
    }

    const colonMatch = rest.match(/^([^:]+):\s*(.+)$/);
    if (colonMatch) {
      return { sender: colonMatch[1].trim(), message: colonMatch[2].trim() };
    }

    const sentMsgMatch = rest.match(/^(.+?)(?:\s+đã gửi.*|\s+sent you.*|\s+sent a message.*)$/i);
    if (sentMsgMatch) {
      return { sender: sentMsgMatch[1].trim(), message: "Có tin nhắn mới" };
    }

    const fromMatch = rest.match(/(?:tin nhắn mới từ|message from)\s+(.+)$/i);
    if (fromMatch) {
      return { sender: fromMatch[1].trim(), message: "Có tin nhắn mới" };
    }

    return { sender: isZalo ? "Zalo" : "Facebook", message: rest };
  }

  // 1. Hook window.Notification API
  const OrigNotification = window.Notification;
  if (OrigNotification) {
    const ProxyNotification = function(title, options) {
      try {
        const body = options && options.body ? options.body : "";
        dispatchToIsland(title, body);
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

  // 2. Continuous Title Watcher (Catches background tab updates)
  let lastSeenTitle = document.title;
  function checkTitle() {
    const currentTitle = document.title;
    if (currentTitle && currentTitle !== lastSeenTitle) {
      lastSeenTitle = currentTitle;
      const parsed = parseTitle(currentTitle);
      if (parsed) {
        dispatchToIsland(parsed.sender, parsed.message);
      }
    }
  }

  setInterval(checkTitle, 700);

  const titleObserver = new MutationObserver(checkTitle);
  const titleEl = document.querySelector('title');
  if (titleEl) {
    titleObserver.observe(titleEl, { subtree: true, characterData: true, childList: true });
  } else {
    document.addEventListener("DOMContentLoaded", () => {
      const t = document.querySelector('title');
      if (t) titleObserver.observe(t, { subtree: true, characterData: true, childList: true });
    });
  }

  // 3. Facebook Messenger Chat Bubble & Dialog Observer
  if (isFacebook) {
    document.addEventListener("DOMContentLoaded", () => {
      const chatObserver = new MutationObserver((mutations) => {
        for (const mutation of mutations) {
          if (mutation.addedNodes.length > 0) {
            mutation.addedNodes.forEach((node) => {
              if (node.nodeType === Node.ELEMENT_NODE) {
                const incomingMsg = node.querySelector ? (node.querySelector('[data-testid="message-container"]') || node.querySelector('[dir="auto"]')) : null;
                if (incomingMsg) {
                  const text = incomingMsg.textContent?.trim();
                  if (text && text.length > 0 && text.length < 500) {
                    const chatHeader = node.closest('[role="region"]') || node.closest('[role="dialog"]') || document.querySelector('[role="main"]');
                    const headerText = chatHeader?.querySelector('h1, h2, [role="heading"]')?.textContent?.trim();
                    if (headerText && !headerText.toLowerCase().includes("messenger") && !headerText.toLowerCase().includes("facebook")) {
                      dispatchToIsland(headerText, text);
                    }
                  }
                }
              }
            });
          }
        }
      });

      chatObserver.observe(document.body, { childList: true, subtree: true });
    });
  }

  // 4. Zalo Web Toast & Chat Observer
  if (isZalo) {
    document.addEventListener("DOMContentLoaded", () => {
      const zaloObserver = new MutationObserver(() => {
        const toast = document.querySelector('.toast-container .toast-message, .v-toast__text, .zl-modal__dialog');
        if (toast && toast.textContent) {
          const text = toast.textContent.trim();
          dispatchToIsland("Zalo", text);
        }
      });
      zaloObserver.observe(document.body, { childList: true, subtree: true });
    });
  }

  // 5. Silent Reply Handler (Zero browser interruption, zero zoom, zero tab opening)
  window.addEventListener("message", (event) => {
    if (event.source !== window || !event.data || event.data.type !== "DYNAMIC_ISLAND_SILENT_REPLY") return;
    const payload = event.data.payload;
    if (!payload || !payload.Message) return;

    try {
      const inputEl = document.querySelector('[role="textbox"][contenteditable="true"]') ||
                      document.querySelector('div[contenteditable="true"]') ||
                      document.querySelector('#input_line, .chat-input, textarea');
      if (inputEl) {
        if (inputEl.isContentEditable) {
          inputEl.focus({ preventScroll: true });
          document.execCommand('insertText', false, payload.Message);
        } else {
          inputEl.value = payload.Message;
          inputEl.dispatchEvent(new Event('input', { bubbles: true }));
        }

        setTimeout(() => {
          const enterEvent = new KeyboardEvent('keydown', {
            bubbles: true, cancelable: true, key: 'Enter', code: 'Enter', keyCode: 13
          });
          inputEl.dispatchEvent(enterEvent);
        }, 120);
      }
    } catch (e) {}
  });

  console.log("[Dynamic Island Bridge] Active, PNA supported, listening for Facebook & Zalo messages.");
})();

