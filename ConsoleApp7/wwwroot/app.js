const STORAGE_KEY = "nba-player-lab-v2";
const API_URL = "/api/players";

const attributes = [
  { key: "dribble", label: "运球", color: "#2563eb" },
  { key: "shooting", label: "投篮", color: "#dc2626" },
  { key: "iq", label: "球商", color: "#7c3aed" },
  { key: "personality", label: "性格", color: "#0891b2" },
  { key: "body", label: "身体", color: "#16a34a" },
  { key: "mental", label: "精神", color: "#f59e0b" }
];

const state = {
  players: [],
  selectedId: null,
  query: "",
  team: "all",
  position: "all",
  groupBy: "team"
};

const els = {
  searchInput: document.querySelector("#searchInput"),
  teamFilter: document.querySelector("#teamFilter"),
  positionFilter: document.querySelector("#positionFilter"),
  groupBySelect: document.querySelector("#groupBySelect"),
  syncNbaButton: document.querySelector("#syncNbaButton"),
  newPlayerButton: document.querySelector("#newPlayerButton"),
  syncStatus: document.querySelector("#syncStatus"),
  playerList: document.querySelector("#playerList"),
  playerRole: document.querySelector("#playerRole"),
  playerName: document.querySelector("#playerName"),
  playerPhoto: document.querySelector("#playerPhoto"),
  photoCaption: document.querySelector("#photoCaption"),
  heroMeta: document.querySelector("#heroMeta"),
  radarCanvas: document.querySelector("#radarCanvas"),
  overallScore: document.querySelector("#overallScore"),
  attributeGrid: document.querySelector("#attributeGrid"),
  metricGrid: document.querySelector("#metricGrid"),
  playerSummary: document.querySelector("#playerSummary"),
  playerForm: document.querySelector("#playerForm"),
  sliderGrid: document.querySelector("#sliderGrid"),
  deleteButton: document.querySelector("#deleteButton"),
  toast: document.querySelector("#toast"),
  inputs: {
    name: document.querySelector("#nameInput"),
    teamName: document.querySelector("#teamNameInput"),
    teamAbbreviation: document.querySelector("#teamAbbreviationInput"),
    jersey: document.querySelector("#jerseyInput"),
    position: document.querySelector("#positionInput"),
    height: document.querySelector("#heightInput"),
    weight: document.querySelector("#weightInput"),
    wingspan: document.querySelector("#wingspanInput"),
    reach: document.querySelector("#reachInput"),
    vertical: document.querySelector("#verticalInput"),
    style: document.querySelector("#styleInput"),
    photoUrl: document.querySelector("#photoUrlInput"),
    notes: document.querySelector("#notesInput")
  }
};

init();

async function init() {
  state.players = await loadPlayers();
  state.selectedId = state.players[0]?.id ?? null;
  buildSliders();
  bindEvents();
  render();
}

async function loadPlayers() {
  try {
    const response = await fetch(API_URL);
    if (response.ok) {
      const players = normalizePlayers(await response.json());
      if (players.length) {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(players));
        return players;
      }
    }
  } catch {
    // Keep local data available if the backend is not running.
  }

  if (Array.isArray(window.NBA_PLAYERS_DATA) && window.NBA_PLAYERS_DATA.length) {
    const players = normalizePlayers(window.NBA_PLAYERS_DATA);
    localStorage.setItem(STORAGE_KEY, JSON.stringify(players));
    return players;
  }

  try {
    const stored = normalizePlayers(JSON.parse(localStorage.getItem(STORAGE_KEY)));
    if (stored.length) return stored;
  } catch {
    localStorage.removeItem(STORAGE_KEY);
  }

  return [];
}

async function savePlayers() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(state.players));
  await fetch(API_URL, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(state.players)
  });
}

function bindEvents() {
  els.searchInput.addEventListener("input", (event) => {
    state.query = event.target.value.trim().toLowerCase();
    renderPlayerList();
  });

  els.teamFilter.addEventListener("change", (event) => {
    state.team = event.target.value;
    selectFirstVisible();
    render();
  });

  els.positionFilter.addEventListener("change", (event) => {
    state.position = event.target.value;
    selectFirstVisible();
    render();
  });

  els.groupBySelect.addEventListener("change", (event) => {
    state.groupBy = event.target.value;
    renderPlayerList();
  });

  els.syncNbaButton.addEventListener("click", async () => {
    if (location.protocol === "file:") {
      if (Array.isArray(window.NBA_PLAYERS_DATA) && window.NBA_PLAYERS_DATA.length) {
        state.players = normalizePlayers(window.NBA_PLAYERS_DATA);
        state.selectedId = state.players[0]?.id ?? null;
        render();
        showToast(`已载入内置名单：${state.players.length} 名球员`);
      } else {
        showToast("请通过本地服务器打开后同步");
      }
      return;
    }

    els.syncNbaButton.disabled = true;
    els.syncStatus.textContent = "正在同步 2025-26 NBA 名单与常规赛数据...";

    try {
      const response = await fetch(`${API_URL}/sync-nba`, { method: "POST" });
      if (!response.ok) throw new Error(await response.text());
      const result = await response.json();
      state.players = await loadPlayers();
      state.selectedId = state.players[0]?.id ?? null;
      render();
      showToast(`已同步 ${result.count} 名球员`);
    } catch {
      showToast("同步失败，请稍后重试");
      els.syncStatus.textContent = `同步失败，当前仍载入 ${state.players.length} 名球员`;
    } finally {
      els.syncNbaButton.disabled = false;
    }
  });

  els.newPlayerButton.addEventListener("click", () => {
    const rookie = {
      id: createId(),
      name: "新球员",
      teamName: "自由球员",
      teamAbbreviation: "FA",
      jersey: "",
      position: "后卫",
      style: "待评估",
      photoUrl: "",
      espnId: "",
      height: 198,
      weight: 95,
      wingspan: 205,
      reach: 260,
      vertical: 85,
      notes: "手动录入球员体测与比赛观察后保存。",
      stats: emptyStats(),
      attributes: { dribble: 70, shooting: 70, iq: 70, personality: 70, body: 70, mental: 70 }
    };
    state.players.unshift(rookie);
    state.selectedId = rookie.id;
    void savePlayers();
    render();
    els.inputs.name.focus();
  });

  els.deleteButton.addEventListener("click", () => {
    if (state.players.length <= 1) {
      showToast("至少保留一名球员");
      return;
    }

    const selected = getSelectedPlayer();
    state.players = state.players.filter((player) => player.id !== selected.id);
    state.selectedId = state.players[0]?.id ?? null;
    void savePlayers();
    render();
    showToast("档案已删除");
  });

  els.playerForm.addEventListener("submit", (event) => {
    event.preventDefault();
    const selected = getSelectedPlayer();
    Object.assign(selected, {
      name: els.inputs.name.value.trim(),
      teamName: els.inputs.teamName.value.trim(),
      teamAbbreviation: els.inputs.teamAbbreviation.value.trim().toUpperCase(),
      jersey: els.inputs.jersey.value.trim(),
      position: els.inputs.position.value,
      height: Number(els.inputs.height.value),
      weight: Number(els.inputs.weight.value),
      wingspan: Number(els.inputs.wingspan.value),
      reach: Number(els.inputs.reach.value),
      vertical: Number(els.inputs.vertical.value),
      style: els.inputs.style.value.trim(),
      photoUrl: els.inputs.photoUrl.value.trim(),
      notes: els.inputs.notes.value.trim()
    });

    attributes.forEach(({ key }) => {
      selected.attributes[key] = Number(document.querySelector(`#${key}Input`).value);
    });

    void savePlayers();
    render();
    showToast("档案已保存");
  });
}

function buildSliders() {
  els.sliderGrid.innerHTML = attributes.map(({ key, label }) => `
    <label class="slider-field">
      <span>${label}</span>
      <input id="${key}Input" name="${key}" type="range" min="0" max="100" step="1">
      <output id="${key}Output">0</output>
    </label>
  `).join("");

  attributes.forEach(({ key }) => {
    const input = document.querySelector(`#${key}Input`);
    const output = document.querySelector(`#${key}Output`);
    input.addEventListener("input", () => {
      output.value = input.value;
      const selected = getSelectedPlayer();
      selected.attributes[key] = Number(input.value);
      renderAnalysis(selected);
    });
  });
}

function render() {
  if (!state.players.length) {
    els.syncStatus.textContent = "暂无球员数据，请点击同步NBA";
    return;
  }

  renderFilters();
  renderPlayerList();
  const selected = getSelectedPlayer();
  renderForm(selected);
  renderAnalysis(selected);
}

function renderFilters() {
  const teams = uniqueSorted(state.players.map((player) => player.teamAbbreviation).filter(Boolean));
  const positions = uniqueSorted(state.players.map((player) => player.position).filter(Boolean));

  els.teamFilter.innerHTML = `<option value="all">全部球队</option>${teams.map((team) =>
    `<option value="${team}">${team}</option>`).join("")}`;
  els.positionFilter.innerHTML = `<option value="all">全部位置</option>${positions.map((position) =>
    `<option value="${position}">${position}</option>`).join("")}`;
  els.teamFilter.value = teams.includes(state.team) ? state.team : "all";
  els.positionFilter.value = positions.includes(state.position) ? state.position : "all";
  els.groupBySelect.value = state.groupBy;
}

function renderPlayerList() {
  const filtered = getFilteredPlayers();
  els.syncStatus.textContent = `当前载入 ${state.players.length} 名球员，筛选显示 ${filtered.length} 名`;

  const groups = groupPlayers(filtered, state.groupBy);
  els.playerList.innerHTML = groups.map(([group, players]) => `
    <section class="player-group">
      <h2>${group}<span>${players.length}</span></h2>
      ${players.map(renderPlayerRow).join("")}
    </section>
  `).join("") || `<p class="empty-state">没有匹配的球员</p>`;

  els.playerList.querySelectorAll(".player-row").forEach((row) => {
    row.addEventListener("click", () => {
      state.selectedId = row.dataset.id;
      render();
    });
  });
}

function renderPlayerRow(player) {
  const score = getOverall(player);
  const active = player.id === state.selectedId ? "is-active" : "";
  return `
    <button class="player-row ${active}" type="button" data-id="${player.id}">
      <img src="${getPhotoUrl(player)}" alt="">
      <span>
        <strong>${player.name}</strong>
        <small>${player.teamAbbreviation} · ${player.position} · ${player.style}</small>
      </span>
      <b>${score}</b>
    </button>
  `;
}

function renderForm(player) {
  Object.entries(els.inputs).forEach(([key, input]) => {
    input.value = player[key] ?? "";
  });

  attributes.forEach(({ key }) => {
    const value = player.attributes[key];
    document.querySelector(`#${key}Input`).value = value;
    document.querySelector(`#${key}Output`).value = value;
  });
}

function renderAnalysis(player) {
  const stats = player.stats ?? emptyStats();
  els.playerRole.textContent = `${player.teamAbbreviation} · ${player.position} · ${player.style}`;
  els.playerName.textContent = player.name;
  els.playerPhoto.src = getPhotoUrl(player);
  els.playerPhoto.alt = `${player.name} 定妆照`;
  els.photoCaption.textContent = player.jersey ? `${player.teamAbbreviation} #${player.jersey}` : player.teamAbbreviation;
  els.heroMeta.innerHTML = `
    <span>${player.height} cm</span>
    <span>${player.weight} kg</span>
    <span>${player.wingspan} cm 臂展</span>
  `;

  els.overallScore.value = getOverall(player);
  els.attributeGrid.innerHTML = attributes.map(({ key, label, color }) => `
    <div class="attribute-card" style="--accent:${color}">
      <span>${label}</span>
      <strong>${player.attributes[key]}</strong>
      <div><i style="width:${player.attributes[key]}%"></i></div>
    </div>
  `).join("");

  const bmi = player.weight / ((player.height / 100) ** 2);
  const reachPlus = player.wingspan - player.height;
  els.metricGrid.innerHTML = `
    <div><dt>球队</dt><dd>${player.teamName}</dd></div>
    <div><dt>位置</dt><dd>${player.position}</dd></div>
    <div><dt>身高</dt><dd>${player.height} cm</dd></div>
    <div><dt>体重</dt><dd>${player.weight} kg</dd></div>
    <div><dt>臂展差</dt><dd>${reachPlus > 0 ? "+" : ""}${reachPlus} cm</dd></div>
    <div><dt>BMI</dt><dd>${bmi.toFixed(1)}</dd></div>
    <div><dt>场均得分</dt><dd>${stats.points.toFixed(1)}</dd></div>
    <div><dt>篮板 / 助攻</dt><dd>${stats.rebounds.toFixed(1)} / ${stats.assists.toFixed(1)}</dd></div>
  `;

  els.playerSummary.textContent = buildSummary(player);
  drawRadar(player);
}

function drawRadar(player) {
  const canvas = els.radarCanvas;
  const ctx = canvas.getContext("2d");
  const size = canvas.width;
  const center = size / 2;
  const radius = size * 0.36;
  const points = attributes.map((attribute, index) => {
    const angle = -Math.PI / 2 + index * (Math.PI * 2 / attributes.length);
    return { ...attribute, angle, value: player.attributes[attribute.key] };
  });

  ctx.clearRect(0, 0, size, size);
  ctx.lineWidth = 2;
  ctx.font = "600 20px Microsoft YaHei, Segoe UI, sans-serif";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";

  for (let level = 1; level <= 5; level += 1) {
    const levelRadius = radius * level / 5;
    drawPolygon(ctx, points.map((point) => ({
      x: center + Math.cos(point.angle) * levelRadius,
      y: center + Math.sin(point.angle) * levelRadius
    })));
    ctx.strokeStyle = level === 5 ? "rgba(15, 23, 42, .25)" : "rgba(148, 163, 184, .28)";
    ctx.stroke();
  }

  points.forEach((point) => {
    ctx.beginPath();
    ctx.moveTo(center, center);
    ctx.lineTo(center + Math.cos(point.angle) * radius, center + Math.sin(point.angle) * radius);
    ctx.strokeStyle = "rgba(148, 163, 184, .24)";
    ctx.stroke();
  });

  const valuePoints = points.map((point) => ({
    x: center + Math.cos(point.angle) * radius * point.value / 100,
    y: center + Math.sin(point.angle) * radius * point.value / 100
  }));

  const gradient = ctx.createLinearGradient(110, 70, 420, 450);
  gradient.addColorStop(0, "rgba(37, 99, 235, .72)");
  gradient.addColorStop(.55, "rgba(220, 38, 38, .50)");
  gradient.addColorStop(1, "rgba(245, 158, 11, .54)");
  drawPolygon(ctx, valuePoints);
  ctx.fillStyle = gradient;
  ctx.fill();
  ctx.strokeStyle = "#111827";
  ctx.lineWidth = 3;
  ctx.stroke();

  valuePoints.forEach((point) => {
    ctx.beginPath();
    ctx.arc(point.x, point.y, 6, 0, Math.PI * 2);
    ctx.fillStyle = "#ffffff";
    ctx.fill();
    ctx.strokeStyle = "#111827";
    ctx.lineWidth = 2;
    ctx.stroke();
  });

  points.forEach((point) => {
    const labelRadius = radius + 44;
    const x = center + Math.cos(point.angle) * labelRadius;
    const y = center + Math.sin(point.angle) * labelRadius;
    ctx.fillStyle = "#334155";
    ctx.fillText(point.label, x, y);
  });
}

function drawPolygon(ctx, points) {
  ctx.beginPath();
  points.forEach((point, index) => {
    if (index === 0) ctx.moveTo(point.x, point.y);
    else ctx.lineTo(point.x, point.y);
  });
  ctx.closePath();
}

function getFilteredPlayers() {
  return state.players.filter((player) => {
    const content = `${player.name} ${player.teamName} ${player.teamAbbreviation} ${player.position} ${player.style}`.toLowerCase();
    return content.includes(state.query)
      && (state.team === "all" || player.teamAbbreviation === state.team)
      && (state.position === "all" || player.position === state.position);
  });
}

function groupPlayers(players, groupBy) {
  const map = new Map();
  players.forEach((player) => {
    const key = groupBy === "position" ? player.position : `${player.teamAbbreviation} · ${player.teamName}`;
    if (!map.has(key)) map.set(key, []);
    map.get(key).push(player);
  });

  return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
}

function selectFirstVisible() {
  const first = getFilteredPlayers()[0];
  if (first) state.selectedId = first.id;
}

function getSelectedPlayer() {
  return state.players.find((player) => player.id === state.selectedId) ?? state.players[0];
}

function getOverall(player) {
  const values = Object.values(player.attributes);
  return Math.round(values.reduce((sum, value) => sum + value, 0) / values.length);
}

function buildSummary(player) {
  const ranked = attributes
    .map((attribute) => ({ ...attribute, value: player.attributes[attribute.key] }))
    .sort((a, b) => b.value - a.value);
  const stats = player.stats ?? emptyStats();

  return `${player.name} 的六边形优势集中在${ranked[0].label}和${ranked[1].label}。当前赛季场均 ${stats.points.toFixed(1)} 分、${stats.rebounds.toFixed(1)} 篮板、${stats.assists.toFixed(1)} 助攻；${player.notes}`;
}

function normalizePlayers(players) {
  if (!Array.isArray(players)) return [];

  return players.map((player) => ({
    ...player,
    teamName: player.teamName || "自由球员",
    teamAbbreviation: player.teamAbbreviation || "FA",
    jersey: player.jersey ?? "",
    espnId: player.espnId ?? "",
    photoUrl: player.photoUrl ?? "",
    stats: { ...emptyStats(), ...(player.stats ?? {}) },
    attributes: {
      dribble: 70,
      shooting: 70,
      iq: 70,
      personality: 70,
      body: 70,
      mental: 70,
      ...(player.attributes ?? {})
    }
  }));
}

function getPhotoUrl(player) {
  return player.photoUrl || "assets/ai-player-shoot.png";
}

function uniqueSorted(values) {
  return [...new Set(values)].sort((a, b) => a.localeCompare(b));
}

function emptyStats() {
  return { points: 0, rebounds: 0, assists: 0, steals: 0, blocks: 0, threeMade: 0, threePct: 0, minutes: 0, games: 0 };
}

function createId() {
  return globalThis.crypto?.randomUUID?.() ?? `player-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function showToast(message) {
  els.toast.textContent = message;
  els.toast.classList.add("is-visible");
  window.clearTimeout(showToast.timer);
  showToast.timer = window.setTimeout(() => {
    els.toast.classList.remove("is-visible");
  }, 1800);
}
