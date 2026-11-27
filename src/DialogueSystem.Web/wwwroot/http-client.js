let token = "";

export async function api(path, method = "GET", body) {
  const headers = method === "GET" ? {} : { "X-CSRF-TOKEN": token, "Content-Type": "application/json" };
  if (method === "DELETE" && body?.version) {
    headers["If-Match"] = `"${body.version}"`;
    body = undefined;
  }
  const response = await fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const result = response.headers.get("content-type")?.includes("json") ? await response.json() : null;
  if (!response.ok) {
    const validation = result?.errors;
    const details = validation && Object.values(validation).flat().join(" ");
    const message = response.status === 413 ? "Сервер отклонил запрос из-за ограничения размера данных." :
      response.status === 401 ? "Проверьте имя пользователя и пароль или войдите заново." :
      result?.detail || details || `Запрос отклонён (HTTP ${response.status}).`;
    throw Object.assign(new Error(message), { status: response.status, result });
  }
  return result;
}

export async function refreshToken() {
  token = (await api("/api/accounts/csrf")).token;
}
