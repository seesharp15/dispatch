const feedForm = document.getElementById("feed-form");
const feedUrlInput = document.getElementById("feed-url");
const feedNameInput = document.getElementById("feed-name");
const feedsContainer = document.getElementById("feeds");
const statusText = document.getElementById("status-text");
const formError = document.getElementById("form-error");
const feedCardTemplate = document.getElementById("feed-card-template");
const recordingTemplate = document.getElementById("recording-template");

const feedState = new Map();
const FEED_REFRESH_MS = 10000;
const RECORDING_REFRESH_MS = 3000;

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    const message = body.message || `Request failed (${response.status})`;
    throw new Error(message);
  }
  return response.json();
}

async function loadFeeds() {
  statusText.textContent = "Refreshing";
  try {
    const feeds = await fetchJson("/api/feeds");
    syncFeeds(feeds);
    statusText.textContent = `Loaded ${feeds.length} feeds`;
  } catch (error) {
    statusText.textContent = "Error loading feeds";
    console.error(error);
  }
}

function syncFeeds(feeds) {
  if (feeds.length === 0) {
    feedState.clear();
    feedsContainer.innerHTML = "<p>No feeds yet. Add one above.</p>";
    return;
  }

  const incomingIds = new Set(feeds.map((feed) => feed.id));

  feeds.forEach((feed) => {
    let state = feedState.get(feed.id);
    if (!state) {
      state = createFeedCard(feed);
      feedState.set(feed.id, state);
      feedsContainer.appendChild(state.card);
    }

    state.feed = feed;
    updateFeedCard(state);
  });

  for (const [id, state] of feedState.entries()) {
    if (!incomingIds.has(id)) {
      state.card.remove();
      feedState.delete(id);
    }
  }
}

function createFeedCard(feed) {
  const fragment = feedCardTemplate.content.cloneNode(true);
  const card = fragment.querySelector(".feed-card");
  const name = fragment.querySelector(".feed-name");
  const meta = fragment.querySelector(".feed-meta");
  const toggle = fragment.querySelector(".toggle");
  const recordingsSection = fragment.querySelector(".recordings");
  const recordingsToggle = fragment.querySelector(".recordings-toggle");
  const recordingsList = fragment.querySelector(".recording-list");
  const count = fragment.querySelector(".recording-count");
  const streamLink = fragment.querySelector("[data-stream]");

  const state = {
    feed,
    card,
    name,
    meta,
    toggle,
    recordingsSection,
    recordingsToggle,
    recordingsList,
    count,
    streamLink,
    isRecordingsOpen: false,
    recordingNodes: new Map()
  };

  toggle.addEventListener("click", async () => {
    toggle.disabled = true;
    try {
      const endpoint = state.feed.isRunning ? "stop" : "start";
      await fetchJson(`/api/feeds/${state.feed.id}/${endpoint}`, { method: "POST" });
      await loadFeeds();
    } catch (error) {
      console.error(error);
    } finally {
      toggle.disabled = false;
    }
  });

  recordingsToggle.addEventListener("click", async () => {
    if (state.isRecordingsOpen) {
      state.recordingsSection.setAttribute("hidden", "true");
      state.recordingsToggle.textContent = "Show recordings";
      state.isRecordingsOpen = false;
      return;
    }

    state.recordingsToggle.textContent = "Loading...";
    try {
      await loadRecordings(state, false);
      state.recordingsSection.removeAttribute("hidden");
      state.recordingsToggle.textContent = "Hide recordings";
      state.isRecordingsOpen = true;
    } catch (error) {
      console.error(error);
      state.recordingsToggle.textContent = "Show recordings";
    }
  });

  updateFeedCard(state);
  return state;
}

function updateFeedCard(state) {
  const feed = state.feed;
  state.name.textContent = feed.name;
  state.meta.textContent = `Feed ID ${feed.feedIdentifier}`;
  state.streamLink.href = `/api/stream?url=${encodeURIComponent(feed.broadcastifyUrl)}`;

  state.card.classList.toggle("running", feed.isRunning);
  state.toggle.textContent = feed.isRunning ? "Stop" : "Start";
  state.toggle.classList.toggle("primary", !feed.isRunning);
  state.toggle.classList.toggle("secondary", feed.isRunning);

  if (!state.isRecordingsOpen) {
    state.recordingsSection.setAttribute("hidden", "true");
  }
}

async function loadRecordings(state, appendOnly) {
  const recordings = await fetchJson(`/api/feeds/${state.feed.id}/recordings`);
  state.count.textContent = `${recordings.length} recordings`;

  if (!appendOnly) {
    state.recordingsList.innerHTML = "";
    state.recordingNodes.clear();
  }

  const newOnes = [];
  recordings.forEach((rec) => {
    const existing = state.recordingNodes.get(rec.id);
    if (existing) {
      updateRecordingNode(existing, rec);
    } else {
      newOnes.push(rec);
    }
  });

  for (let i = newOnes.length - 1; i >= 0; i -= 1) {
    const rec = newOnes[i];
    const node = createRecordingNode(rec);
    state.recordingNodes.set(rec.id, node);
    state.recordingsList.prepend(node.root);
  }
}

function createRecordingNode(rec) {
  const recordingFragment = recordingTemplate.content.cloneNode(true);
  const root = recordingFragment.querySelector(".recording");
  const time = recordingFragment.querySelector(".recording-time");
  const duration = recordingFragment.querySelector(".recording-duration");
  const badge = recordingFragment.querySelector(".badge");
  const progressText = recordingFragment.querySelector(".progress-text");
  const progressFill = recordingFragment.querySelector(".progress-fill");
  const audio = recordingFragment.querySelector(".recording-audio");
  const text = recordingFragment.querySelector(".recording-text");

  const node = { root, time, duration, badge, progressText, progressFill, audio, text };
  updateRecordingNode(node, rec);
  return node;
}

function updateRecordingNode(node, rec) {
  const start = new Date(rec.startUtc);
  node.time.textContent = start.toLocaleString();
  node.duration.textContent = `${rec.durationSeconds.toFixed(1)}s`;
  node.badge.textContent = rec.transcriptStatus;

  const percent = Math.round(rec.transcriptProgress || 0);
  if (rec.transcriptStatus === "Processing") {
    node.progressText.textContent = `${percent}%`;
    node.progressFill.style.width = `${Math.min(percent, 100)}%`;
  } else if (rec.transcriptStatus === "Pending") {
    if (rec.transcriptQueuePosition) {
      node.progressText.textContent = `Queue #${rec.transcriptQueuePosition}`;
    } else {
      node.progressText.textContent = "Queued";
    }
    node.progressFill.style.width = "0%";
  } else if (rec.transcriptStatus === "Complete" || rec.transcriptStatus === "Skipped") {
    node.progressText.textContent = "100%";
    node.progressFill.style.width = "100%";
  } else {
    node.progressText.textContent = percent ? `${percent}%` : "";
    node.progressFill.style.width = `${Math.min(percent, 100)}%`;
  }

  if (node.audio.dataset.recordingId !== rec.id) {
    node.audio.src = `/api/recordings/${rec.id}/audio`;
    node.audio.dataset.recordingId = rec.id;
  }

  if (rec.transcriptText) {
    node.text.textContent = rec.transcriptText;
  } else if (rec.transcriptStatus === "Processing") {
    node.text.textContent = "Transcribing...";
  } else if (rec.transcriptStatus === "Failed") {
    node.text.textContent = "Transcription failed";
  } else if (rec.transcriptStatus === "Skipped") {
    node.text.textContent = "Transcription skipped";
  } else {
    node.text.textContent = "Transcript pending";
  }
}

function refreshOpenRecordings() {
  for (const state of feedState.values()) {
    if (state.isRecordingsOpen) {
      loadRecordings(state, true).catch((error) => console.error(error));
    }
  }
}

feedForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  formError.textContent = "";

  const broadcastifyUrl = feedUrlInput.value.trim();
  const name = feedNameInput.value.trim();
  if (!broadcastifyUrl) {
    formError.textContent = "Please provide a feed URL.";
    return;
  }

  try {
    await fetchJson("/api/feeds", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ broadcastifyUrl, name: name || null })
    });

    feedUrlInput.value = "";
    feedNameInput.value = "";
    await loadFeeds();
  } catch (error) {
    formError.textContent = error.message;
  }
});

loadFeeds();
setInterval(loadFeeds, FEED_REFRESH_MS);
setInterval(refreshOpenRecordings, RECORDING_REFRESH_MS);
