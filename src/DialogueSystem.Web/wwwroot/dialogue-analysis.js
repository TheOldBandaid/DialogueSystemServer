import { api, byId, dialogueUrl, initializeWorkspace, run } from "./workspace.js";

const host = byId("analysis");
const id = Number(host.dataset.dialogueId);
const endpoint = `/api/dialogues/${id}/analysis`;
const routePanel = byId("ending-route");
const reasons = {
  "source-unreachable": "До исходной реплики нет маршрута",
  "terminal-source": "Прохождение завершается на исходной реплике",
  "target-missing": "Целевая реплика отсутствует",
  "required-state-unreachable": "У исходной реплики недоступно требуемое состояние"
};

function lineLink(lineId, text = `Реплика №${lineId}`) {
  const link = document.createElement("a");
  link.href = `${dialogueUrl(id)}?line=${lineId}`;
  link.textContent = text;
  return link;
}

function setRows(target, entries, createRow, emptyText) {
  const rows = entries.map(createRow);
  if (rows.length === 0) {
    const item = document.createElement("li");
    item.textContent = emptyText;
    rows.push(item);
  }
  byId(target).replaceChildren(...rows);
}

function paragraph(text) {
  const result = document.createElement("p");
  result.textContent = text;
  return result;
}

function endingRow(ending) {
  const item = document.createElement("li");
  const line = ending.line;
  item.append(lineLink(line.id, `№${line.id} · ${line.speaker}`), paragraph(line.text));
  if (ending.reachable === true) {
    const viewPath = document.createElement("button");
    viewPath.type = "button";
    viewPath.textContent = `Кратчайший маршрут · шагов ${ending.steps}`;
    viewPath.onclick = run(() => loadRoute(ending.line.id));
    item.append(viewPath);
  } else {
    item.append(paragraph(ending.reachable === false ? "Недостижима" : "Достижимость пока не определена"));
  }
  return item;
}

function unavailableRow(choice) {
  const item = document.createElement("li");
  item.append(lineLink(choice.sourceLineId), " → ", lineLink(choice.targetLineId));
  item.append(paragraph(`Ответ ${choice.position} · ${choice.text}`));
  const details = reasons[choice.reason];
  item.append(paragraph(choice.requiredState == null ? details : `${details}. Требуется «${choice.requiredState}».`));
  return item;
}

function trapsRow(trap) {
  const item = document.createElement("li");
  item.append(lineLink(trap.lineId), ` · состояние ${stateLabel(trap.storyState)}`);
  return item;
}

function stateLabel(state) {
  return state == null ? "пустое" : `«${state}»`;
}

function metric(label, value) {
  const group = document.createElement("div");
  const name = document.createElement("dt");
  const number = document.createElement("dd");
  name.textContent = label;
  number.textContent = value;
  group.append(name, number);
  return group;
}

function renderProblems(report) {
  byId("analysis-problems").hidden = !report.complete;
  if (!report.complete) return;

  renderSection("analysis-unreachable", "Недостижимые реплики", report.unreachableLineCount,
    report.unreachableLines, line => {
      const item = document.createElement("li");
      item.append(lineLink(line.id, `№${line.id} · ${line.speaker}`), paragraph(line.text));
      return item;
    });
  renderSection("analysis-unavailable", "Недоступные переходы", report.unavailableChoiceCount,
    report.unavailableChoices, unavailableRow);
  renderSection("analysis-trapped", "Состояния без пути к концовке", report.trappedPositionCount,
    report.trappedPositions, trapsRow);
}

function renderSection(id, title, count, entries, createRow) {
  const shown = count > entries.length ? `${entries.length} из ${count}` : count;
  byId(`${id}-title`).textContent = `${title} · ${shown}`;
  setRows(id, entries, createRow, "Таких записей нет.");
}

function renderReport(report) {
  byId("analysis-heading").textContent = report.title;
  document.title = `${report.title} — Маршруты диалога`;
  byId("analysis-metrics").replaceChildren(
    metric("Достигнуто реплик", `${report.reachableLineCount} из ${report.lineCount}`),
    metric("Доступно переходов", `${report.reachableChoiceCount} из ${report.choiceCount}`),
    metric("Пар «реплика и состояние»", report.visitedPositionCount),
    metric("Минимальный уровень", report.minimumPlayerLevel ?? "Любой")
  );
  const notice = byId("analysis-notice");
  notice.textContent = !report.startLineId ? "Задайте стартовую реплику в редакторе, чтобы проверить маршруты." :
    !report.complete ? "Достигнут предел обхода. Показаны найденные маршруты и нижние оценки доступности." : "";
  notice.hidden = notice.textContent === "";
  setRows("analysis-endings", report.endings, endingRow, "В диалоге пока нет концовок.");
  renderProblems(report);
  routePanel.hidden = true;
  host.hidden = false;
}

async function loadReport() {
  try {
    renderReport(await api(endpoint));
  } catch (error) {
    if (error.status !== 404) throw error;
    host.hidden = true;
    byId("status").textContent = "Диалог не найден. Вернуться к списку можно через каталог.";
  }
}

function routeStep(step) {
  const item = document.createElement("li");
  const transition = document.createElement("div");
  transition.append(lineLink(step.sourceLineId), " → ", lineLink(step.targetLineId));
  item.append(transition, paragraph(step.answer));
  const details = [];
  if (step.requiredState != null) details.push(`Требуется ${stateLabel(step.requiredState)}`);
  if (step.stateBefore !== step.stateAfter) {
    details.push(`Состояние ${stateLabel(step.stateBefore)} → ${stateLabel(step.stateAfter)}`);
  }
  if (details.length) item.append(paragraph(details.join(" · ")));
  return item;
}

async function loadRoute(endingId) {
  const result = await api(`${endpoint}/endings/${endingId}`);
  byId("ending-route-title").textContent = `Маршрут к реплике №${endingId}`;
  const route = result.route;
  const message = byId("ending-route-message");
  if (route) {
    message.textContent = route.steps.length === 0 ? "Стартовая реплика уже является концовкой." :
      `Состояние в конце — ${stateLabel(route.finalState)}.`;
  } else {
    message.textContent = result.complete ? "После изменений маршрут больше недоступен. Обновите отчёт." :
      "Маршрут не найден в пределах обхода.";
  }
  byId("ending-route-steps").replaceChildren(...(route?.steps.map(routeStep) ?? []));
  routePanel.hidden = false;
  requestAnimationFrame(() => routePanel.focus());
}

byId("refresh-analysis").onclick = run(loadReport);
await initializeWorkspace(loadReport);
