import { api, refreshToken } from "./http-client.js";
import { byId, run, setUnauthorizedHandler } from "./ui.js";
import { initializeAccountAdministration } from "./account-admin.js";

export async function initializeWorkspace(loadPage) {
  const login = byId("login");
  const workspace = byId("workspace");
  const passwordForm = byId("change-password");
  const administration = initializeAccountAdministration();
  setUnauthorizedHandler(showLogin);

  function showLogin() {
    administration.show(null);
    passwordForm.reset();
    byId("my-account").open = false;
    workspace.hidden = true;
    byId("logout").hidden = true;
    login.hidden = false;
  }

  passwordForm.onsubmit = run(async () => {
    const { currentPassword, newPassword, confirmation } = Object.fromEntries(new FormData(passwordForm));
    if (newPassword !== confirmation) throw new Error("Новые пароли не совпадают.");
    if (!document.dispatchEvent(new Event("workspace:beforeleave", { cancelable: true }))) return;

    await api("/api/accounts/me/password", "PUT", { currentPassword, newPassword });
    showLogin();
    byId("status").textContent = "Пароль изменён. Войдите с новым паролем.";
  });


  async function showWorkspace(profile) {
    const mustChange = profile.mustChangePassword === true;
    administration.show(mustChange ? null : profile);
    byId("page-content").hidden = mustChange;
    if (mustChange) {
      byId("my-account").open = true;
      byId("status").textContent = "Перед работой смените временный пароль в разделе «Моя учётная запись».";
    } else {
      await loadPage();
    }
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
