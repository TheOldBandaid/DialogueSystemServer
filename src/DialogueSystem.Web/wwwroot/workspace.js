export const byId = id => document.getElementById(id);
const login = byId("login");
const workspace = byId("workspace");
const accountAdmin = byId("account-admin");
const newAccountForm = byId("new-account");
const passwordForm = byId("change-password");
const roleLabels = { Editor: "Редактор", Administrator: "Администратор" };
let token = "";
let pendingOperations = 0;

export async function api(path, method = "GET", body) {
  const response = await fetch(path, {
    method,
    headers: method === "GET" ? {} : {
      "X-CSRF-TOKEN": token,
      "Content-Type": "application/json"
    },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const result = response.headers.get("content-type")?.includes("json") ? await response.json() : null;
  if (!response.ok) {
    const validation = result?.errors;
    const details = validation && Object.values(validation).flat().join(" ");
    const message = response.status === 401 ? "Проверьте имя пользователя и пароль или войдите заново." :
      result?.detail || details || `Запрос отклонён (HTTP ${response.status}).`;
    throw Object.assign(new Error(message), { status: response.status, result });
  }
  return result;
}

export function run(operation) {
  return async event => {
    event?.preventDefault();
    pendingOperations++;
    workspace.inert = true;
    login.inert = true;
    byId("logout").disabled = true;
    byId("status").textContent = "";
    try {
      await operation(event);
    } catch (error) {
      if (error.status === 401) showLogin();
      byId("status").textContent = error.message;
    } finally {
      pendingOperations--;
      workspace.inert = pendingOperations > 0;
      login.inert = pendingOperations > 0;
      byId("logout").disabled = pendingOperations > 0;
    }
  };
}

async function refreshToken() {
  token = (await api("/api/accounts/csrf")).token;
}

function showLogin() {
  showAccountManagement(null);
  passwordForm.reset();
  byId("my-account").open = false;
  workspace.hidden = true;
  byId("logout").hidden = true;
  login.hidden = false;
}

function showAccountManagement(profile) {
  accountAdmin.hidden = profile?.role !== "Administrator";
  if (accountAdmin.hidden) {
    accountAdmin.open = false;
    newAccountForm.reset();
    byId("accounts-list").replaceChildren();
  }
}

async function accountRequest(route, method, body) {
  try {
    return await api(route, method, body);
  } catch (error) {
    if (error.status !== 403) throw error;
    showAccountManagement(null);
    throw new Error("Для управления ролями нужны права администратора.");
  }
}

function accountRow(account) {
  const row = document.createElement("li");
  const form = document.createElement("form");
  form.className = "actions account-role";
  const label = document.createElement("label");
  const loginName = document.createElement("span");
  loginName.textContent = account.login;
  const role = document.createElement("select");
  role.setAttribute("aria-label", `Роль для ${account.login}`);
  role.append(...Object.entries(roleLabels).map(([value, title]) => new Option(title, value)));
  role.value = account.role;
  label.append(loginName, role);
  const save = document.createElement("button");
  save.textContent = "Сохранить роль";
  save.disabled = true;
  role.onchange = () => { save.disabled = role.value === account.role; };

  form.onsubmit = run(async () => {
    await accountRequest(`/api/accounts/${account.id}/role`, "PUT", { role: role.value });
    showAccountManagement(await api("/api/accounts/me"));
    if (!accountAdmin.hidden) await loadAccounts();
    byId("status").textContent = `Роль пользователя «${account.login}» обновлена.`;
  });
  form.append(label, save);
  row.append(form);
  return row;
}

async function loadAccounts() {
  const accounts = await accountRequest("/api/accounts");
  byId("accounts-list").replaceChildren(...accounts.map(accountRow));
}

const refreshAccounts = run(loadAccounts);
accountAdmin.ontoggle = () => {
  if (accountAdmin.open && !accountAdmin.hidden) refreshAccounts();
};
byId("refresh-accounts").onclick = refreshAccounts;
newAccountForm.onsubmit = run(async () => {
  const input = Object.fromEntries(new FormData(newAccountForm));
  const account = await accountRequest("/api/accounts", "POST", input);
  newAccountForm.reset();
  await loadAccounts();
  byId("status").textContent = `Пользователь «${account.login}» создан.`;
});
passwordForm.onsubmit = run(async () => {
  const { currentPassword, newPassword, confirmation } = Object.fromEntries(new FormData(passwordForm));
  if (newPassword !== confirmation) throw new Error("Новые пароли не совпадают.");
  if (!document.dispatchEvent(new Event("workspace:beforeleave", { cancelable: true }))) return;

  await api("/api/accounts/me/password", "PUT", { currentPassword, newPassword });
  showLogin();
  byId("status").textContent = "Пароль изменён. Войдите с новым паролем.";
});

export const dialogueUrl = id => `/dialogues/${id}`;

export function downloadJson(data, name) {
  const file = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(file);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

export async function initializeWorkspace(loadPage) {
  async function showWorkspace(profile) {
    showAccountManagement(profile);
    await loadPage();
    login.hidden = true;
    workspace.hidden = false;
    byId("logout").hidden = false;
  }

  login.onsubmit = run(async () => {
    await refreshToken();
    const profile = await api("/api/accounts/login", "POST", Object.fromEntries(new FormData(login)));
    login.reset();
    await refreshToken();
    await showWorkspace(profile);
  });
  byId("logout").onclick = run(async () => {
    if (!document.dispatchEvent(new Event("workspace:beforeleave", { cancelable: true }))) return;
    await api("/api/accounts/logout", "POST");
    showLogin();
  });

  window.addEventListener("pageshow", event => {
    if (event.persisted) location.reload();
  });

  await run(async () => {
    let profile;
    try {
      profile = await api("/api/accounts/me");
    } catch (error) {
      if (error.status !== 401) throw error;
      showLogin();
      return;
    }
    await refreshToken();
    await showWorkspace(profile);
  })();
}
