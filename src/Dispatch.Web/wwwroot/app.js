const treeContainer = document.getElementById("feed-tree");
const refreshStatesButton = document.getElementById("refresh-states");
const addLocalFeedButton = document.getElementById("add-local-feed");
const searchInput = document.getElementById("feed-search");
const activeFeedsContainer = document.getElementById("active-feeds");
const activeStatus = document.getElementById("active-status");
const recordingsContainer = document.getElementById("recordings");
const recordingsTitle = document.getElementById("recordings-title");
const recordingsSubtitle = document.getElementById("recordings-subtitle");
const showArchivedToggle = document.getElementById("show-archived-toggle");
const dropZone = document.getElementById("drop-zone");
const dashboardSection = document.getElementById("dashboard");
const settingsSection = document.getElementById("settings");
const navLinks = document.querySelectorAll(".nav-link");
const autoRefreshToggle = document.getElementById("auto-refresh-toggle");
const refreshIntervalInput = document.getElementById("refresh-interval");
const refreshIntervalValue = document.getElementById("refresh-interval-value");
const localFeedModal = document.getElementById("local-feed-modal");
const localDeviceSelect = document.getElementById("local-device-select");
const localFeedNameInput = document.getElementById("local-feed-name");
const localFeedAutoStart = document.getElementById("local-feed-autostart");
const localFeedCancelButton = document.getElementById("local-feed-cancel");
const localFeedSubmitButton = document.getElementById("local-feed-submit");
const localFeedNote = document.getElementById("local-feed-note");
const synthesisModal = document.getElementById("synthesis-modal");
const synthesisTitle = document.getElementById("synthesis-title");
const synthesisMeta = document.getElementById("synthesis-meta");
const synthesisSummary = document.getElementById("synthesis-summary");
const synthesisThemes = document.getElementById("synthesis-themes");
const synthesisCategories = document.getElementById("synthesis-categories");
const synthesisHighlights = document.getElementById("synthesis-highlights");
const synthesisCloseButton = document.getElementById("synthesis-close");

const treeStateTemplate = document.getElementById("tree-state-template");
const treeCountyTemplate = document.getElementById("tree-county-template");
const treeFeedTemplate = document.getElementById("tree-feed-template");
const activeFeedTemplate = document.getElementById("active-feed-template");
const recordingTemplate = document.getElementById("recording-template");
const recordingDayTemplate = document.getElementById("recording-day-template");

const DEFAULT_REFRESH_MS = 8000;
const NEW_RECORDING_WINDOW_SECONDS = 10;
const PRELOAD_CONCURRENCY = 4;
const RECORDINGS_PER_DAY_INITIAL = 25;
const STORAGE_KEYS = {
  autoRefresh: "dispatch.autoRefreshEnabled",
  refreshMs: "dispatch.refreshIntervalMs",
  refreshSecondsLegacy: "dispatch.refreshIntervalSeconds",
  showArchived: "dispatch.showArchived"
};

const stateStore = new Map();
const activeFeedNodes = new Map();
const recordingNodes = new Map();
const recordingDayNodes = new Map();
const recordingDayTotals = new Map();
const feedFlashTimers = new Map();
let selectedFeedId = null;
let refreshEnabled = true;
let refreshIntervalMs = DEFAULT_REFRESH_MS;
let showArchived = false;
let contextMenu = null;
let contextRecordingId = null;
let recordingsInitialized = false;
let latestRecordingStart = null;
let oldestRecordingStart = null;
let canLoadOlderRecordings = false;
let recordingsPageLoading = false;
let preloadToken = 0;
let eventSource = null;
let feedEventSource = null;
let progressTimer = null;
let streamSyncInFlight = false;
let uiConfig = {
  expectedRealtimeFactor: 0.7,
  estimatedBytesPerSecond: 32000
};

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  const bodyText = await response.text();
  let parsedBody = null;
  if (bodyText) {
    try {
      parsedBody = JSON.parse(bodyText);
    } catch {
      parsedBody = null;
    }
  }

  if (response.status === 401) {
    window.location.href = "/login.html";
    return;
  }

  if (!response.ok) {
    const message =
      (parsedBody && typeof parsedBody === "object" && parsedBody.message) ||
      bodyText ||
      `Request failed (${response.status})`;
    throw new Error(message);
  }

  if (!bodyText) {
    return null;
  }

  return parsedBody ?? bodyText;
}

function toFeedKey(feedId) {
  if (!feedId) {
    return "";
  }

  return String(feedId).toLowerCase();
}

function setLocalFeedModalOpen(isOpen) {
  if (!localFeedModal) {
    return;
  }

  localFeedModal.hidden = !isOpen;
}

function populateLocalAudioDevices(devices) {
  if (!localDeviceSelect || !localFeedSubmitButton || !localFeedNote) {
    return;
  }

  localDeviceSelect.innerHTML = "";
  if (!devices || devices.length === 0) {
    const option = document.createElement("option");
    option.value = "";
    option.textContent = "No local audio devices detected";
    localDeviceSelect.appendChild(option);
    localDeviceSelect.disabled = true;
    localFeedSubmitButton.disabled = true;
    localFeedNote.textContent = "Connect or enable an input/loopback device, then try again.";
    return;
  }

  const sortedDevices = [...devices].sort((a, b) => {
    const aKind = String(a.captureKind || "");
    const bKind = String(b.captureKind || "");
    if (aKind !== bKind) {
      return aKind.localeCompare(bKind);
    }
    return String(a.name || "").localeCompare(String(b.name || ""));
  });

  sortedDevices.forEach((device) => {
    const option = document.createElement("option");
    option.value = device.id;
    option.textContent = `${device.name} (${device.captureKind} • ${device.backend})`;
    localDeviceSelect.appendChild(option);
  });

  localDeviceSelect.disabled = false;
  localFeedSubmitButton.disabled = false;
  localFeedNote.textContent = "";
}

async function openLocalFeedModal() {
  if (!localFeedModal || !localDeviceSelect || !localFeedSubmitButton || !localFeedNote) {
    return;
  }

  setLocalFeedModalOpen(true);
  if (localFeedNameInput) {
    localFeedNameInput.value = "";
  }
  if (localFeedAutoStart) {
    localFeedAutoStart.checked = true;
  }

  localDeviceSelect.innerHTML = "";
  localDeviceSelect.disabled = true;
  localFeedSubmitButton.disabled = true;
  localFeedNote.textContent = "Loading local audio devices...";

  try {
    const devices = await fetchJson("/api/local-audio/devices");
    populateLocalAudioDevices(Array.isArray(devices) ? devices : []);
  } catch (error) {
    console.error(error);
    localFeedNote.textContent = error?.message || "Failed to load local devices.";
  }
}

function closeLocalFeedModal() {
  setLocalFeedModalOpen(false);
  if (localFeedCancelButton) {
    localFeedCancelButton.disabled = false;
  }
  if (localFeedSubmitButton) {
    localFeedSubmitButton.disabled = false;
  }
}

function setSynthesisModalOpen(isOpen) {
  if (!synthesisModal) {
    return;
  }

  synthesisModal.hidden = !isOpen;
}

function closeSynthesisModal() {
  setSynthesisModalOpen(false);
}

function renderSynthesisResult(result) {
  if (!result || !synthesisTitle || !synthesisMeta || !synthesisSummary || !synthesisThemes || !synthesisCategories || !synthesisHighlights) {
    return;
  }

  const dayLabel = result.day || "";
  const feedName = result.feedName || "Selected feed";
  synthesisTitle.textContent = `${feedName} • ${dayLabel}`;
  synthesisMeta.textContent = `${result.transcribedCalls || 0} transcribed / ${result.totalCalls || 0} total calls`;
  synthesisSummary.textContent = result.summary || "No synthesis output available.";

  synthesisThemes.innerHTML = "";
  const themes = Array.isArray(result.keyThemes) ? result.keyThemes : [];
  themes.forEach((theme) => {
    const chip = document.createElement("span");
    chip.className = "synthesis-theme";
    chip.textContent = String(theme);
    synthesisThemes.appendChild(chip);
  });

  synthesisCategories.innerHTML = "";
  const categories = Array.isArray(result.categories) ? result.categories : [];
  categories.forEach((category) => {
    const item = document.createElement("li");
    item.textContent = `${category.category}: ${category.count}`;
    synthesisCategories.appendChild(item);
  });

  synthesisHighlights.innerHTML = "";
  const highlights = Array.isArray(result.highlights) ? result.highlights : [];
  if (highlights.length === 0) {
    const empty = document.createElement("p");
    empty.className = "muted";
    empty.textContent = "No notable highlights were extracted for this day.";
    synthesisHighlights.appendChild(empty);
    return;
  }

  highlights.forEach((highlight) => {
    const card = document.createElement("article");
    card.className = "synthesis-highlight";

    const header = document.createElement("div");
    header.className = "synthesis-highlight-header";

    const time = document.createElement("span");
    time.className = "synthesis-highlight-time";
    const highlightDate = new Date(highlight.startUtc);
    time.textContent = Number.isNaN(highlightDate.getTime())
      ? "Unknown time"
      : highlightDate.toLocaleString();

    const category = document.createElement("span");
    category.className = "synthesis-highlight-category";
    category.textContent = String(highlight.category || "General Dispatch");

    const score = document.createElement("span");
    score.className = "synthesis-highlight-score";
    score.textContent = `Score ${Number(highlight.score || 0).toFixed(1)}`;

    header.appendChild(time);
    header.appendChild(category);
    header.appendChild(score);
    card.appendChild(header);

    const excerpt = document.createElement("p");
    excerpt.textContent = String(highlight.excerpt || "");
    card.appendChild(excerpt);

    synthesisHighlights.appendChild(card);
  });
}

async function synthesizeDay(dayKey, dayEntry) {
  if (!selectedFeedId) {
    return;
  }

  const button = dayEntry?.synthesize;
  const originalLabel = button?.textContent || "Synthesize";
  if (button) {
    button.disabled = true;
    button.textContent = "Working...";
  }

  try {
    const result = await fetchJson(
      `/api/feeds/${selectedFeedId}/synthesis?day=${encodeURIComponent(dayKey)}&includeArchived=${showArchived}`
    );
    renderSynthesisResult(result);
    setSynthesisModalOpen(true);
  } catch (error) {
    console.error(error);
    if (synthesisTitle && synthesisMeta && synthesisSummary && synthesisThemes && synthesisCategories && synthesisHighlights) {
      synthesisTitle.textContent = `Synthesis failed • ${dayKey}`;
      synthesisMeta.textContent = "";
      synthesisSummary.textContent = error?.message || "Unable to synthesize this day right now.";
      synthesisThemes.innerHTML = "";
      synthesisCategories.innerHTML = "";
      synthesisHighlights.innerHTML = "";
      setSynthesisModalOpen(true);
    }
  } finally {
    if (button) {
      button.disabled = false;
      button.textContent = originalLabel;
    }
  }
}

async function submitLocalFeed() {
  if (!localDeviceSelect || !localFeedSubmitButton || !localFeedCancelButton || !localFeedNote) {
    return;
  }

  const deviceId = localDeviceSelect.value;
  if (!deviceId) {
    return;
  }

  localFeedSubmitButton.disabled = true;
  localFeedCancelButton.disabled = true;
  localFeedNote.textContent = "Adding source...";

  try {
    const payload = {
      deviceId,
      name: localFeedNameInput?.value?.trim() || null,
      startImmediately: localFeedAutoStart?.checked !== false
    };
    const createdFeed = await fetchJson("/api/feeds/local", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });

    closeLocalFeedModal();
    await loadActiveFeeds();
    if (createdFeed?.id) {
      selectFeed(createdFeed.id);
    }
  } catch (error) {
    console.error(error);
    localFeedNote.textContent = error?.message || "Failed to add local source.";
    localFeedSubmitButton.disabled = false;
    localFeedCancelButton.disabled = false;
  }
}

async function loadStates() {
  treeContainer.innerHTML = "";
  stateStore.clear();
  preloadToken += 1;
  const currentToken = preloadToken;

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

  preloadStates(currentToken).catch((err) => console.error(err));
}

async function toggleState(stateEntry) {
  if (!stateEntry.loaded) {
    await loadStateFeeds(stateEntry);
  }

  stateEntry.expanded = !stateEntry.children.hidden;
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
  const isExpanded = !stateEntry.children.hidden;
  stateEntry.expanded = isExpanded;
  stateEntry.toggle.textContent = isExpanded ? "▾" : "▸";
  applySearchFilter();
}

async function preloadStates(token) {
  const entries = Array.from(stateStore.values());
  if (entries.length === 0) {
    return;
  }

  const queue = entries.slice();
  const workerCount = Math.min(PRELOAD_CONCURRENCY, queue.length);
  const workers = Array.from({ length: workerCount }, async () => {
    while (queue.length > 0) {
      if (token !== preloadToken) {
        return;
      }

      const entry = queue.shift();
      if (!entry || entry.loaded) {
        continue;
      }

      try {
        await loadStateFeeds(entry);
      } catch (error) {
        console.error(error);
      }
    }
  });

  await Promise.all(workers);
}

function toggleCounty(countyEntry) {
  countyEntry.expanded = !countyEntry.children.hidden;
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
      await loadRecordings({ force: true });
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
  const storedIntervalMs = localStorage.getItem(STORAGE_KEYS.refreshMs);
  const storedIntervalLegacySeconds = localStorage.getItem(STORAGE_KEYS.refreshSecondsLegacy);

  refreshEnabled = storedEnabled !== null ? storedEnabled === "true" : true;
  if (storedIntervalMs !== null) {
    refreshIntervalMs = Number(storedIntervalMs);
  } else if (storedIntervalLegacySeconds !== null) {
    refreshIntervalMs = Number(storedIntervalLegacySeconds) * 1000;
  } else {
    refreshIntervalMs = DEFAULT_REFRESH_MS;
  }

  if (!Number.isFinite(refreshIntervalMs) || refreshIntervalMs < 0) {
    refreshIntervalMs = DEFAULT_REFRESH_MS;
  }

  autoRefreshToggle.checked = refreshEnabled;
  refreshIntervalInput.value = String(refreshIntervalMs);
  refreshIntervalValue.textContent = `Every ${refreshIntervalMs} ms`;
}

async function loadUiConfig() {
  try {
    const config = await fetchJson("/api/ui-config");
    if (config && typeof config === "object") {
      if (Number.isFinite(config.expectedRealtimeFactor)) {
        uiConfig.expectedRealtimeFactor = config.expectedRealtimeFactor;
      }
      if (Number.isFinite(config.estimatedBytesPerSecond)) {
        uiConfig.estimatedBytesPerSecond = config.estimatedBytesPerSecond;
      }
    }
  } catch (error) {
    console.error(error);
  }
}

function loadArchiveSettings() {
  const storedArchived = localStorage.getItem(STORAGE_KEYS.showArchived);
  showArchived = storedArchived === "true";
  showArchivedToggle.checked = showArchived;
}

function applyArchiveSettings() {
  showArchived = showArchivedToggle.checked;
  localStorage.setItem(STORAGE_KEYS.showArchived, String(showArchived));
  loadRecordings({ force: true }).catch((err) => console.error(err));
}

function applyRefreshSettings() {
  refreshEnabled = autoRefreshToggle.checked;
  refreshIntervalMs = Number(refreshIntervalInput.value);
  if (!Number.isFinite(refreshIntervalMs) || refreshIntervalMs < 0) {
    refreshIntervalMs = DEFAULT_REFRESH_MS;
    refreshIntervalInput.value = String(refreshIntervalMs);
  }

  localStorage.setItem(STORAGE_KEYS.autoRefresh, String(refreshEnabled));
  localStorage.setItem(STORAGE_KEYS.refreshMs, String(refreshIntervalMs));
  localStorage.removeItem(STORAGE_KEYS.refreshSecondsLegacy);
  refreshIntervalValue.textContent = `Every ${refreshIntervalMs} ms`;
  restartAutoRefresh();
}

function restartAutoRefresh() {
  if (progressTimer) {
    clearInterval(progressTimer);
    progressTimer = null;
  }

  if (!refreshEnabled) {
    disconnectRecordingStream();
    disconnectFeedStream();
    return;
  }

  connectFeedStream();
  connectRecordingStream();
  progressTimer = setInterval(() => {
    updateProcessingProgress();
    syncSelectedFeedFromApi().catch((error) => console.error(error));
  }, refreshIntervalMs);
}

function renderActiveFeeds(feeds) {
  if (feeds.length === 0) {
    activeFeedNodes.forEach((entry) => {
      stopFeedListen(entry);
      entry.card.remove();
    });
    activeFeedNodes.clear();
    activeFeedsContainer.innerHTML = "<p class=\"muted\">No active feeds yet.</p>";
    return;
  }

  if (activeFeedsContainer.querySelector("p.muted")) {
    activeFeedsContainer.innerHTML = "";
  }

  const incomingIds = new Set(feeds.map((feed) => toFeedKey(feed.id)));

  feeds.forEach((feed) => {
    const feedKey = toFeedKey(feed.id);
    let entry = activeFeedNodes.get(feedKey);
    if (!entry) {
      entry = createActiveFeedCard(feed);
      activeFeedNodes.set(feedKey, entry);
      activeFeedsContainer.appendChild(entry.card);
    } else {
      entry.feed = feed;
      updateActiveFeedCard(entry);
    }
  });

  for (const [id, entry] of activeFeedNodes.entries()) {
    if (!incomingIds.has(id)) {
      stopFeedListen(entry);
      entry.card.remove();
      activeFeedNodes.delete(id);
    }
  }

  if (selectedFeedId && !incomingIds.has(toFeedKey(selectedFeedId))) {
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

function getFeedListenUrl(feedId) {
  return `/api/feeds/${encodeURIComponent(feedId)}/listen`;
}

function updateFeedListenButton(entry) {
  if (!entry || !entry.listenToggle) {
    return;
  }

  const isRunning = Boolean(entry.feed?.isRunning);
  entry.listenToggle.hidden = !isRunning;
  if (!isRunning) {
    entry.listenToggle.classList.remove("is-live");
    entry.listenToggle.innerHTML = "&#128263;";
    entry.listenToggle.title = "Unmute monitor";
    entry.listenToggle.setAttribute("aria-label", "Unmute monitor");
    return;
  }

  const isLive = Boolean(entry.isListening);
  entry.listenToggle.classList.toggle("is-live", isLive);
  entry.listenToggle.innerHTML = isLive ? "&#128266;" : "&#128263;";
  entry.listenToggle.title = isLive ? "Mute monitor" : "Unmute monitor";
  entry.listenToggle.setAttribute("aria-label", isLive ? "Mute monitor" : "Unmute monitor");
}

function stopFeedListen(entry) {
  if (!entry || !entry.monitorAudio) {
    return;
  }

  entry.isListening = false;
  try {
    entry.monitorAudio.pause();
    entry.monitorAudio.removeAttribute("src");
    entry.monitorAudio.load();
  } catch (error) {
    console.error(error);
  }
  updateFeedListenButton(entry);
}

async function startFeedListen(entry) {
  if (!entry?.feed?.id || !entry.feed.isRunning || !entry.monitorAudio) {
    return;
  }

  entry.isListening = true;
  entry.monitorAudio.src = getFeedListenUrl(entry.feed.id);
  updateFeedListenButton(entry);

  try {
    await entry.monitorAudio.play();
  } catch (error) {
    console.error(error);
    stopFeedListen(entry);
  }
}

function createActiveFeedCard(feed) {
  const fragment = activeFeedTemplate.content.cloneNode(true);
  const card = fragment.querySelector(".active-feed-card");
  const title = fragment.querySelector(".active-title");
  const meta = fragment.querySelector(".active-meta");
  const badge = fragment.querySelector(".badge");
  const listenToggle = fragment.querySelector(".listen-toggle");
  const toggle = fragment.querySelector(".toggle");
  const remove = fragment.querySelector(".remove");
  const monitorAudio = document.createElement("audio");
  monitorAudio.preload = "none";
  monitorAudio.autoplay = true;
  monitorAudio.hidden = true;
  card.appendChild(monitorAudio);

  const entry = { feed, card, title, meta, badge, listenToggle, toggle, remove, monitorAudio, isListening: false };
  if (listenToggle) {
    listenToggle.type = "button";
  }
  toggle.type = "button";
  remove.type = "button";

  if (listenToggle) {
    listenToggle.addEventListener("click", async (event) => {
      event.stopPropagation();
      if (!entry.feed?.isRunning) {
        return;
      }

      if (entry.isListening) {
        stopFeedListen(entry);
        return;
      }

      await startFeedListen(entry);
    });
  }

  toggle.addEventListener("click", async (event) => {
    event.stopPropagation();
    toggle.disabled = true;
    try {
      const currentFeed = entry.feed;
      if (!currentFeed || !currentFeed.id) {
        return;
      }

      const nextState = !Boolean(currentFeed.isRunning);
      const prevState = Boolean(currentFeed.isRunning);
      const prevActive = Boolean(currentFeed.isActive);
      currentFeed.isRunning = nextState;
      currentFeed.isActive = nextState || prevActive;
      updateActiveFeedCard(entry);
      updateActiveStatusFromNodes();

      const endpoint = nextState ? "start" : "stop";
      await fetchJson(`/api/feeds/${currentFeed.id}/${endpoint}`, { method: "POST" });
    } catch (error) {
      console.error(error);
    } finally {
      try {
        await loadActiveFeeds();
      } catch (error) {
        console.error(error);
      }
      toggle.disabled = false;
    }
  });

  remove.addEventListener("click", async (event) => {
    event.stopPropagation();
    remove.disabled = true;
    try {
      const currentFeed = entry.feed;
      if (!currentFeed || !currentFeed.id) {
        return;
      }

      await fetchJson(`/api/feeds/${currentFeed.id}`, { method: "DELETE" });
      stopFeedListen(entry);
      activeFeedNodes.delete(toFeedKey(currentFeed.id));
      card.remove();
      updateActiveStatusFromNodes();

      if (activeFeedNodes.size === 0) {
        activeFeedsContainer.innerHTML = "<p class=\"muted\">No active feeds yet.</p>";
      }

      if (toFeedKey(selectedFeedId) === toFeedKey(currentFeed.id)) {
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

  card.addEventListener("click", () => selectFeed(entry.feed.id));

  updateActiveFeedCard(entry);
  return entry;
}

function updateActiveFeedCard(entry) {
  const feed = entry.feed;
  if (!feed.isRunning && entry.isListening) {
    stopFeedListen(entry);
  }

  entry.title.textContent = feed.name;
  entry.meta.textContent = `Feed ID ${feed.feedIdentifier}`;
  entry.badge.textContent = feed.isRunning ? "Live" : "Paused";
  entry.badge.classList.remove("badge-live", "badge-paused");
  entry.badge.classList.add(feed.isRunning ? "badge-live" : "badge-paused");
  entry.toggle.textContent = feed.isRunning ? "Stop" : "Start";
  entry.toggle.classList.remove("is-start", "is-stop");
  entry.toggle.classList.add(feed.isRunning ? "is-stop" : "is-start");
  updateFeedListenButton(entry);
  entry.card.classList.toggle("selected", toFeedKey(feed.id) === toFeedKey(selectedFeedId));
}

function flashFeedCard(feedId) {
  const feedKey = toFeedKey(feedId);
  const entry = activeFeedNodes.get(feedKey);
  if (!entry) {
    return;
  }

  entry.card.classList.add("feed-flash");
  const existing = feedFlashTimers.get(feedKey);
  if (existing) {
    clearTimeout(existing);
  }

  const timer = setTimeout(() => {
    entry.card.classList.remove("feed-flash");
    feedFlashTimers.delete(feedKey);
  }, 4000);
  feedFlashTimers.set(feedKey, timer);
}

function selectFeed(feedId) {
  selectedFeedId = feedId;
  const selectedFeedKey = toFeedKey(feedId);
  for (const entry of activeFeedNodes.values()) {
    entry.card.classList.toggle("selected", toFeedKey(entry.feed.id) === selectedFeedKey);
  }

  const selected = activeFeedNodes.get(selectedFeedKey);
  updateRecordingHeader(selected?.feed ?? null);
  resetRecordingView();
  loadRecordings({ force: true });
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

function resetRecordingView() {
  recordingsInitialized = false;
  latestRecordingStart = null;
  oldestRecordingStart = null;
  canLoadOlderRecordings = false;
  recordingsPageLoading = false;
  recordingNodes.clear();
  recordingDayNodes.clear();
  recordingDayTotals.clear();
  recordingsContainer.innerHTML = "";
}

function buildRecordingsUrl(options = {}) {
  const feedId = options.feedId || selectedFeedId;
  if (!feedId) {
    return "";
  }

  const params = new URLSearchParams();
  params.set("includeArchived", String(options.includeArchived ?? showArchived));

  if (options.since) {
    params.set("since", options.since);
  }
  if (options.day) {
    params.set("day", options.day);
  }
  if (options.before) {
    params.set("before", options.before);
  }
  if (Number.isFinite(options.limit) && options.limit > 0) {
    params.set("limit", String(Math.trunc(options.limit)));
  }

  return `/api/feeds/${feedId}/recordings?${params.toString()}`;
}

function updateDayCount(dayKey, dayEntry) {
  if (!dayEntry) {
    return;
  }

  const totalCalls = recordingDayTotals.has(dayKey)
    ? recordingDayTotals.get(dayKey)
    : dayEntry.list.children.length;
  const normalized = Number.isFinite(totalCalls) ? Math.max(0, Number(totalCalls)) : dayEntry.list.children.length;
  dayEntry.count.textContent = `${normalized} calls`;
}

function updateOldestRecordingStart(recordings) {
  if (!recordings || recordings.length === 0) {
    return;
  }

  let oldest = oldestRecordingStart ? new Date(oldestRecordingStart) : null;
  if (oldest && Number.isNaN(oldest.getTime())) {
    oldest = null;
  }

  recordings.forEach((recording) => {
    const date = new Date(recording.startUtc);
    if (Number.isNaN(date.getTime())) {
      return;
    }
    if (!oldest || date < oldest) {
      oldest = date;
    }
  });

  if (oldest) {
    oldestRecordingStart = oldest.toISOString();
  }
}

async function loadInitialRecordingsPage() {
  if (!selectedFeedId || recordingsPageLoading) {
    return;
  }

  const feedIdAtRequest = selectedFeedId;
  recordingsPageLoading = true;
  try {
    const daySummariesResponse = await fetchJson(
      `/api/feeds/${feedIdAtRequest}/recordings/days?includeArchived=${showArchived}`
    );
    if (toFeedKey(selectedFeedId) !== toFeedKey(feedIdAtRequest)) {
      return;
    }

    const daySummaries = Array.isArray(daySummariesResponse) ? daySummariesResponse : [];
    if (daySummaries.length === 0) {
      recordingsContainer.innerHTML = "<p class=\"muted\">No recordings yet.</p>";
      recordingsInitialized = true;
      latestRecordingStart = new Date().toISOString();
      oldestRecordingStart = null;
      canLoadOlderRecordings = false;
      return;
    }

    const orderedDays = daySummaries
      .map((item) => ({
        day: String(item.day || ""),
        totalCalls: Number(item.totalCalls || 0)
      }))
      .filter((item) => item.day.length > 0)
      .sort((a, b) => (a.day < b.day ? 1 : -1));

    recordingDayTotals.clear();
    const todayKey = toDateKey(new Date());
    orderedDays.forEach((item) => {
      recordingDayTotals.set(item.day, Math.max(0, item.totalCalls));
      const dayEntry = ensureDayEntry(item.day, todayKey);
      dayEntry.label.textContent = item.day === todayKey ? "Today" : formatDateLabel(item.day);
      updateDayCount(item.day, dayEntry);
      if (!dayEntry.root.isConnected) {
        recordingsContainer.appendChild(dayEntry.root);
      }
    });

    const loadedRecordings = [];
    for (let i = 0; i < orderedDays.length; i += PRELOAD_CONCURRENCY) {
      const batch = orderedDays.slice(i, i + PRELOAD_CONCURRENCY);
      const batchResponses = await Promise.all(
        batch.map(async (dayItem) => {
          try {
            const response = await fetchJson(
              buildRecordingsUrl({
                feedId: feedIdAtRequest,
                day: dayItem.day,
                limit: RECORDINGS_PER_DAY_INITIAL
              })
            );
            return Array.isArray(response) ? response : [];
          } catch (error) {
            console.error(error);
            return [];
          }
        })
      );

      if (toFeedKey(selectedFeedId) !== toFeedKey(feedIdAtRequest)) {
        return;
      }

      batchResponses.forEach((records) => loadedRecordings.push(...records));
    }

    if (loadedRecordings.length > 0) {
      insertRecordings(loadedRecordings, { adjustTotals: false });
      updateLatestRecordingStart(loadedRecordings);
      updateOldestRecordingStart(loadedRecordings);
    } else {
      latestRecordingStart = new Date().toISOString();
      oldestRecordingStart = null;
    }

    recordingsInitialized = true;
    canLoadOlderRecordings = false;
  } finally {
    recordingsPageLoading = false;
  }
}

async function loadRecordings(options = {}) {
  if (!selectedFeedId) {
    return;
  }

  const force = options.force === true;
  if (force || !recordingsInitialized) {
    resetRecordingView();
    await loadInitialRecordingsPage();
    return;
  }

  await appendNewRecordings();
  await refreshRecordingStatuses();
}

function updateLatestRecordingStart(recordings) {
  if (!recordings || recordings.length === 0) {
    return;
  }

  let latest = latestRecordingStart ? new Date(latestRecordingStart) : null;
  if (latest && Number.isNaN(latest.getTime())) {
    latest = null;
  }

  recordings.forEach((recording) => {
    const date = new Date(recording.startUtc);
    if (Number.isNaN(date.getTime())) {
      return;
    }
    if (!latest || date > latest) {
      latest = date;
    }
  });

  if (latest) {
    latestRecordingStart = latest.toISOString();
  }
}

function getSinceTimestamp() {
  if (!latestRecordingStart) {
    return null;
  }

  const since = new Date(latestRecordingStart);
  if (Number.isNaN(since.getTime())) {
    return null;
  }

  since.setSeconds(since.getSeconds() - 1);
  return since.toISOString();
}

async function appendNewRecordings() {
  if (!selectedFeedId) {
    return;
  }

  const feedIdAtRequest = selectedFeedId;
  const since = getSinceTimestamp();
  if (!since) {
    return;
  }

  const recordings = await fetchJson(buildRecordingsUrl({
    feedId: feedIdAtRequest,
    since
  }));
  if (toFeedKey(selectedFeedId) !== toFeedKey(feedIdAtRequest)) {
    return;
  }

  const page = Array.isArray(recordings) ? recordings : [];

  if (!page.length) {
    return;
  }

  insertRecordings(page, { adjustTotals: true });
}

async function refreshRecordingStatuses() {
  const ids = [];
  for (const [id, node] of recordingNodes.entries()) {
    if (node.currentStatus === "Pending" || node.currentStatus === "Processing") {
      ids.push(id);
    }
  }

  if (ids.length === 0) {
    return;
  }

  await refreshRecordingStatusesByIds(ids);
}

async function refreshRecordingStatusesByIds(ids) {
  if (!ids || ids.length === 0) {
    return;
  }

  const response = await fetchJson("/api/recordings/batch", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ recordingIds: ids })
  });

  const recordings = Array.isArray(response) ? response : [];
  recordings.forEach((recording) => {
    const node = recordingNodes.get(recording.id);
    if (node) {
      updateRecordingNode(node, recording);
    }
  });
}

async function syncSelectedFeedFromApi() {
  if (!selectedFeedId || !recordingsInitialized || streamSyncInFlight) {
    return;
  }

  streamSyncInFlight = true;
  try {
    await appendNewRecordings();
    await refreshRecordingStatuses();
  } finally {
    streamSyncInFlight = false;
  }
}

function connectRecordingStream() {
  disconnectRecordingStream();

  if (!refreshEnabled) {
    return;
  }

  const url = "/api/recordings/stream";
  eventSource = new EventSource(url);

  const handleEvent = (type) => async (event) => {
    try {
      const payload = JSON.parse(event.data);
      if (!payload) {
        return;
      }

      if (type === "created" && payload.feedId) {
        flashFeedCard(payload.feedId);
      }

      const payloadFeedId = payload?.feedId ? String(payload.feedId).toLowerCase() : "";
      const selectedId = selectedFeedId ? String(selectedFeedId).toLowerCase() : "";
      if (!selectedId || payloadFeedId !== selectedId) {
        return;
      }

      if (type === "archived") {
        if (!showArchived) {
          removeRecordingNode(payload.recordingId);
          await refreshRecordingStatuses();
          return;
        }
      }

      const recording = await fetchJson(`/api/recordings/${payload.recordingId}`);
      if (recording.isArchived && !showArchived) {
        removeRecordingNode(payload.recordingId);
        return;
      }

      if (recordingNodes.has(recording.id)) {
        const node = recordingNodes.get(recording.id);
        if (node) {
          updateRecordingNode(node, recording);
        }
      } else {
        insertRecordings([recording], { adjustTotals: type === "created" });
      }

      await refreshRecordingStatuses();
    } catch (error) {
      console.error(error);
    }
  };

  eventSource.addEventListener("open", () => {
    syncSelectedFeedFromApi().catch((error) => console.error(error));
  });

  eventSource.addEventListener("snapshot", async (event) => {
    try {
      const payload = JSON.parse(event.data);
      const snapshotFeedId = payload?.feedId ? String(payload.feedId).toLowerCase() : null;
      const selectedId = selectedFeedId ? String(selectedFeedId).toLowerCase() : "";
      const ids = Array.isArray(payload?.recordingIds) ? payload.recordingIds : [];

      if (snapshotFeedId && selectedId && snapshotFeedId === selectedId && ids.length > 0) {
        await refreshRecordingStatusesByIds(ids);
      }

      await syncSelectedFeedFromApi();
    } catch (error) {
      console.error(error);
    }
  });

  eventSource.addEventListener("created", handleEvent("created"));
  eventSource.addEventListener("updated", handleEvent("updated"));
  eventSource.addEventListener("archived", handleEvent("archived"));
  eventSource.onerror = () => {
    if (!refreshEnabled) {
      return;
    }
    disconnectRecordingStream();
    setTimeout(() => {
      if (refreshEnabled) {
        connectRecordingStream();
      }
    }, 1000);
  };
}

function disconnectRecordingStream() {
  if (eventSource) {
    eventSource.close();
    eventSource = null;
  }
}

function connectFeedStream() {
  disconnectFeedStream();

  if (!refreshEnabled) {
    return;
  }

  feedEventSource = new EventSource("/api/feeds/stream");
  feedEventSource.addEventListener("snapshot", (event) => {
    try {
      const payload = JSON.parse(event.data);
      if (!Array.isArray(payload)) {
        return;
      }

      payload.forEach((item) => {
        if (!item || !item.feedId) {
          return;
        }

        const entry = activeFeedNodes.get(toFeedKey(item.feedId));
        if (!entry) {
          return;
        }

        if (typeof item.isRunning === "boolean") {
          entry.feed.isRunning = item.isRunning;
        }
        if (item.isActive !== undefined && item.isActive !== null) {
          entry.feed.isActive = item.isActive;
        }

        updateActiveFeedCard(entry);
      });

      updateActiveStatusFromNodes();
    } catch (error) {
      console.error(error);
    }
  });
  feedEventSource.addEventListener("updated", (event) => {
    try {
      const payload = JSON.parse(event.data);
      if (!payload || !payload.feedId) {
        return;
      }

      const entry = activeFeedNodes.get(toFeedKey(payload.feedId));
      if (!entry) {
        return;
      }

      if (typeof payload.isRunning === "boolean") {
        entry.feed.isRunning = payload.isRunning;
      }
      if (payload.isActive !== undefined && payload.isActive !== null) {
        entry.feed.isActive = payload.isActive;
      }

      updateActiveFeedCard(entry);
      updateActiveStatusFromNodes();
    } catch (error) {
      console.error(error);
    }
  });
  feedEventSource.onerror = () => {
    if (!refreshEnabled) {
      return;
    }
    disconnectFeedStream();
    setTimeout(() => {
      if (refreshEnabled) {
        connectFeedStream();
      }
    }, 1000);
  };
}

function disconnectFeedStream() {
  if (feedEventSource) {
    feedEventSource.close();
    feedEventSource = null;
  }
}

function removeRecordingNode(recordingId) {
  const node = recordingNodes.get(recordingId);
  if (!node) {
    return;
  }

  const parentList = node.root.parentElement;
  node.root.remove();
  recordingNodes.delete(recordingId);

  if (parentList && parentList.classList.contains("day-list")) {
    const dayRoot = parentList.closest(".recording-day");
    if (dayRoot) {
      const dayKey = dayRoot.dataset.dayKey;
      const dayEntry = dayKey ? recordingDayNodes.get(dayKey) : null;
      if (dayEntry) {
        if (dayKey && recordingDayTotals.has(dayKey)) {
          const current = Number(recordingDayTotals.get(dayKey) || 0);
          recordingDayTotals.set(dayKey, Math.max(0, current - 1));
        }

        updateDayCount(dayKey || "", dayEntry);
        const totalCalls = dayKey && recordingDayTotals.has(dayKey)
          ? Number(recordingDayTotals.get(dayKey) || 0)
          : dayEntry.list.children.length;

        if (totalCalls <= 0) {
          dayEntry.root.remove();
          if (dayKey) {
            recordingDayNodes.delete(dayKey);
            recordingDayTotals.delete(dayKey);
          }
        }
      }
    }
  }
}

function renderRecordings(recordings) {
  const dayScrollStates = captureDayScrollStates();

  if (!recordings.length) {
    recordingsContainer.innerHTML = "<p class=\"muted\">No recordings yet.</p>";
    recordingDayNodes.clear();
    recordingNodes.clear();
    recordingsInitialized = true;
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
  const seenDayKeys = new Set(orderedKeys);

  orderedKeys.forEach((key) => {
    const recordingsForDay = groups.get(key) || [];
    const dayEntry = ensureDayEntry(key, todayKey);

    const label = key === todayKey ? "Today" : formatDateLabel(key);
    dayEntry.label.textContent = label;
    updateDayCount(key, dayEntry);

    const desiredIds = recordingsForDay.map((recording) => recording.id);
    const desiredSet = new Set(desiredIds);

    recordingsForDay.forEach((recording) => {
      const isNew = !recordingNodes.has(recording.id);
      const shouldHighlight = isNew && shouldHighlightRecording(recording);
      const node = getRecordingNode(recording, shouldHighlight);
      dayEntry.list.appendChild(node.root);
    });

    Array.from(dayEntry.list.children).forEach((child) => {
      const recordingId = child.dataset?.recordingId;
      if (recordingId && !desiredSet.has(recordingId)) {
        const existing = recordingNodes.get(recordingId);
        if (existing) {
          recordingNodes.delete(recordingId);
        }
        child.remove();
      }
    });

  });

  for (const [key, entry] of recordingDayNodes.entries()) {
    if (!seenDayKeys.has(key)) {
      entry.root.remove();
      recordingDayNodes.delete(key);
      recordingDayTotals.delete(key);
    }
  }

  orderedKeys.forEach((key) => {
    const entry = recordingDayNodes.get(key);
    if (entry) {
      recordingsContainer.appendChild(entry.root);
    }
  });

  recordingsInitialized = true;

  if (dayScrollStates.size > 0) {
    requestAnimationFrame(() => {
      requestAnimationFrame(() => restoreDayScrollStates(dayScrollStates));
    });
  }
}

function ensureDayEntry(key, todayKey) {
  let dayEntry = recordingDayNodes.get(key);
  if (dayEntry) {
    return dayEntry;
  }

  const dayFragment = recordingDayTemplate.content.cloneNode(true);
  const dayRoot = dayFragment.querySelector(".recording-day");
  const dayToggle = dayFragment.querySelector(".day-toggle");
  const daySynthesize = dayFragment.querySelector(".day-synthesize");
  const dayArchive = dayFragment.querySelector(".day-archive");
  const dayLabel = dayFragment.querySelector(".day-label");
  const dayCount = dayFragment.querySelector(".day-count");
  const dayList = dayFragment.querySelector(".day-list");

  const expandedDefault = !recordingsInitialized && key === todayKey;
  dayList.hidden = !expandedDefault;
  dayRoot.dataset.dayKey = key;
  dayEntry = {
    key,
    root: dayRoot,
    toggle: dayToggle,
    synthesize: daySynthesize,
    archive: dayArchive,
    label: dayLabel,
    count: dayCount,
    list: dayList,
    expanded: expandedDefault
  };

  dayToggle.addEventListener("click", () => {
    dayEntry.expanded = !dayEntry.expanded;
    dayEntry.list.hidden = !dayEntry.expanded;
  });

  if (daySynthesize) {
    daySynthesize.addEventListener("click", async (event) => {
      event.stopPropagation();
      await synthesizeDay(key, dayEntry);
    });
  }

  dayArchive.addEventListener("click", async (event) => {
    event.stopPropagation();
    await archiveDay(key);
  });

  recordingDayNodes.set(key, dayEntry);
  return dayEntry;
}

function insertRecordings(recordings, options = {}) {
  if (!recordings.length) {
    return;
  }

  const adjustTotals = options.adjustTotals === true;
  const emptyMessage = recordingsContainer.querySelector("p.muted");
  if (emptyMessage) {
    emptyMessage.remove();
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

  const dayKeys = Array.from(groups.keys()).sort((a, b) => (a < b ? 1 : -1));
  dayKeys.forEach((key) => {
    const recordingsForDay = groups.get(key) || [];
    const dayEntry = ensureDayEntry(key, todayKey);

    const label = key === todayKey ? "Today" : formatDateLabel(key);
    dayEntry.label.textContent = label;

    recordingsForDay.sort((a, b) => (a.startUtc < b.startUtc ? 1 : -1));
    recordingsForDay.forEach((recording) => {
      if (recording.isArchived && !showArchived) {
        return;
      }

      const existing = recordingNodes.get(recording.id);
      if (existing) {
        updateRecordingNode(existing, recording);
        return;
      }

      const shouldHighlight = shouldHighlightRecording(recording);
      const node = getRecordingNode(recording, shouldHighlight);
      dayEntry.list.insertBefore(node.root, dayEntry.list.firstChild);

      if (adjustTotals) {
        if (recordingDayTotals.has(key)) {
          recordingDayTotals.set(key, Number(recordingDayTotals.get(key) || 0) + 1);
        } else {
          recordingDayTotals.set(key, 1);
        }
      }
    });

    updateDayCount(key, dayEntry);

    if (!dayEntry.root.isConnected) {
      const existingDays = Array.from(recordingsContainer.querySelectorAll(".recording-day"));
      const insertBefore = existingDays.find((element) => {
        const dayKey = element.dataset.dayKey;
        return dayKey && dayKey < key;
      });
      if (insertBefore) {
        recordingsContainer.insertBefore(dayEntry.root, insertBefore);
      } else {
        recordingsContainer.appendChild(dayEntry.root);
      }
    }
  });

  updateLatestRecordingStart(recordings);
}

function captureDayScrollStates() {
  const states = new Map();
  for (const [key, entry] of recordingDayNodes.entries()) {
    if (entry.list.hidden) {
      continue;
    }

    const state = captureListScrollState(entry.list);
    if (state) {
      states.set(key, state);
    }
  }
  return states;
}

function restoreDayScrollStates(states) {
  for (const [key, state] of states.entries()) {
    const entry = recordingDayNodes.get(key);
    if (!entry || entry.list.hidden) {
      continue;
    }

    restoreListScrollState(entry.list, state);
  }
}

function captureListScrollState(listEl) {
  const scrollTop = listEl.scrollTop;
  const canScroll = listEl.scrollHeight > listEl.clientHeight;
  if (!canScroll || scrollTop <= 0) {
    return scrollTop > 0 ? { scrollTop, anchorId: null, anchorOffset: 0 } : null;
  }

  const containerRect = listEl.getBoundingClientRect();
  const items = listEl.querySelectorAll(".recording");
  for (const item of items) {
    const rect = item.getBoundingClientRect();
    if (rect.bottom > containerRect.top + 4) {
      const anchorId = item.dataset.recordingId;
      if (anchorId) {
        return {
          scrollTop,
          anchorId,
          anchorOffset: rect.top - containerRect.top
        };
      }
    }
  }

  return { scrollTop, anchorId: null, anchorOffset: 0 };
}

function restoreListScrollState(listEl, state) {
  if (!state) {
    return;
  }

  if (!state.anchorId) {
    listEl.scrollTop = state.scrollTop;
    return;
  }

  const anchor = listEl.querySelector(`.recording[data-recording-id="${state.anchorId}"]`);
  if (!anchor) {
    listEl.scrollTop = state.scrollTop;
    return;
  }

  const containerRect = listEl.getBoundingClientRect();
  const anchorRect = anchor.getBoundingClientRect();
  const newOffset = anchorRect.top - containerRect.top;
  const delta = newOffset - state.anchorOffset;
  listEl.scrollTop = listEl.scrollTop + delta;
}

function updateProcessingProgress() {
  const now = Date.now();
  for (const node of recordingNodes.values()) {
    if (node.currentStatus !== "Processing") {
      continue;
    }
    if (!node.transcriptStartedUtc) {
      continue;
    }

    const started = new Date(node.transcriptStartedUtc).getTime();
    if (Number.isNaN(started)) {
      continue;
    }

    const durationSeconds = Number.isFinite(node.durationSeconds) && node.durationSeconds > 0
      ? node.durationSeconds
      : 1;
    const expectedSeconds = Math.max(durationSeconds / Math.max(uiConfig.expectedRealtimeFactor, 0.1), 1);
    const elapsed = (now - started) / 1000;
    const percent = Math.min(99, Math.max(5, (elapsed / expectedSeconds) * 100));
    node.progressText.textContent = `${Math.round(percent)}%`;
  }
}

async function archiveDay(dayKey) {
  if (!selectedFeedId) {
    return;
  }

  try {
    await fetchJson(
      `/api/feeds/${selectedFeedId}/recordings/archive?day=${encodeURIComponent(dayKey)}`,
      { method: "POST" }
    );
    await loadRecordings({ force: true });
  } catch (error) {
    console.error(error);
  }
}

function getRecordingNode(recording, shouldHighlight) {
  let node = recordingNodes.get(recording.id);
  if (!node) {
    const fragment = recordingTemplate.content.cloneNode(true);
    node = {
      root: fragment.querySelector(".recording"),
      time: fragment.querySelector(".recording-time"),
      badge: fragment.querySelector(".badge"),
      progressText: fragment.querySelector(".progress-text"),
      archivedFlag: fragment.querySelector(".archived-flag"),
      audio: fragment.querySelector(".recording-audio"),
      text: fragment.querySelector(".recording-text"),
      transcriptToggle: fragment.querySelector(".transcript-toggle"),
      archiveToggle: fragment.querySelector(".archive-toggle"),
      newTimeout: null,
      hasTranscript: false
    };
    node.audio.preload = "none";
    node.transcriptToggle.addEventListener("click", (event) => {
      event.stopPropagation();
      toggleTranscript(node);
    });
    node.archiveToggle.addEventListener("click", async (event) => {
      event.stopPropagation();
      if (node.recordingId) {
        await archiveRecording(node.recordingId);
      }
    });
    node.root.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      if (node.recordingId) {
        showContextMenu(event.clientX, event.clientY, node.recordingId);
      }
    });
    node.root.addEventListener("dblclick", (event) => {
      if (event.target.closest(".transcript-toggle") || event.target.closest(".archive-toggle")) {
        return;
      }
      toggleTranscript(node);
    });
    recordingNodes.set(recording.id, node);
    if (shouldHighlight) {
      highlightNewRecording(node);
    }
  }

  updateRecordingNode(node, recording);
  return node;
}

function shouldHighlightRecording(recording) {
  if (!recordingsInitialized) {
    return false;
  }

  const startTime = new Date(recording.startUtc).getTime();
  if (Number.isNaN(startTime)) {
    return false;
  }

  const ageSeconds = (Date.now() - startTime) / 1000;
  return ageSeconds >= 0 && ageSeconds <= NEW_RECORDING_WINDOW_SECONDS;
}

function highlightNewRecording(node) {
  if (node.newTimeout) {
    clearTimeout(node.newTimeout);
  }

  node.root.classList.add("recording-new");
  node.newTimeout = setTimeout(() => {
    node.root.classList.remove("recording-new");
    node.newTimeout = null;
  }, 4000);
}

function updateRecordingNode(node, rec) {
  node.recordingId = rec.id;
  node.root.dataset.recordingId = rec.id;
  node.currentStatus = rec.transcriptStatus;
  node.transcriptStartedUtc = rec.transcriptStartedUtc;
  node.durationSeconds = rec.durationSeconds;
  const start = new Date(rec.startUtc);
  node.time.textContent = start.toLocaleString();
  const showStatus = rec.transcriptStatus !== "Complete";
  node.badge.textContent = showStatus ? rec.transcriptStatus : "";
  node.badge.hidden = !showStatus;
  node.badge.classList.remove("badge-processing", "badge-pending", "badge-failed", "badge-skipped");
  if (showStatus) {
    if (rec.transcriptStatus === "Processing") {
      node.badge.classList.add("badge-processing");
    } else if (rec.transcriptStatus === "Pending") {
      node.badge.classList.add("badge-pending");
    } else if (rec.transcriptStatus === "Failed") {
      node.badge.classList.add("badge-failed");
    } else if (rec.transcriptStatus === "Skipped") {
      node.badge.classList.add("badge-skipped");
    }
  }
  node.root.classList.toggle("archived", rec.isArchived);
  node.archivedFlag.hidden = !rec.isArchived;
  node.archiveToggle.disabled = rec.isArchived;
  node.archiveToggle.textContent = rec.isArchived ? "Archived" : "Archive";

  const percent = Math.round(rec.transcriptProgress || 0);
  if (rec.transcriptStatus === "Processing") {
    node.progressText.textContent = `${percent}%`;
  } else if (rec.transcriptStatus === "Pending") {
    if (rec.transcriptQueuePosition) {
      const total = rec.transcriptQueueTotal || rec.transcriptQueuePosition;
      node.progressText.textContent = `Queue: (${rec.transcriptQueuePosition}/${total})`;
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
  } else if (rec.transcriptStatus === "Processing") {
    node.text.textContent = "Transcribing...";
  } else if (rec.transcriptStatus === "Failed") {
    node.text.textContent = "Transcription failed";
  } else if (rec.transcriptStatus === "Skipped") {
    node.text.textContent = "Transcription skipped";
  } else {
    node.text.textContent = "Transcript pending";
  }

  const hasTranscript = rec.transcriptStatus === "Complete" && Boolean(rec.transcriptText);
  node.hasTranscript = hasTranscript;
  node.transcriptToggle.hidden = !hasTranscript;
  if (!hasTranscript) {
    node.text.setAttribute("hidden", "true");
    node.transcriptToggle.textContent = "Transcript";
  } else if (node.text.hasAttribute("hidden")) {
    node.transcriptToggle.textContent = "Transcript";
  } else {
    node.transcriptToggle.textContent = "Hide";
  }
}

function toggleTranscript(node) {
  if (!node.hasTranscript) {
    return;
  }

  const isHidden = node.text.hasAttribute("hidden");
  if (isHidden) {
    node.text.removeAttribute("hidden");
    node.transcriptToggle.textContent = "Hide";
  } else {
    node.text.setAttribute("hidden", "true");
    node.transcriptToggle.textContent = "Transcript";
  }
}

async function archiveRecording(recordingId) {
  try {
    await fetchJson(`/api/recordings/${recordingId}/archive`, { method: "POST" });
    await loadRecordings({ force: true });
  } catch (error) {
    console.error(error);
  }
}

function toDateKey(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function formatDateLabel(key) {
  const date = new Date(`${key}T00:00:00`);
  return date.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" });
}

async function addFeedFromDiscovery(payload) {
  const requestedFeedId = String(payload?.feedId ?? "").trim();
  if (!requestedFeedId) {
    throw new Error("Invalid feed selection.");
  }

  const feeds = await fetchJson("/api/feeds");
  const existing = feeds.find((feed) => String(feed.feedIdentifier) === requestedFeedId);

  let targetFeed = existing;
  if (!existing) {
    targetFeed = await fetchJson("/api/feeds", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        broadcastifyUrl: requestedFeedId,
        name: `${payload.feedName} (${payload.county}, ${payload.state})`
      })
    });
  }

  try {
    if (targetFeed) {
      await fetchJson(`/api/feeds/${targetFeed.id}/start`, { method: "POST" });
    }
  } finally {
    await loadActiveFeeds();
  }
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
if (addLocalFeedButton) {
  addLocalFeedButton.addEventListener("click", () => {
    openLocalFeedModal().catch((err) => console.error(err));
  });
}
if (localFeedCancelButton) {
  localFeedCancelButton.addEventListener("click", closeLocalFeedModal);
}
if (localFeedSubmitButton) {
  localFeedSubmitButton.addEventListener("click", () => {
    submitLocalFeed().catch((err) => console.error(err));
  });
}
if (localFeedNameInput) {
  localFeedNameInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      submitLocalFeed().catch((err) => console.error(err));
    }
  });
}
if (localFeedModal) {
  localFeedModal.addEventListener("click", (event) => {
    if (event.target === localFeedModal) {
      closeLocalFeedModal();
    }
  });
}
if (synthesisCloseButton) {
  synthesisCloseButton.addEventListener("click", closeSynthesisModal);
}
if (synthesisModal) {
  synthesisModal.addEventListener("click", (event) => {
    if (event.target === synthesisModal) {
      closeSynthesisModal();
    }
  });
}
document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") {
    return;
  }

  if (localFeedModal && !localFeedModal.hidden) {
    closeLocalFeedModal();
    return;
  }

  if (synthesisModal && !synthesisModal.hidden) {
    closeSynthesisModal();
  }
});
searchInput.addEventListener("input", applySearchFilter);
navLinks.forEach((link) => {
  link.addEventListener("click", () => showPage(link.dataset.page));
});
autoRefreshToggle.addEventListener("change", applyRefreshSettings);
refreshIntervalInput.addEventListener("change", applyRefreshSettings);
refreshIntervalInput.addEventListener("blur", applyRefreshSettings);
  showArchivedToggle.addEventListener("change", applyArchiveSettings);

document.getElementById("sign-out-btn")?.addEventListener("click", () => {
  window.location.href = "/api/auth/logout";
});

(async () => {
  try {
    const user = await fetchJson("/api/auth/me");
    if (!user) return; // fetchJson already redirected on 401
    const emailEl = document.getElementById("user-email");
    if (emailEl) emailEl.textContent = user.email;
  } catch {
    window.location.href = "/login.html";
    return;
  }

  setupContextMenu();
  setupDragAndDrop();
  loadUiConfig();
  loadStates().catch((err) => console.error(err));
  loadActiveFeeds().catch((err) => console.error(err));
  loadRefreshSettings();
  loadArchiveSettings();
  restartAutoRefresh();
  showPage("dashboard");
})();
