export function createDraftGuard(root) {
  const document = root.ownerDocument;
  const window = document.defaultView;
  const baselines = new Map();
  const snapshot = form => JSON.stringify([...new window.FormData(form)]);

  function hasChanges(except) {
    if (root.closest("[hidden]")) return false;
    return [...baselines].some(([form, before]) =>
      form !== except && form.isConnected && snapshot(form) !== before);
  }

  function watch(form) { baselines.set(form, snapshot(form)); }
  function reset(...forms) {
    baselines.clear();
    forms.forEach(watch);
  }
  function confirmDiscard(except) {
    return !hasChanges(except) || window.confirm("Есть несохранённые изменения. Отменить их и продолжить?");
  }

  window.addEventListener("beforeunload", event => {
    if (!hasChanges()) return;
    event.preventDefault();
    event.returnValue = "";
  });
  document.addEventListener("workspace:beforeleave", event => {
    if (!confirmDiscard()) event.preventDefault();
  });
  document.addEventListener("click", event => {
    const link = event.target.closest?.("a[href]");
    if (!link || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey ||
        event.shiftKey || event.altKey || link.hasAttribute("download") ||
        (link.target && link.target !== "_self")) return;
    const target = new URL(link.href);
    const current = new URL(window.location.href);
    if (target.origin === current.origin && target.pathname === current.pathname && target.search === current.search) return;
    if (!confirmDiscard()) event.preventDefault();
    else reset();
  });
  return { watch, reset, hasChanges, confirmDiscard };
}
