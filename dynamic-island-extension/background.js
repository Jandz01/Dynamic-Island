// Dynamic Island Background Service Worker
chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
  if (request && request.action === "forward_notification") {
    const payload = request.data;
    fetch("http://127.0.0.1:5005/api/message", {
      method: "POST",
      headers: {
        "Content-Type": "application/json; charset=utf-8"
      },
      body: JSON.stringify(payload)
    })
    .then(res => res.json())
    .then(data => {
      sendResponse({ success: true, data: data });
    })
    .catch(err => {
      sendResponse({ success: false, error: err.toString() });
    });

    return true; // Keep message channel open for async response
  }
});

// Silent background Outbox poller (Zero tab opening, zero focus stealing, zero zoom impact)
async function pollOutboxQueue() {
  try {
    const res = await fetch("http://127.0.0.1:5005/api/outbox");
    if (res.ok) {
      const messages = await res.json();
      if (Array.isArray(messages) && messages.length > 0) {
        for (const msg of messages) {
          const isZalo = msg.App && msg.App.toLowerCase().includes("zalo");
          const isFb = msg.App && (msg.App.toLowerCase().includes("facebook") || msg.App.toLowerCase().includes("messenger"));

          chrome.tabs.query({}, (tabs) => {
            for (const tab of tabs) {
              if (tab.id && tab.url) {
                const tabIsZl = tab.url.includes("zalo.me");
                const tabIsFb = tab.url.includes("facebook.com") || tab.url.includes("messenger.com");
                if ((isZalo && tabIsZl) || (isFb && tabIsFb)) {
                  chrome.tabs.sendMessage(tab.id, {
                    action: "send_silent_reply",
                    data: msg
                  }).catch(() => {});
                }
              }
            }
          });
        }
      }
    }
  } catch (e) {}
}

setInterval(pollOutboxQueue, 1500);

