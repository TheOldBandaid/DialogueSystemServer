export const byId = id => document.getElementById(id);
let pendingOperations = 0;
let onUnauthorized;

export function setUnauthorizedHandler(handler) { onUnauthorized = handler; }

function setBusy(busy) {
  for (const id of ["workspace", "login"]) {
    const element = byId(id);
    if (element) element.inert = busy;
  }
  const logout = byId("logout");
  if (logout) logout.disabled = busy;
}

export function run(operation) {
  return async event => {
    event?.preventDefault();
    pendingOperations++;
    setBusy(true);
    const status = byId("status");
    if (status) status.textContent = "";
    try {
      await operation(event);
    } catch (error) {
      if (error.status === 401) onUnauthorized?.();
      if (status) status.textContent = error.message;
    } finally {
      pendingOperations--;
      setBusy(pendingOperations > 0);
    }
  };
}

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
