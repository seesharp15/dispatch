const treeContainer = document.getElementById("feed-tree");
const refreshStatesButton = document.getElementById("refresh-states");
const searchInput = document.getElementById("feed-search");
const activeFeedsContainer = document.getElementById("active-feeds");
const activeStatus = document.getElementById("active-status");
const recordingsContainer = document.getElementById("recordings");
const recordingsTitle = document.getElementById("recordings-title");
const recordingsSubtitle = document.getElementById("recordings-subtitle");
const dropZone = document.getElementById("drop-zone");
const dashboardSection = document.getElementById("dashboard");
const settingsSection = document.getElementById("settings");
const navLinks = document.querySelectorAll(".nav-link");
const autoRefreshToggle = document.getElementById("auto-refresh-toggle");
const refreshIntervalInput = document.getElementById("refresh-interval");
const refreshIntervalValue = document.getElementById("refresh-interval-value");

const treeStateTemplate = document.getElementById("tree-state-template");
const treeCountyTemplate = document.getElementById("tree-county-template");
const treeFeedTemplate = document.getElementById("tree-feed-template");
const activeFeedTemplate = document.getElementById("active-feed-template");
const recordingTemplate = document.getElementById("recording-template");
const recordingDayTemplate = document.getElementById("recording-day-template");

const DEFAULT_REFRESH_SECONDS = 8;
const STORAGE_KEYS = {
  autoRefresh: "dispatch.autoRefreshEnabled",
  refreshSeconds: "dispatch.refreshIntervalSeconds"
};

const stateStore = new Map();
const activeFeedNodes = new Map();
const recordingNodes = new Map();
let selectedFeedId = null;
let refreshTimer = null;
let refreshEnabled = true;
let refreshIntervalSeconds = DEFAULT_REFRESH_SECONDS;
let contextMenu = null;
let contextRecordingId = null;

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

  const sortedCounties = Array.from(countyMap.entries()).sort((a, b) => {
    const aKey = a[0];
    const bKey = b[0];
    const aIsStatewide = aKey.toLowerCase() === "statewide";
    const bIsStatewide = bKey.toLowerCase() === "statewide";

    if (aIsStatewide && !bIsStatewide) {
      return -1;
    }
    if (!aIsStatewide && bIsStatewide) {
      return 1;
    }

    return aKey.localeCompare(bKey);
  });
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

function setupContextMenu() {
  contextMenu = document.createElement("div");
  contextMenu.className = "context-menu";
  contextMenu.innerHTML = "<button type=\"button\" class=\"context-menu-item\">Reprocess transcript</button>";
  contextMenu.hidden = true;
  document.body.appendChild(contextMenu);

  contextMenu.addEventListener("click", async (event) => {
    const item = event.target.closest(".context-menu-item");
    if (!item) {
      return;
    }

    const recordingId = contextRecordingId;
    hideContextMenu();
    if (!recordingId) {
      return;
    }

    try {
      await fetchJson(`/api/recordings/${recordingId}/reprocess`, { method: "POST" });
      await loadRecordings();
    } catch (error) {
      console.error(error);
    }
  });

  document.addEventListener("click", hideContextMenu);
  window.addEventListener("scroll", hideContextMenu, true);
  window.addEventListener("resize", hideContextMenu);
}

function showContextMenu(x, y, recordingId) {
  if (!contextMenu) {
    return;
  }

  contextRecordingId = recordingId;
  contextMenu.hidden = false;
  contextMenu.style.left = `${x}px`;
  contextMenu.style.top = `${y}px`;

  requestAnimationFrame(() => {
    const rect = contextMenu.getBoundingClientRect();
    const maxX = window.innerWidth - rect.width - 8;
    const maxY = window.innerHeight - rect.height - 8;
    contextMenu.style.left = `${Math.max(8, Math.min(x, maxX))}px`;
    contextMenu.style.top = `${Math.max(8, Math.min(y, maxY))}px`;
  });
}

function hideContextMenu() {
  if (!contextMenu) {
    return;
  }

  contextMenu.hidden = true;
  contextRecordingId = null;
}

async function loadActiveFeeds() {
  const feeds = await fetchJson("/api/feeds");
  renderActiveFeeds(feeds);
  updateActiveStatus(feeds);
}

function showPage(page) {
  const isDashboard = page === "dashboard";
  dashboardSection.hidden = !isDashboard;
  settingsSection.hidden = isDashboard;
  dashboardSection.style.display = isDashboard ? "grid" : "none";
  settingsSection.style.display = isDashboard ? "none" : "block";

  navLinks.forEach((link) => {
    link.classList.toggle("active", link.dataset.page === page);
  });

  window.scrollTo({ top: 0, behavior: "auto" });
}

function loadRefreshSettings() {
  const storedEnabled = localStorage.getItem(STORAGE_KEYS.autoRefresh);
  const storedInterval = localStorage.getItem(STORAGE_KEYS.refreshSeconds);

  refreshEnabled = storedEnabled !== null ? storedEnabled === "true" : true;
  refreshIntervalSeconds = storedInterval ? Number(storedInterval) : DEFAULT_REFRESH_SECONDS;

  if (!Number.isFinite(refreshIntervalSeconds) || refreshIntervalSeconds < 2) {
    refreshIntervalSeconds = DEFAULT_REFRESH_SECONDS;
  }

  autoRefreshToggle.checked = refreshEnabled;
  refreshIntervalInput.value = String(refreshIntervalSeconds);
  refreshIntervalValue.textContent = `Every ${refreshIntervalSeconds} seconds`;
}

function applyRefreshSettings() {
  refreshEnabled = autoRefreshToggle.checked;
  refreshIntervalSeconds = Number(refreshIntervalInput.value);
  if (!Number.isFinite(refreshIntervalSeconds) || refreshIntervalSeconds < 2) {
    refreshIntervalSeconds = DEFAULT_REFRESH_SECONDS;
    refreshIntervalInput.value = String(refreshIntervalSeconds);
  }

  localStorage.setItem(STORAGE_KEYS.autoRefresh, String(refreshEnabled));
  localStorage.setItem(STORAGE_KEYS.refreshSeconds, String(refreshIntervalSeconds));
  refreshIntervalValue.textContent = `Every ${refreshIntervalSeconds} seconds`;
  restartAutoRefresh();
}

function restartAutoRefresh() {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }

  if (!refreshEnabled) {
    return;
  }

  refreshTimer = setInterval(() => {
    loadActiveFeeds().catch((err) => console.error(err));
    loadRecordings().catch((err) => console.error(err));
  }, refreshIntervalSeconds * 1000);
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

function updateActiveStatus(feeds) {
  const runningCount = feeds.filter((feed) => feed.isRunning).length;
  activeStatus.textContent = feeds.length
    ? `${runningCount} live • ${feeds.length} total`
    : "No feeds added";
}

function updateActiveStatusFromNodes() {
  const feeds = Array.from(activeFeedNodes.values()).map((entry) => entry.feed);
  updateActiveStatus(feeds);
}

function createActiveFeedCard(feed) {
  const fragment = activeFeedTemplate.content.cloneNode(true);
  const card = fragment.querySelector(".active-feed-card");
  const title = fragment.querySelector(".active-title");
  const meta = fragment.querySelector(".active-meta");
  const badge = fragment.querySelector(".badge");
  const toggle = fragment.querySelector(".toggle");
  const select = fragment.querySelector(".select");
  const remove = fragment.querySelector(".remove");

  const entry = { feed, card, title, meta, badge, toggle, select, remove };

  toggle.addEventListener("click", async (event) => {
    event.stopPropagation();
    toggle.disabled = true;
    const nextState = !feed.isRunning;
    const prevState = feed.isRunning;
    const prevActive = feed.isActive;
    feed.isRunning = nextState;
    feed.isActive = nextState || prevActive;
    updateActiveFeedCard(entry);
    updateActiveStatusFromNodes();
    try {
      const endpoint = nextState ? "start" : "stop";
      await fetchJson(`/api/feeds/${feed.id}/${endpoint}`, { method: "POST" });
      await loadActiveFeeds();
    } catch (error) {
      console.error(error);
      feed.isRunning = prevState;
      feed.isActive = prevActive;
      updateActiveFeedCard(entry);
      updateActiveStatusFromNodes();
    } finally {
      toggle.disabled = false;
    }
  });

  select.addEventListener("click", (event) => {
    event.stopPropagation();
    selectFeed(feed.id);
  });

  remove.addEventListener("click", async (event) => {
    event.stopPropagation();
    remove.disabled = true;
    try {
      if (feed.isRunning) {
        await fetchJson(`/api/feeds/${feed.id}/stop`, { method: "POST" });
      }
      feed.isActive = false;
      feed.isRunning = false;
      activeFeedNodes.delete(feed.id);
      card.remove();
      updateActiveStatusFromNodes();

      if (activeFeedNodes.size === 0) {
        activeFeedsContainer.innerHTML = "<p class=\"muted\">No active feeds yet.</p>";
      }

      if (selectedFeedId === feed.id) {
        selectedFeedId = null;
        updateRecordingHeader(null);
        recordingsContainer.innerHTML = "";
      }
    } catch (error) {
      console.error(error);
    } finally {
      await loadActiveFeeds();
      remove.disabled = false;
    }
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
      audio: fragment.querySelector(".recording-audio"),
      text: fragment.querySelector(".recording-text"),
      transcriptToggle: fragment.querySelector(".transcript-toggle")
    };
    node.transcriptToggle.addEventListener("click", (event) => {
      event.stopPropagation();
      toggleTranscript(node);
    });
    node.root.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      if (node.recordingId) {
        showContextMenu(event.clientX, event.clientY, node.recordingId);
      }
    });
    node.root.addEventListener("dblclick", (event) => {
      if (event.target.closest(".transcript-toggle")) {
        return;
      }
      toggleTranscript(node);
    });
    recordingNodes.set(recording.id, node);
  }

  updateRecordingNode(node, recording);
  return node;
}

function updateRecordingNode(node, rec) {
  node.recordingId = rec.id;
  const start = new Date(rec.startUtc);
  node.time.textContent = start.toLocaleString();
  node.duration.textContent = `${rec.durationSeconds.toFixed(1)}s`;
  node.badge.textContent = rec.transcriptStatus;

  const percent = Math.round(rec.transcriptProgress || 0);
  if (rec.transcriptStatus === "Processing") {
    node.progressText.textContent = `${percent}%`;
  } else if (rec.transcriptStatus === "Pending") {
    if (rec.transcriptQueuePosition) {
      node.progressText.textContent = `Queue #${rec.transcriptQueuePosition}`;
    } else {
      node.progressText.textContent = "Queued";
    }
  } else if (rec.transcriptStatus === "Complete" || rec.transcriptStatus === "Skipped") {
    node.progressText.textContent = "";
  } else {
    node.progressText.textContent = percent ? `${percent}%` : "";
  }

  if (node.audio.dataset.recordingId !== rec.id) {
    node.audio.src = `/api/recordings/${rec.id}/audio`;
    node.audio.dataset.recordingId = rec.id;
  }

  if (rec.transcriptText) {
    node.text.textContent = rec.transcriptText;
    if (!node.text.hasAttribute("hidden")) {
      node.transcriptToggle.textContent = "Hide";
    }
  } else if (rec.transcriptStatus === "Processing") {
    node.text.textContent = "Transcribing...";
  } else if (rec.transcriptStatus === "Failed") {
    node.text.textContent = "Transcription failed";
  } else if (rec.transcriptStatus === "Skipped") {
    node.text.textContent = "Transcription skipped";
  } else {
    node.text.textContent = "Transcript pending";
  }

  if (node.text.hasAttribute("hidden")) {
    node.transcriptToggle.textContent = "Transcript";
  }
}

function toggleTranscript(node) {
  const isHidden = node.text.hasAttribute("hidden");
  if (isHidden) {
    node.text.removeAttribute("hidden");
    node.transcriptToggle.textContent = "Hide";
  } else {
    node.text.setAttribute("hidden", "true");
    node.transcriptToggle.textContent = "Transcript";
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
navLinks.forEach((link) => {
  link.addEventListener("click", () => showPage(link.dataset.page));
});
autoRefreshToggle.addEventListener("change", applyRefreshSettings);
refreshIntervalInput.addEventListener("change", applyRefreshSettings);
refreshIntervalInput.addEventListener("blur", applyRefreshSettings);

setupContextMenu();
setupDragAndDrop();
loadStates().catch((err) => console.error(err));
loadActiveFeeds().catch((err) => console.error(err));
loadRefreshSettings();
restartAutoRefresh();
showPage("dashboard");
