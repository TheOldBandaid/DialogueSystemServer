import { api, byId, run } from "./workspace.js";

export function initializeCleanup(path, cleaned = () => {}) {
  const panel = byId("cleanup-panel");
  const form = byId("cleanup-form");
  const apply = byId("cleanup-apply");
  const status = byId("cleanup-status");
  const scope = byId("cleanup-all-users");
  let calculation;
  let generation = 0;

  function invalidate() {
    generation++;
    calculation = null;
    apply.disabled = true;
    status.textContent = "Рассчитайте состав очистки перед удалением.";
  }

  function reset() {
    form.reset();
    const date = new Date(Date.now() - 30 * 86400000);
    form.elements.before.value = new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    scope.hidden = true;
    invalidate();
  }

  panel.ontoggle = run(async () => {
    if (!panel.open) return;
    const profile = await api("/api/accounts/me");
    scope.hidden = profile.role !== "Administrator";
    if (scope.hidden && form.elements.allUsers.checked) {
      form.elements.allUsers.checked = false;
      invalidate();
    }
  });
  form.oninput = invalidate;
  form.onchange = invalidate;
  form.onsubmit = run(async () => {
    invalidate();
    const request = generation;
    const route = path();
    const criteria = {
      beforeUnixMilliseconds: new Date(form.elements.before.value).getTime(),
      keepLatest: Number(form.elements.keepLatest.value),
      previews: form.elements.previews.checked,
      revisions: form.elements.revisions.checked,
      allUsers: !scope.hidden && form.elements.allUsers.checked
    };
    const plan = await api(`${route}/cleanup/plan`, "POST", criteria);
    if (request !== generation || route !== path()) return;
    calculation = { criteria, confirmationToken: plan.confirmationToken };
    status.textContent = `Будет удалено прохождений: ${plan.previewCount}, шагов истории: ${plan.historyStepCount}, версий: ${plan.revisionCount}.` +
      (plan.hasMore ? " Это первая порция; после удаления можно рассчитать следующую." : "");
    apply.disabled = plan.previewCount + plan.revisionCount === 0;
  });
  apply.onclick = run(async () => {
    if (!calculation || apply.disabled) return;
    if (!window.confirm(`${status.textContent}\nУдалить эти данные после создания резервной копии?`)) return;
    const input = calculation;
    const route = path();
    invalidate();
    let result;
    try { result = await api(`${route}/cleanup`, "POST", input); }
    catch (error) {
      status.textContent = "Очистка не подтверждена. Рассчитайте её заново перед повторной попыткой.";
      throw error;
    }
    status.textContent = `Удалено прохождений: ${result.previewCount}, шагов истории: ${result.historyStepCount}, версий: ${result.revisionCount}. Резервная копия создана.` +
      (result.hasMore ? " Рассчитайте следующую порцию для продолжения." : "");
    await cleaned();
  });
  reset();
  return { reset };
}
