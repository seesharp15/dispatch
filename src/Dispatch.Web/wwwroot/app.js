const treeContainer = document.getElementById("feed-tree");
const refreshStatesButton = document.getElementById("refresh-states");
const searchInput = document.getElementById("feed-search");
const activeFeedsContainer = document.getElementById("active-feeds");
const activeStatus = document.getElementById("active-status");
const recordingsContainer = document.getElementById("recordings");
const recordingsTitle = document.getElementById("recordings-title");
const recordingsSubtitle = document.getElementById("recordings-subtitle");
const dropZone = document.getElementById("drop-zone");

const treeStateTemplate = document.getElementById("tree-state-template");
const treeCountyTemplate = document.getElementById("tree-county-template");
const treeFeedTemplate = document.getElementById("tree-feed-template");
const activeFeedTemplate = document.getElementById("active-feed-template");
const recordingTemplate = document.getElementById("recording-template");
const recordingDayTemplate = document.getElementById("recording-day-template");

const ACTIVE_REFRESH_MS = 8000;
const RECORDING_REFRESH_MS = 4000;

const stateStore = new Map();
const activeFeedNodes = new Map();
const recordingNodes = new Map();
let selectedFeedId = null;

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    const message = body.message || `Request failed (${response.status})`;
    throw new Error(message);
  }
  return response.json();
}

async function loadStates() {
  treeContainer.innerHTML = "";
  stateStore.clear();

  const states = await fetchJson("/api/discovery/states");
  states.forEach((stateName) => {
    const fragment = treeStateTemplate.content.cloneNode(true);
    const node = fragment.querySelector(".tree-node");
    const label = fragment.querySelector(".tree-label");
    const count = fragment.querySelector(".tree-count");
    const toggle = fragment.querySelector(".tree-toggle");

    label.textContent = stateName;
    count.textContent = "–";

    const children = document.createElement("div");
    children.className = "tree-children";
    children.hidden = true;

    const stateEntry = {
      stateName,
      node,
      children,
      toggle,
      count,
      loaded: false,
      expanded: false,
      counties: new Map()
    };

    toggle.addEventListener("click", async (event) => {
      event.stopPropagation();
      await toggleState(stateEntry);
    });

    node.addEventListener("click", async () => {
      await toggleState(stateEntry);
    });

    stateStore.set(stateName, stateEntry);
    treeContainer.appendChild(node);
    treeContainer.appendChild(children);
  });
}

async function toggleState(stateEntry) {
  if (!stateEntry.loaded) {
    await loadStateFeeds(stateEntry);
  }

  stateEntry.expanded = !stateEntry.expanded;
  stateEntry.children.hidden = !stateEntry.expanded;
  stateEntry.toggle.textContent = stateEntry.expanded ? "▾" : "▸";
}

async function loadStateFeeds(stateEntry) {
  stateEntry.count.textContent = "…";
  const feeds = await fetchJson(`/api/discovery/feeds?state=${encodeURIComponent(stateEntry.stateName)}`);

  const countyMap = new Map();
  feeds.forEach((feed) => {
    const countyKey = feed.county || "Statewide";
    if (!countyMap.has(countyKey)) {
      countyMap.set(countyKey, []);
    }
    countyMap.get(countyKey).push(feed);
  });

  stateEntry.children.innerHTML = "";
  stateEntry.counties.clear();

  const sortedCounties = Array.from(countyMap.entries()).sort((a, b) => a[0].localeCompare(b[0]));
  sortedCounties.forEach(([countyName, countyFeeds]) => {
    const countyFragment = treeCountyTemplate.content.cloneNode(true);
    const countyNode = countyFragment.querySelector(".tree-node");
    const countyLabel = countyFragment.querySelector(".tree-label");
    const countyCount = countyFragment.querySelector(".tree-count");
    const countyToggle = countyFragment.querySelector(".tree-toggle");

    countyLabel.textContent = countyName;
    countyCount.textContent = `${countyFeeds.length}`;

    const countyChildren = document.createElement("div");
    countyChildren.className = "tree-children";
    countyChildren.hidden = true;

    const countyEntry = {
      countyName,
      node: countyNode,
      children: countyChildren,
      toggle: countyToggle,
      expanded: false,
      feeds: []
    };

    countyToggle.addEventListener("click", (event) => {
      event.stopPropagation();
      toggleCounty(countyEntry);
    });

    countyNode.addEventListener("click", () => toggleCounty(countyEntry));

    const sortedFeeds = countyFeeds.sort((a, b) => a.feedName.localeCompare(b.feedName));
    sortedFeeds.forEach((feed) => {
      const feedFragment = treeFeedTemplate.content.cloneNode(true);
      const feedNode = feedFragment.querySelector(".tree-node");
      const feedLabel = feedFragment.querySelector(".tree-label");
      const feedStatus = feedFragment.querySelector(".tree-status");

      feedLabel.textContent = feed.feedName;
      feedStatus.textContent = feed.feedStatus;
      feedNode.dataset.feedId = feed.feedId;
      feedNode.dataset.feedName = feed.feedName;
      feedNode.dataset.state = feed.state;
      feedNode.dataset.county = feed.county;
      feedNode.dataset.search = `${feed.state} ${feed.county} ${feed.feedName}`.toLowerCase();

      feedNode.addEventListener("dragstart", (event) => {
        feedNode.classList.add("dragging");
        event.dataTransfer.setData(
          "application/json",
          JSON.stringify({
            feedId: feed.feedId,
            feedName: feed.feedName,
            state: feed.state,
            county: feed.county
          })
        );
      });

      feedNode.addEventListener("dragend", () => {
        feedNode.classList.remove("dragging");
      });

      countyChildren.appendChild(feedNode);
      countyEntry.feeds.push({ node: feedNode, data: feed });
    });

    stateEntry.children.appendChild(countyNode);
    stateEntry.children.appendChild(countyChildren);
    stateEntry.counties.set(countyName, countyEntry);
  });

  stateEntry.loaded = true;
  stateEntry.count.textContent = `${feeds.length}`;
  stateEntry.expanded = true;
  stateEntry.children.hidden = false;
  stateEntry.toggle.textContent = "▾";
  applySearchFilter();
}

function toggleCounty(countyEntry) {
  countyEntry.expanded = !countyEntry.expanded;
  countyEntry.children.hidden = !countyEntry.expanded;
  countyEntry.toggle.textContent = countyEntry.expanded ? "▾" : "▸";
}

function applySearchFilter() {
  const query = searchInput.value.trim().toLowerCase();
  if (!query) {
    stateStore.forEach((stateEntry) => {
      stateEntry.node.classList.remove("hidden");
      stateEntry.counties.forEach((countyEntry) => {
        countyEntry.node.classList.remove("hidden");
        countyEntry.feeds.forEach((feedEntry) => feedEntry.node.classList.remove("hidden"));
      });
    });
    return;
  }

  stateStore.forEach((stateEntry) => {
    let stateHasMatch = false;

    stateEntry.counties.forEach((countyEntry) => {
      let countyHasMatch = false;
      countyEntry.feeds.forEach((feedEntry) => {
        const isMatch = feedEntry.node.dataset.search.includes(query);
        feedEntry.node.classList.toggle("hidden", !isMatch);
        if (isMatch) {
          countyHasMatch = true;
          stateHasMatch = true;
        }
      });

      countyEntry.node.classList.toggle("hidden", !countyHasMatch);
      if (countyHasMatch) {
        countyEntry.children.hidden = false;
        countyEntry.toggle.textContent = "▾";
      }
    });

    stateEntry.node.classList.toggle("hidden", !stateHasMatch);
    if (stateHasMatch) {
      stateEntry.children.hidden = false;
      stateEntry.toggle.textContent = "▾";
    }
  });
}

async function loadActiveFeeds() {
  const feeds = await fetchJson("/api/feeds");
  const runningCount = feeds.filter((feed) => feed.isRunning).length;
  activeStatus.textContent = feeds.length
    ? `${runningCount} live • ${feeds.length} total`
    : "No feeds added";

  renderActiveFeeds(feeds);
}

function renderActiveFeeds(feeds) {
  if (feeds.length === 0) {
    activeFeedNodes.forEach((entry) => entry.card.remove());
    activeFeedNodes.clear();
    activeFeedsContainer.innerHTML = "<p class=\"muted\">No active feeds yet.</p>";
    return;
  }

  if (activeFeedsContainer.querySelector("p.muted")) {
    activeFeedsContainer.innerHTML = "";
  }

  const incomingIds = new Set(feeds.map((feed) => feed.id));

  feeds.forEach((feed) => {
    let entry = activeFeedNodes.get(feed.id);
    if (!entry) {
      entry = createActiveFeedCard(feed);
      activeFeedNodes.set(feed.id, entry);
      activeFeedsContainer.appendChild(entry.card);
    } else {
      entry.feed = feed;
      updateActiveFeedCard(entry);
    }
  });

  for (const [id, entry] of activeFeedNodes.entries()) {
    if (!incomingIds.has(id)) {
      entry.card.remove();
      activeFeedNodes.delete(id);
    }
  }

  if (selectedFeedId && !incomingIds.has(selectedFeedId)) {
    selectedFeedId = null;
    updateRecordingHeader(null);
    recordingsContainer.innerHTML = "";
  }

}

function createActiveFeedCard(feed) {
  const fragment = activeFeedTemplate.content.cloneNode(true);
  const card = fragment.querySelector(".active-feed-card");
  const title = fragment.querySelector(".active-title");
  const meta = fragment.querySelector(".active-meta");
  const badge = fragment.querySelector(".badge");
  const toggle = fragment.querySelector(".toggle");
  const select = fragment.querySelector(".select");

  const entry = { feed, card, title, meta, badge, toggle, select };

  toggle.addEventListener("click", async (event) => {
    event.stopPropagation();
    toggle.disabled = true;
    try {
      const endpoint = feed.isRunning ? "stop" : "start";
      await fetchJson(`/api/feeds/${feed.id}/${endpoint}`, { method: "POST" });
      await loadActiveFeeds();
    } catch (error) {
      console.error(error);
    } finally {
      toggle.disabled = false;
    }
  });

  select.addEventListener("click", (event) => {
    event.stopPropagation();
    selectFeed(feed.id);
  });

  card.addEventListener("click", () => selectFeed(feed.id));

  updateActiveFeedCard(entry);
  return entry;
}

function updateActiveFeedCard(entry) {
  const feed = entry.feed;
  entry.title.textContent = feed.name;
  entry.meta.textContent = `Feed ID ${feed.feedIdentifier}`;
  entry.badge.textContent = feed.isRunning ? "Live" : "Paused";
  entry.toggle.textContent = feed.isRunning ? "Stop" : "Start";
  entry.card.classList.toggle("selected", feed.id === selectedFeedId);
}

function selectFeed(feedId) {
  selectedFeedId = feedId;
  for (const entry of activeFeedNodes.values()) {
    entry.card.classList.toggle("selected", entry.feed.id === feedId);
  }

  const selected = activeFeedNodes.get(feedId);
  updateRecordingHeader(selected?.feed ?? null);
  loadRecordings();
}

function updateRecordingHeader(feed) {
  if (!feed) {
    recordingsTitle.textContent = "Select a feed";
    recordingsSubtitle.textContent = "Choose an active feed to view recordings.";
    return;
  }

  recordingsTitle.textContent = feed.name;
  recordingsSubtitle.textContent = `${feed.feedIdentifier} • ${feed.broadcastifyUrl}`;
}

async function loadRecordings() {
  if (!selectedFeedId) {
    return;
  }

  const recordings = await fetchJson(`/api/feeds/${selectedFeedId}/recordings`);
  renderRecordings(recordings);
}

function renderRecordings(recordings) {
  recordingsContainer.innerHTML = "";
  if (!recordings.length) {
    recordingsContainer.innerHTML = "<p class=\"muted\">No recordings yet.</p>";
    return;
  }

  const todayKey = toDateKey(new Date());
  const groups = new Map();

  recordings.forEach((recording) => {
    const key = toDateKey(new Date(recording.startUtc));
    if (!groups.has(key)) {
      groups.set(key, []);
    }
    groups.get(key).push(recording);
  });

  const orderedKeys = Array.from(groups.keys()).sort((a, b) => (a < b ? 1 : -1));

  orderedKeys.forEach((key) => {
    const recordingsForDay = groups.get(key) || [];
    const dayFragment = recordingDayTemplate.content.cloneNode(true);
    const dayRoot = dayFragment.querySelector(".recording-day");
    const dayHeader = dayFragment.querySelector(".day-header");
    const dayLabel = dayFragment.querySelector(".day-label");
    const dayCount = dayFragment.querySelector(".day-count");
    const dayList = dayFragment.querySelector(".day-list");

    const label = key === todayKey ? "Today" : formatDateLabel(key);
    dayLabel.textContent = label;
    dayCount.textContent = `${recordingsForDay.length} calls`;

    let expanded = key === todayKey;
    dayList.hidden = !expanded;

    dayHeader.addEventListener("click", () => {
      expanded = !expanded;
      dayList.hidden = !expanded;
    });

    recordingsForDay.forEach((recording) => {
      const node = getRecordingNode(recording);
      dayList.appendChild(node.root);
    });

    recordingsContainer.appendChild(dayRoot);
  });
}

function getRecordingNode(recording) {
  let node = recordingNodes.get(recording.id);
  if (!node) {
    const fragment = recordingTemplate.content.cloneNode(true);
    node = {
      root: fragment.querySelector(".recording"),
      time: fragment.querySelector(".recording-time"),
      duration: fragment.querySelector(".recording-duration"),
      badge: fragment.querySelector(".badge"),
      progressText: fragment.querySelector(".progress-text"),
      progressFill: fragment.querySelector(".progress-fill"),
      audio: fragment.querySelector(".recording-audio"),
      text: fragment.querySelector(".recording-text")
    };
    recordingNodes.set(recording.id, node);
  }

  updateRecordingNode(node, recording);
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

function toDateKey(date) {
  return date.toISOString().slice(0, 10);
}

function formatDateLabel(key) {
  const date = new Date(`${key}T00:00:00`);
  return date.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });
}

async function addFeedFromDiscovery(payload) {
  const feeds = await fetchJson("/api/feeds");
  const existing = feeds.find((feed) => feed.feedIdentifier === payload.feedId);

  let targetFeed = existing;
  if (!existing) {
    targetFeed = await fetchJson("/api/feeds", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        broadcastifyUrl: payload.feedId,
        name: `${payload.feedName} (${payload.county}, ${payload.state})`
      })
    });
  }

  if (targetFeed) {
    await fetchJson(`/api/feeds/${targetFeed.id}/start`, { method: "POST" });
  }

  await loadActiveFeeds();
}

function setupDragAndDrop() {
  dropZone.addEventListener("dragover", (event) => {
    event.preventDefault();
    dropZone.classList.add("drag-over");
  });

  dropZone.addEventListener("dragleave", () => {
    dropZone.classList.remove("drag-over");
  });

  dropZone.addEventListener("drop", async (event) => {
    event.preventDefault();
    dropZone.classList.remove("drag-over");
    const raw = event.dataTransfer.getData("application/json");
    if (!raw) {
      return;
    }

    const payload = JSON.parse(raw);
    try {
      await addFeedFromDiscovery(payload);
    } catch (error) {
      console.error(error);
    }
  });
}

refreshStatesButton.addEventListener("click", () => loadStates().catch((err) => console.error(err)));
searchInput.addEventListener("input", applySearchFilter);

setupDragAndDrop();
loadStates().catch((err) => console.error(err));
loadActiveFeeds().catch((err) => console.error(err));
setInterval(loadActiveFeeds, ACTIVE_REFRESH_MS);
setInterval(loadRecordings, RECORDING_REFRESH_MS);
