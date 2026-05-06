const state = {
  authenticated: false,
  captchaToken: "",
  page: 1,
  pageSize: 12
};

const $ = (selector) => document.querySelector(selector);

const elements = {
  loginPanel: $("#loginPanel"),
  submitPanel: $("#submitPanel"),
  logoutButton: $("#logoutButton"),
  loginForm: $("#loginForm"),
  submitForm: $("#submitForm"),
  filterForm: $("#filterForm"),
  loginMessage: $("#loginMessage"),
  captchaQuestion: $("#captchaQuestion"),
  refreshCaptcha: $("#refreshCaptcha"),
  libraryList: $("#libraryList"),
  reloadButton: $("#reloadButton"),
  previewButton: $("#previewButton"),
  scoreBox: $("#scoreBox"),
  scoreValue: $("#scoreValue"),
  scoreFeedback: $("#scoreFeedback"),
  scoreBreakdown: $("#scoreBreakdown"),
  previousPage: $("#previousPage"),
  nextPage: $("#nextPage"),
  pageLabel: $("#pageLabel"),
  statTotal: $("#statTotal"),
  statAverage: $("#statAverage"),
  statBest: $("#statBest"),
  statPass: $("#statPass")
};

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: "same-origin",
    headers: { "Content-Type": "application/json", ...options.headers },
    ...options
  });

  const text = await response.text();
  const data = text ? JSON.parse(text) : {};
  if (!response.ok) {
    throw new Error(data.message || `Request failed with status ${response.status}.`);
  }

  return data;
}

async function loadCaptcha() {
  const data = await api("/api/captcha");
  elements.captchaQuestion.textContent = data.question;
  state.captchaToken = data.token;
}

async function loadSession() {
  const data = await api("/api/me");
  setAuthenticated(data.authenticated);
}

function setAuthenticated(isAuthenticated) {
  state.authenticated = isAuthenticated;
  elements.loginPanel.hidden = isAuthenticated;
  elements.submitPanel.hidden = !isAuthenticated;
  elements.logoutButton.hidden = !isAuthenticated;
}

async function loadStats() {
  const stats = await api("/api/stats");
  elements.statTotal.textContent = stats.total;
  elements.statAverage.textContent = stats.averageScore;
  elements.statBest.textContent = stats.highestScore;
  elements.statPass.textContent = stats.passingScore;
}

async function loadSubmissions() {
  const params = new URLSearchParams({
    page: state.page,
    pageSize: state.pageSize
  });

  const query = $("#query").value.trim();
  const tag = $("#tagFilter").value.trim();
  const minScore = $("#minScore").value;
  if (query) params.set("q", query);
  if (tag) params.set("tag", tag);
  if (minScore) params.set("minScore", minScore);

  const data = await api(`/api/submissions?${params}`);
  elements.pageLabel.textContent = `Page ${data.page}`;
  elements.previousPage.disabled = data.page <= 1;
  elements.nextPage.disabled = data.items.length < data.pageSize;

  if (data.items.length === 0) {
    elements.libraryList.innerHTML = '<div class="empty">No shared conversations yet.</div>';
    return;
  }

  elements.libraryList.innerHTML = data.items.map(renderSubmission).join("");
}

function renderSubmission(item) {
  const author = item.author ? ` by ${escapeHtml(item.author)}` : "";
  const tags = item.tags
    ? `<div class="tags">${item.tags.split(",").map((tag) => `<span>${escapeHtml(tag.trim())}</span>`).join("")}</div>`
    : "";
  const deleteButton = state.authenticated
    ? `<button class="text-button danger" data-delete="${item.id}" type="button">Delete</button>`
    : "";

  return `
    <article class="item">
      <div class="item-header">
        <div>
          <h3>${escapeHtml(item.title)}</h3>
          <div class="meta">${escapeHtml(item.createdAt)}${author}</div>
        </div>
        <div class="badge">${item.score}</div>
      </div>
      ${tags}
      <div class="conversation">${escapeHtml(item.conversation)}</div>
      <p class="feedback">${escapeHtml(item.feedback)}</p>
      <div class="item-actions">
        <button class="text-button" data-report="${item.id}" type="button">Report</button>
        <span>${item.reportCount} reports</span>
        ${deleteButton}
      </div>
    </article>
  `;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function getSubmissionPayload() {
  return {
    title: $("#title").value,
    author: $("#author").value,
    tags: $("#tags").value,
    conversation: $("#conversation").value
  };
}

function showScore(result) {
  elements.scoreValue.textContent = result.score;
  elements.scoreFeedback.textContent = `${result.message ? `${result.message} ` : ""}${result.feedback}`;
  elements.scoreBreakdown.innerHTML = result.breakdown
    .map((item) => `<li><strong>${escapeHtml(item.name)}</strong> ${item.points}/${item.maxPoints} - ${escapeHtml(item.note)}</li>`)
    .join("");
  elements.scoreBox.hidden = false;
}

elements.loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  elements.loginMessage.textContent = "";

  try {
    await api("/api/login", {
      method: "POST",
      body: JSON.stringify({
        password: $("#password").value,
        captchaAnswer: $("#captchaAnswer").value,
        captchaToken: state.captchaToken
      })
    });
    setAuthenticated(true);
    elements.loginForm.reset();
    await loadCaptcha();
    await loadSubmissions();
  } catch (error) {
    elements.loginMessage.textContent = error.message;
    await loadCaptcha();
  }
});

elements.previewButton.addEventListener("click", async () => {
  try {
    const result = await api("/api/score", {
      method: "POST",
      body: JSON.stringify(getSubmissionPayload())
    });
    showScore(result);
  } catch (error) {
    showScore({ score: "!", feedback: error.message, breakdown: [] });
  }
});

elements.submitForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  elements.scoreBox.hidden = true;

  try {
    const result = await api("/api/submissions", {
      method: "POST",
      body: JSON.stringify(getSubmissionPayload())
    });
    showScore(result);

    if (result.saved) {
      elements.submitForm.reset();
      state.page = 1;
      await Promise.all([loadStats(), loadSubmissions()]);
    }
  } catch (error) {
    showScore({ score: "!", feedback: error.message, breakdown: [] });
  }
});

elements.logoutButton.addEventListener("click", async () => {
  await api("/api/logout", { method: "POST" });
  setAuthenticated(false);
  await Promise.all([loadCaptcha(), loadSubmissions()]);
});

elements.filterForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  state.page = 1;
  await loadSubmissions();
});

elements.reloadButton.addEventListener("click", async () => {
  await Promise.all([loadStats(), loadSubmissions()]);
});

elements.previousPage.addEventListener("click", async () => {
  state.page = Math.max(1, state.page - 1);
  await loadSubmissions();
});

elements.nextPage.addEventListener("click", async () => {
  state.page += 1;
  await loadSubmissions();
});

elements.refreshCaptcha.addEventListener("click", loadCaptcha);

elements.libraryList.addEventListener("click", async (event) => {
  const reportId = event.target.dataset.report;
  const deleteId = event.target.dataset.delete;

  if (reportId) {
    await api(`/api/submissions/${reportId}/report`, { method: "POST" });
    await Promise.all([loadStats(), loadSubmissions()]);
  }

  if (deleteId && confirm("Delete this submission?")) {
    await api(`/api/submissions/${deleteId}`, { method: "DELETE" });
    await Promise.all([loadStats(), loadSubmissions()]);
  }
});

Promise.all([loadCaptcha(), loadSession(), loadStats(), loadSubmissions()]);
