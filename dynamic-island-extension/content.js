// Isolated world content script
window.addEventListener("message", (event) => {
  if (event.source !== window) return;
  if (event.data && event.data.type === "DYNAMIC_ISLAND_INTERCEPT") {
    try {
      chrome.runtime.sendMessage({
        action: "forward_notification",
        data: event.data.payload
      });
    } catch (err) {
      console.warn("[Dynamic Island Bridge] Bridge send error:", err);
    }
  }
});

// Forward silent background replies to MAIN world
chrome.runtime.onMessage.addListener((request) => {
  if (request && request.action === "send_silent_reply") {
    window.postMessage({
      type: "DYNAMIC_ISLAND_SILENT_REPLY",
      payload: request.data
    }, "*");
  }
});

