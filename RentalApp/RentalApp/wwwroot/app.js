// состояние приложения
const S = { user: null, cats: [], tariffs: [] };

// элементы и помощники
const view = document.getElementById('view');
const $ = (s, r = document) => r.querySelector(s);

function esc(v) {
  if (v == null) return '';
  return String(v).replace(/[&<>"']/g, function(c) {
    if (c === '&') return '&amp;';
    if (c === '<') return '&lt;';
    if (c === '>') return '&gt;';
    if (c === '"') return '&quot;';
    return '&#39;';
  });
}

function money(n) {
  const val = Number(n || 0);
  return val.toLocaleString('ru-RU', { maximumFractionDigits: 2 }) + ' ₽';
}

function fmtDate(s) {
  if (!s) return '';
  const parts = s.split('-');
  return parts.reverse().join('.');
}

function pad(n) {
  return String(n).padStart(2, '0');
}

function toStr(d) {
  return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate());
}

function todayStr() {
  return toStr(new Date());
}

function addDays(s, n) {
  const parts = s.split('-').map(Number);
  const newDate = new Date(parts[0], parts[1] - 1, parts[2] + n);
  return toStr(newDate);
}

function kc(id) {
  return 'k' + (((Number(id) - 1) % 4) + 1);
}

const STATUS = { booked: 'Забронировано', issued: 'На руках', returned: 'Возвращено', cancelled: 'Отменено' };
const ROLE = { client: 'Клиент', manager: 'Менеджер', admin: 'Админ' };
const MONTHS = ['янв', 'фев', 'мар', 'апр', 'май', 'июн', 'июл', 'авг', 'сен', 'окт', 'ноя', 'дек'];

// если картинка не загрузилась
window.imgFail = function(img) {
  const d = document.createElement('div');
  d.className = 'img-fallback';
  d.textContent = img.alt;
  img.replaceWith(d);
};

// запрос к серверу
async function api(path, method = 'GET', body) {
  const headers = {};
  const token = localStorage.getItem('token');
  if (token) {
    headers.Authorization = 'Bearer ' + token;
  }
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
  }
  
  const options = { method: method, headers: headers };
  if (body !== undefined) {
    options.body = JSON.stringify(body);
  }
  
  const res = await fetch('/api' + path, options);
  let data = {};
  try { 
    data = await res.json(); 
  } catch (e) { }
  
  if (!res.ok) {
    if (res.status === 401 && token) {
      localStorage.removeItem('token');
      S.user = null;
      renderNav();
    }
    throw new Error(data.error || 'Ошибка запроса');
  }
  return data;
}

// всплывающие уведомления
function toast(msg, type = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + type;
  t.textContent = msg;
  $('#toasts').appendChild(t);
  setTimeout(function() { t.remove(); }, 3800);
}

// модальное окно
function openModal(html, wide = false) {
  const m = $('#modal');
  m.innerHTML = `<div class="dialog${wide ? ' wide' : ''}">${html}</div>`;
  m.classList.remove('hidden');
  m.onmousedown = function(e) { 
    if (e.target === m) closeModal(); 
  };
  
  const closeBtns = m.querySelectorAll('[data-close]');
  closeBtns.forEach(function(b) {
    b.addEventListener('click', closeModal);
  });
  return m;
}

function closeModal() {
  const m = $('#modal');
  m.classList.add('hidden');
  m.innerHTML = '';
}

document.addEventListener('keydown', function(e) { 
  if (e.key === 'Escape') closeModal(); 
});

// обёртка для обработчиков с ошибками
function guard(fn) {
  return async function(e) {
    try { 
      await fn(e); 
    } catch (err) { 
      toast(err.message, 'err'); 
    }
  };
}

// верхнее меню
function renderNav() {
  const r = S.user ? S.user.role : '';
  const cur = location.hash.replace(/^#\/?/, '');
  const items = [['', 'Каталог']];
  
  if (S.user) items.push(['cabinet', 'Мои аренды']);
  if (r === 'manager' || r === 'admin') {
    items.push(['manager', 'Выдача и приём'], ['stock', 'Склад'], ['stats', 'Статистика']);
  }
  if (r === 'admin') items.push(['admin', 'Администрирование']);
  
  let h = '';
  for (let i = 0; i < items.length; i++) {
    const k = items[i][0];
    const t = items[i][1];
    const cls = cur === k ? ' on' : '';
    h += `<a class="nl${cls}" href="#/${k}">${t}</a>`;
  }
  
  h += '<span class="sp"></span>';
  
  if (S.user) {
    h += `<span class="who"><b>${esc(S.user.name)}</b><span class="role">${ROLE[S.user.role]}</span></span><button class="btn sm ghost" id="logout">Выйти</button>`;
  } else {
    h += `<a class="btn sm primary" style="text-decoration:none" href="#/login">Войти</a>`;
  }
  
  $('#nav').innerHTML = h;
  
  const lo = $('#logout');
  if (lo) {
    lo.onclick = guard(async function() {
      try { await api('/logout', 'POST'); } catch (e) { }
      localStorage.removeItem('token');
      S.user = null;
      location.hash = '#/';
      route();
    });
  }
}

// маршруты
const routes = { '': catalog, login: authPage, cabinet: cabinet, manager: manager, stock: stock, admin: admin, stats: stats };
const access = { 
  cabinet: ['client', 'manager', 'admin'], 
  manager: ['manager', 'admin'], 
  stock: ['manager', 'admin'], 
  admin: ['admin'], 
  stats: ['manager', 'admin'] 
};

async function route() {
  closeModal();
  const key = location.hash.replace(/^#\/?/, '').split('?')[0];
  const fn = routes[key] || catalog;
  const need = access[key];
  
  if (need && !S.user) { 
    toast('Сначала войдите в систему', 'warn'); 
    location.hash = '#/login'; 
    return; 
  }
  
  if (need) {
    let hasAccess = false;
    for (let i = 0; i < need.length; i++) {
      if (need[i] === S.user.role) {
        hasAccess = true;
        break;
      }
    }
    if (!hasAccess) {
      toast('Недостаточно прав для этого раздела', 'err'); 
      location.hash = '#/'; 
      return;
    }
  }
  
  renderNav();
  view.innerHTML = '';
  view.onclick = null;
  window.scrollTo(0, 0);
  try { 
    await fn(); 
  } catch (err) { 
    view.innerHTML = `<div class="empty">${esc(err.message)}</div>`; 
  }
}

// каталог
let F = { q: '', cat: 0, max: '', from: '', to: '', only: false };
let items = [];

async function catalog() {
  view.innerHTML = `
  <h2>Каталог оборудования</h2>
  <section class="filters">
    <input id="fq" type="search" placeholder="Поиск: палатка, велосипед, перфоратор…" value="${esc(F.q)}">
    <div class="chips" id="fcats"></div>
    <div class="frow">
      <label>Цена до, ₽ за сутки<input id="fmax" type="number" min="0" step="50" value="${esc(F.max)}" placeholder="любая"></label>
      <label>Свободно с<input id="ffrom" type="date" min="${todayStr()}" value="${F.from}"></label>
      <label>Свободно по<input id="fto" type="date" min="${todayStr()}" value="${F.to}"></label>
      <label class="chk"><input id="fonly" type="checkbox" ${F.only ? 'checked' : ''}> Только доступное</label>
      <button class="btn ghost" id="freset">Сбросить</button>
    </div>
  </section>
  <div id="grid" class="grid"></div>`;
  
  drawChips();
  
  $('#fq').oninput = function(e) { F.q = e.target.value; drawGrid(); };
  $('#fmax').oninput = function(e) { F.max = e.target.value; drawGrid(); };
  $('#fonly').onchange = function(e) { F.only = e.target.checked; drawGrid(); };
  
  $('#ffrom').onchange = function(e) {
    F.from = e.target.value;
    if (F.from && (!F.to || F.to < F.from)) { 
      F.to = F.from; 
      $('#fto').value = F.to; 
    }
    loadItems();
  };
  
  $('#fto').onchange = function(e) {
    F.to = e.target.value;
    if (F.to && F.from && F.to < F.from) { 
      F.to = F.from; 
      e.target.value = F.to; 
      toast('Дата «по» не может быть раньше даты «с»', 'warn'); 
    }
    if (F.to && !F.from) { 
      F.from = F.to; 
      $('#ffrom').value = F.from; 
    }
    loadItems();
  };
  
  $('#freset').onclick = function() {
    F = { q: '', cat: 0, max: '', from: '', to: '', only: false };
    catalog();
  };
  
  $('#grid').addEventListener('click', function(e) {
    const b = e.target.closest('[data-book]');
    if (b) {
      const item = items.find(function(x) { return x.id === Number(b.dataset.book); });
      bookModal(item);
    }
  });
  
  await loadItems();
}

// кнопки категорий
function drawChips() {
  let h = `<button class="chip${F.cat === 0 ? ' on' : ''}" data-c="0">Все</button>`;
  
  for (let i = 0; i < S.cats.length; i++) {
    const c = S.cats[i];
    const cls = F.cat === c.id ? ' on' : '';
    h += `<button class="chip ${kc(c.id)}${cls}" data-c="${c.id}">${esc(c.name)}</button>`;
  }
  
  const box = $('#fcats');
  box.innerHTML = h;
  
  box.onclick = function(e) {
    const b = e.target.closest('[data-c]');
    if (!b) return;
    F.cat = Number(b.dataset.c);
    drawChips();
    drawGrid();
  };
}

// загрузка позиций
async function loadItems() {
  let q = '';
  if (F.from && F.to) {
    q = `?from=${F.from}&to=${F.to}`;
  }
  items = await api('/equipment' + q);
  drawGrid();
}

// отрисовка карточек
function drawGrid() {
  const q = F.q.trim().toLowerCase();
  const max = Number(F.max);
  
  const list = items.filter(function(e) {
    if (F.cat && e.category_id !== F.cat) return false;
    if (q && !e.name.toLowerCase().includes(q) && !e.description.toLowerCase().includes(q)) return false;
    if (max && e.price_per_day > max) return false;
    if (F.only && e.available <= 0) return false;
    return true;
  });
  
  const g = $('#grid');
  if (!g) return;
  
  if (list.length === 0) {
    g.innerHTML = '<div class="empty" style="grid-column:1/-1">По вашему запросу ничего не найдено</div>';
  } else {
    let html = '';
    for (let i = 0; i < list.length; i++) {
      html += cardHtml(list[i]);
    }
    g.innerHTML = html;
  }
}

// картинка или текст
function picHtml(e) {
  if (e.image_url) {
    return `<img src="${esc(e.image_url)}" alt="${esc(e.name)}" loading="lazy" referrerpolicy="no-referrer" onerror="imgFail(this)">`;
  }
  return `<div class="img-fallback">${esc(e.name)}</div>`;
}

// карточка товара
function cardHtml(e) {
  const ok = e.available > 0;
  let when = 'сегодня';
  if (F.from && F.to) {
    when = 'на выбранные даты';
  }
  
  let availableText = '';
  if (!ok) {
    availableText = '- можно выбрать другие даты';
  }
  
  const totalQty = e.total_qty - e.repair_qty - e.written_off_qty;
  const btnClass = ok ? 'primary' : 'violet';
  const btnText = ok ? 'Забронировать' : 'Выбрать даты';
  
  return `<article class="card">
    <div class="pic">${picHtml(e)}<span class="badge ${kc(e.category_id)}">${esc(e.category_name)}</span></div>
    <div class="cbody">
      <h3>${esc(e.name)}</h3>
      <p>${esc(e.description)}</p>
      <div class="left ${ok ? 'ok' : 'no'}">Свободно ${when}: <b>${e.available}</b> из ${totalQty}${availableText}</div>
      <div class="cfoot">
        <div class="price">${money(e.price_per_day)} <small>/ сутки</small></div>
        <button class="btn sm ${btnClass}" data-book="${e.id}" >${btnText}</button>
      </div>
    </div>
  </article>`;
}

// окно бронирования
function bookModal(e) {
  if (!e) return;
  
  if (!S.user) { 
    toast('Войдите, чтобы забронировать', 'warn'); 
    location.hash = '#/login'; 
    return; 
  }
  
  let from = todayStr();
  if (F.from) from = F.from;
  
  let to = addDays(from, 2);
  if (F.to) to = F.to;
  
  const m = openModal(`
    <form id="bf" novalidate>
      <h3>Бронирование оборудования</h3>
      <div class="eq">${esc(e.name)}  - ${money(e.price_per_day)} / сутки</div>
      <div class="row3">
        <label>Дата начала<input type="date" name="from" min="${todayStr()}" value="${from}"></label>
        <label>Дата окончания<input type="date" name="to" min="${todayStr()}" value="${to}"></label>
        <label>Кол-во<input type="number" name="qty" min="1" value="1"></label>
      </div>
      <div class="quote" id="quote">Считаем…</div>
      <div class="acts">
        <button type="button" class="btn ghost" data-close>Отмена</button>
        <button class="btn primary" id="bsub" disabled>Забронировать</button>
      </div>
    </form>`);
    
  const f = $('#bf', m);
  let ready = false;

  // пересчет
  async function update() {
    const q = $('#quote', m);
    const fromVal = f.elements.from.value;
    const toVal = f.elements.to.value;
    const qtyVal = f.elements.qty.value;
    
    ready = false;
    $('#bsub', m).disabled = true;
    
    if (!fromVal || !toVal) { 
      q.innerHTML = '<span class="bad">Укажите даты аренды</span>'; 
      return; 
    }
    
    const n = Number(qtyVal);
    if (!Number.isInteger(n) || n < 1) { 
      q.innerHTML = '<span class="bad">Количество должно быть больше 0</span>'; 
      return; 
    }
    
    try {
      const r = await api(`/quote?id=${e.id}&from=${fromVal}&to=${toVal}&qty=${n}`);
      const enough = r.available >= n;
      
      let discText = ' -';
      if (r.discount) {
        discText = r.discount + ' %';
      }
      
      let errText = '';
      if (!enough) {
        errText = '<div class="bad">Нужного количества нет  - измените даты или количество</div>';
      }
      
      q.innerHTML = `
        <div><span>Срок аренды</span><b>${r.days} сут.</b></div>
        <div><span>Стоимость без скидки</span><b>${money(r.base)}</b></div>
        <div><span>Скидка по тарифу</span><b>${discText}</b></div>
        <div><span>Залог (возвращается)</span><b>${money(r.deposit)}</b></div>
        <div><span>Доступно на эти даты</span><b class="${enough ? '' : 'bad'}">${r.available} шт.</b></div>
        <div class="tot"><span>Итого</span><span>${money(r.total)}</span></div>
        ${errText}`;
        
      ready = enough;
      $('#bsub', m).disabled = !enough;
    } catch (err) {
      q.innerHTML = `<span class="bad">${esc(err.message)}</span>`;
    }
  }
  
  f.addEventListener('input', update);

  // отправка
  f.addEventListener('submit', guard(async function(ev) {
    ev.preventDefault();
    if (!ready) return;
    
    await api('/bookings', 'POST', { 
      equipmentId: e.id, 
      qty: Number(f.elements.qty.value), 
      dateFrom: f.elements.from.value, 
      dateTo: f.elements.to.value 
    });
    
    closeModal();
    toast('Бронь оформлена. Заберите оборудование в день начала аренды');
    location.hash = '#/cabinet';
  }));
  
  update();
}

// вход и регистрация
let authMode = 'login';

function authPage() {
  if (S.user) { 
    location.hash = '#/'; 
    return; 
  }
  
  const reg = authMode === 'register';
  let regHtml = '';
  if (reg) {
    regHtml = '<label>Имя<input name="name" autocomplete="name" placeholder="Как к вам обращаться"></label>';
  }
  
  let autocomplete = reg ? 'new-password' : 'current-password';
  let btnText = reg ? 'Создать аккаунт' : 'Войти';
  let tabCls1 = reg ? '' : ' on';
  let tabCls2 = reg ? ' on' : '';
  
  view.innerHTML = `
  <div class="box authbox">
    <div class="tabs">
      <button class="tab${tabCls1}" data-m="login">Вход</button>
      <button class="tab${tabCls2}" data-m="register">Регистрация</button>
    </div>
    <form class="form" id="af" novalidate>
      ${regHtml}
      <label>Email<input name="email" type="email" autocomplete="email" placeholder="you@example.com"></label>
      <label>Пароль<input name="password" type="password" autocomplete="${autocomplete}" placeholder="Минимум 6 символов"></label>
      <button class="btn primary">${btnText}</button>
    </form>
  </div>`;
  
  const tabs = view.querySelectorAll('[data-m]');
  tabs.forEach(function(b) {
    b.onclick = function() { 
      authMode = b.dataset.m; 
      authPage(); 
    };
  });

  $('#af').onsubmit = guard(async function(ev) {
    ev.preventDefault();
    const f = ev.target.elements;
    const email = f.email.value.trim();
    
    if (!email || !f.password.value) { 
      toast('Заполните email и пароль', 'warn'); 
      return; 
    }
    
    if (reg && f.name.value.trim().length < 2) { 
      toast('Введите имя (минимум 2 символа)', 'warn'); 
      return; 
    }
    
    if (reg && f.password.value.length < 6) { 
      toast('Пароль - минимум 6 символов', 'warn'); 
      return; 
    }
    
    const body = { email: email, password: f.password.value };
    if (reg) {
      body.name = f.name.value.trim();
    }
    
    const r = await api(reg ? '/register' : '/login', 'POST', body);
    localStorage.setItem('token', r.token);
    S.user = r.user;
    toast('Добро пожаловать, ' + r.user.name + '!');
    
    if (r.user.role === 'manager') {
      location.hash = '#/manager';
    } else {
      location.hash = '#/';
    }
  });
}

// личный кабинет
function stBadge(b) {
  let h = `<span class="st ${b.status}">${STATUS[b.status]}</span>`;
  if (b.overdue_days > 0) { 
    h += ` <span class="st over">просрочка ${b.overdue_days} дн.</span>`; 
  }
  return h;
}

async function cabinet() {
  const list = await api('/bookings/my');
  
  const cur = list.filter(function(b) { 
    return b.status === 'booked' || b.status === 'issued'; 
  });
  
  const hist = list.filter(function(b) { 
    return b.status === 'returned' || b.status === 'cancelled'; 
  });
  
  function rows(arr, canCancel) {
    if (arr.length === 0) {
      return '<div class="empty">Здесь пока пусто</div>';
    }
    
    let html = '<div class="tw"><table><thead><tr><th>№</th><th>Оборудование</th><th>Период</th><th>Кол-во</th><th>Сумма</th><th>Статус</th><th>Штраф</th>';
    if (canCancel) html += '<th></th>';
    html += '</tr></thead><tbody>';
    
    for (let i = 0; i < arr.length; i++) {
      const b = arr[i];
      const fine = b.fine_overdue + b.fine_damage;
      let fineStr = ' -';
      if (fine) {
        fineStr = `<span class="fine">${money(fine)}</span>`;
      }
      
      let cancelBtn = '';
      if (canCancel && b.status === 'booked') {
        cancelBtn = `<button class="btn sm danger" data-cancel="${b.id}">Отменить</button>`;
      }
      
      html += `<tr><td>${b.id}</td><td>${esc(b.equipment_name)}</td><td>${fmtDate(b.date_from)}  - ${fmtDate(b.date_to)}</td><td>${b.qty}</td><td>${money(b.total)}</td><td>${stBadge(b)}</td><td>${fineStr}</td>`;
      if (canCancel) html += `<td>${cancelBtn}</td>`;
      html += '</tr>';
    }
    
    html += '</tbody></table></div>';
    return html;
  }
  
  view.innerHTML = `
    <h2>Личный кабинет</h2>
    <p class="muted" style="margin-top:-8px">${esc(S.user.name)} · ${esc(S.user.email)}</p>
    <section class="sec"><h3>Текущие бронирования</h3>${rows(cur, true)}</section>
    <section class="sec"><h3>История аренд</h3>${rows(hist, false)}</section>`;
    
  view.onclick = guard(async function(e) {
    const b = e.target.closest('[data-cancel]');
    if (!b) return;
    
    if (!confirm('Отменить бронь №' + b.dataset.cancel + '?')) return;
    await api(`/bookings/${b.dataset.cancel}/cancel`, 'POST');
    toast('Бронь отменена');
    cabinet();
  });
}

// менеджер
let mFilter = 'booked';

async function manager() {
  const all = await api('/manage/bookings');
  
  const pick = {
    booked: function(b) { return b.status === 'booked'; },
    issued: function(b) { return b.status === 'issued'; },
    over: function(b) { return b.overdue_days > 0; },
    all: function() { return true; }
  };
  
  const cnt = function(k) {
    return all.filter(pick[k]).length;
  };
  
  const list = all.filter(pick[mFilter]);
  
  function tab(k, t) {
    const cls = mFilter === k ? ' on' : '';
    return `<button class="tab${cls}" data-f="${k}">${t} (${cnt(k)})</button>`;
  }
  
  let tableHtml = '';
  if (list.length === 0) {
    tableHtml = '<div class="empty">В этом списке ничего нет</div>';
  } else {
    tableHtml = '<div class="tw"><table><thead><tr><th>№</th><th>Клиент</th><th>Оборудование</th><th>Период</th><th>Статус</th><th>Заметки</th><th></th></tr></thead><tbody>';
    
    for (let i = 0; i < list.length; i++) {
      const b = list[i];
      let note = esc(b.issue_note);
      if (b.return_note) {
        note += '<br>↩ ' + esc(b.return_note);
      }
      
      let actions = '';
      if (b.status === 'booked') {
        actions = `<button class="btn sm primary" data-issue="${b.id}">Выдать</button><button class="btn sm danger" data-cancel="${b.id}">Отменить</button>`;
      } else if (b.status === 'issued') {
        actions = `<button class="btn sm violet" data-return="${b.id}">Принять</button>`;
      }
      
      tableHtml += `<tr>
      <td>${b.id}</td>
      <td>${esc(b.user_name)}<br><span class="muted">${esc(b.user_email)}</span></td>
      <td>${esc(b.equipment_name)} × ${b.qty}</td>
      <td>${fmtDate(b.date_from)}  - ${fmtDate(b.date_to)}</td>
      <td>${stBadge(b)}</td>
      <td class="muted">${note}</td>
      <td>${actions}</td>
    </tr>`;
    }
    
    tableHtml += '</tbody></table></div>';
  }
  
  view.innerHTML = `
    <h2>Выдача и приём оборудования</h2>
    <div class="tabs">${tab('booked', 'Ожидают выдачи')}${tab('issued', 'На руках')}${tab('over', 'Просрочены')}${tab('all', 'Все')}</div>
    ${tableHtml}`;
    
  view.onclick = guard(async function(e) {
    const t = e.target.closest('button');
    if (!t) return;
    
    if (t.dataset.f) { 
      mFilter = t.dataset.f; 
      manager(); 
      return; 
    }
    
    if (t.dataset.cancel) {
      if (!confirm('Отменить бронь №' + t.dataset.cancel + '?')) return;
      await api(`/bookings/${t.dataset.cancel}/cancel`, 'POST');
      toast('Бронь отменена');
      manager();
    }
    
    if (t.dataset.issue) {
      const b = all.find(function(x) { return x.id === Number(t.dataset.issue); });
      issueModal(b);
    }
    
    if (t.dataset.return) {
      const b = all.find(function(x) { return x.id === Number(t.dataset.return); });
      returnModal(b);
    }
  });
}

// выдача
function issueModal(b) {
  const m = openModal(`
    <form id="mf" novalidate>
      <h3>Выдача оборудования</h3>
      <div class="eq">${esc(b.equipment_name)} × ${b.qty}  - ${esc(b.user_name)}</div>
      <div class="form">
        <label>Состояние при выдаче
          <select name="state"><option>Отличное</option><option selected>Хорошее</option><option>Удовлетворительное</option><option>Есть мелкие дефекты</option></select></label>
        <label>Комментарий<textarea name="note" maxlength="400" placeholder="Комплектность, царапины, заметки…"></textarea></label>
      </div>
      <div class="acts"><button type="button" class="btn ghost" data-close>Отмена</button><button class="btn primary">Выдать</button></div>
    </form>`);
    
  $('#mf', m).onsubmit = guard(async function(ev) {
    ev.preventDefault();
    const f = ev.target.elements;
    
    let note = f.state.value;
    if (f.note.value.trim()) {
      note += '. ' + f.note.value.trim();
    }
    
    await api(`/manage/bookings/${b.id}/issue`, 'POST', { note: note });
    closeModal();
    toast('Оборудование выдано');
    manager();
  });
}

// прием
function returnModal(b) {
  const over = b.overdue_days || 0;
  const fineOver = over * b.price_per_day * b.qty * 1.5;
  
  const m = openModal(`
    <form id="mf" novalidate>
      <h3>Приём оборудования</h3>
      <div class="eq">${esc(b.equipment_name)} × ${b.qty}  - ${esc(b.user_name)}</div>
      <div class="quote">
        <div><span>Срок возврата</span><b>${fmtDate(b.date_to)}</b></div>
        <div><span>Просрочка</span><b class="${over ? 'bad' : ''}">${over} дн.</b></div>
        <div><span>Штраф за просрочку (150 % за сутки)</span><b class="${over ? 'bad' : ''}">${money(fineOver)}</b></div>
      </div>
      <div class="form">
        <label>Состояние при возврате
          <select name="state"><option>Без повреждений</option><option>Незначительные повреждения</option><option>Требует ремонта</option></select></label>
        <div class="row2">
          <label>Штраф за повреждение, ₽<input type="number" name="fine" min="0" step="50" value="0"></label>
          <label>Отправить в ремонт, шт.<input type="number" name="repair" min="0" max="${b.qty}" value="0"></label>
        </div>
        <label>Комментарий<textarea name="note" maxlength="400" placeholder="Что обнаружено при приёмке…"></textarea></label>
      </div>
      <div class="acts"><button type="button" class="btn ghost" data-close>Отмена</button><button class="btn violet">Принять</button></div>
    </form>`);
    
  $('#mf', m).onsubmit = guard(async function(ev) {
    ev.preventDefault();
    const f = ev.target.elements;
    const fine = Number(f.fine.value || 0);
    const repair = Number(f.repair.value || 0);
    
    if (!(fine >= 0)) { 
      toast('Штраф не может быть отрицательным', 'warn'); 
      return; 
    }
    
    if (!Number.isInteger(repair) || repair < 0 || repair > b.qty) { 
      toast('В ремонт можно отправить от 0 до ' + b.qty + ' шт.', 'warn'); 
      return; 
    }
    
    let note = f.state.value;
    if (f.note.value.trim()) {
      note += '. ' + f.note.value.trim();
    }
    
    const r = await api(`/manage/bookings/${b.id}/return`, 'POST', { note: note, damageFine: fine, repairQty: repair });
    closeModal();
    
    if (r.fineOverdue) {
      toast('Принято. Штраф за просрочку: ' + money(r.fineOverdue));
    } else {
      toast('Оборудование принято');
    }
    manager();
  });
}

// склад
async function stock() {
  const list = await api('/stock');
  
  let rowsHtml = '';
  for (let i = 0; i < list.length; i++) {
    const e = list[i];
    rowsHtml += `<tr data-id="${e.id}">
      <td>${esc(e.name)}<br><span class="muted">${esc(e.category_name)}</span></td>
      <td class="num"><input type="number" min="0" name="total" value="${e.total_qty}"></td>
      <td class="num"><input type="number" min="0" name="repair" value="${e.repair_qty}"></td>
      <td class="num"><input type="number" min="0" name="off" value="${e.written_off_qty}"></td>
      <td>${e.on_hand}</td>
      <td><b style="color:var(--green)">${e.free_now}</b></td>
            <td class="wide"><input name="cond" maxlength="60" value="${esc(e.cond)}"></td>
      <td><button class="btn sm primary" data-save="${e.id}">Сохранить</button></td>
    </tr>`;
  }
  
  view.innerHTML = `
    <h2>Учёт склада</h2>
    <div class="tw"><table><thead><tr><th>Оборудование</th><th>Всего</th><th>В ремонте</th><th>Списано</th><th>На руках</th><th>Свободно</th><th>Состояние</th><th></th></tr></thead><tbody>
    ${rowsHtml}</tbody></table></div>
    <p class="muted">Ремонт и списание уменьшают количество доступных единиц. Для списания увеличьте поле «Списано».</p>`;
    
  view.onclick = guard(async function(e) {
    const b = e.target.closest('[data-save]');
    if (!b) return;
    
    const tr = b.closest('tr');
    const total = Number(tr.querySelector('[name=total]').value);
    const repair = Number(tr.querySelector('[name=repair]').value);
    const off = Number(tr.querySelector('[name=off]').value);
    const cond = tr.querySelector('[name=cond]').value;
    
    if (!Number.isInteger(total) || total < 0) { 
      toast('Количество - целое неотрицательное число', 'warn'); 
      return; 
    }
    if (!Number.isInteger(repair) || repair < 0) { 
      toast('Количество - целое неотрицательное число', 'warn'); 
      return; 
    }
    if (!Number.isInteger(off) || off < 0) { 
      toast('Количество - целое неотрицательное число', 'warn'); 
      return; 
    }
    
    if (repair + off > total) { 
      toast('Ремонт + списание не могут превышать общее количество', 'warn'); 
      return; 
    }
    
    await api('/stock/' + b.dataset.save, 'PUT', { total: total, repair: repair, writtenOff: off, condition: cond });
    toast('Данные склада сохранены');
    stock();
  });
}

// администрирование
let aTab = 'catalog';

async function admin() {
  function tab(k, t) {
    const cls = aTab === k ? ' on' : '';
    return `<button class="tab${cls}" data-t="${k}">${t}</button>`;
  }
  
  view.innerHTML = `<h2>Администрирование</h2>
    <div class="tabs">${tab('catalog', 'Каталог')}${tab('tariffs', 'Тарифы')}${tab('staff', 'Пользователи и сотрудники')}</div>
    <div id="apane"></div>`;
    
  view.onclick = guard(function(e) {
    const t = e.target.closest('[data-t]');
    if (t) { 
      aTab = t.dataset.t; 
      admin(); 
    }
  });
  
  if (aTab === 'catalog') {
    await adminCatalog();
  } else if (aTab === 'tariffs') {
    await adminTariffs();
  } else {
    await adminStaff();
  }
}

// админ каталог
async function adminCatalog() {
  const res = await api('/equipment');
  const list = res;
  await refs();
  
  let tableHtml = '';
  for (let i = 0; i < list.length; i++) {
    const e = list[i];
    tableHtml += `<tr><td>${esc(e.name)}</td><td>${esc(e.category_name)}</td><td>${money(e.price_per_day)}</td><td>${money(e.deposit)}</td><td>${e.total_qty}</td>
      <td><button class="btn sm" data-edit="${e.id}">Изменить</button><button class="btn sm danger" data-del="${e.id}">Удалить</button></td></tr>`;
  }
  
  $('#apane').innerHTML = `
    <div class="bar"><h3 style="margin:0">Позиции каталога (${list.length})</h3>
      <div><button class="btn sm ghost" id="addcat">+ Категория</button> <button class="btn sm primary" id="addeq">+ Оборудование</button></div></div>
    <div class="tw"><table><thead><tr><th>Название</th><th>Категория</th><th>Цена / сутки</th><th>Залог</th><th>Всего</th><th></th></tr></thead><tbody>
    ${tableHtml}</tbody></table></div>`;
    
  $('#addeq').onclick = function() { equipModal(null); };
  
  $('#addcat').onclick = function() {
    const m = openModal(`<form id="cf" novalidate><h3>Новая категория</h3><label>Название<input name="name" maxlength="40"></label>
      <div class="acts"><button type="button" class="btn ghost" data-close>Отмена</button><button class="btn primary">Добавить</button></div></form>`);
      
    $('#cf', m).onsubmit = guard(async function(ev) {
      ev.preventDefault();
      await api('/admin/categories', 'POST', { name: ev.target.elements.name.value });
      closeModal();
      toast('Категория добавлена');
      admin();
    });
  };
  
  $('#apane').onclick = guard(async function(e) {
    const ed = e.target.closest('[data-edit]');
    const dl = e.target.closest('[data-del]');
    
    if (ed) {
      const item = list.find(function(x) { return x.id === Number(ed.dataset.edit); });
      equipModal(item);
    }
    
    if (dl) {
      if (!confirm('Удалить позицию из каталога?')) return;
      await api('/admin/equipment/' + dl.dataset.del, 'DELETE');
      toast('Позиция удалена');
      admin();
    }
  });
}

// справочники
async function refs() {
  S.cats = await api('/categories');
  S.tariffs = await api('/tariffs');
}

// окно позиции
function equipModal(e) {
  let v;
  if (e) {
    v = e;
  } else {
    let defCat = 1;
    if (S.cats.length > 0) defCat = S.cats[0].id;
    v = { name: '', category_id: defCat, description: '', image_url: '', price_per_day: '', deposit: 0, total_qty: 1, cond: 'Хорошее' };
  }
  
  let title = e ? 'Изменить позицию' : 'Новая позиция';
  
  let optionsHtml = '';
  for (let i = 0; i < S.cats.length; i++) {
    const c = S.cats[i];
    const sel = c.id === v.category_id ? 'selected' : '';
    optionsHtml += `<option value="${c.id}" ${sel}>${esc(c.name)}</option>`;
  }
  
  const m = openModal(`
    <form id="ef" novalidate>
      <h3>${title}</h3>
      <div class="form">
        <label>Название<input name="name" maxlength="80" value="${esc(v.name)}"></label>
        <div class="row2">
          <label>Категория<select name="cat">${optionsHtml}</select></label>
          <label>Состояние<input name="cond" maxlength="60" value="${esc(v.cond)}"></label>
        </div>
        <label>Описание<textarea name="desc" maxlength="300">${esc(v.description)}</textarea></label>
        <label>Ссылка на изображение (URL из интернета)<input name="img" placeholder="https://…" value="${esc(v.image_url)}"></label>
        <div class="row3" style="grid-template-columns:1fr 1fr 1fr">
          <label>Цена за сутки, ₽<input type="number" name="price" min="1" step="10" value="${v.price_per_day}"></label>
          <label>Залог, ₽<input type="number" name="dep" min="0" step="100" value="${v.deposit}"></label>
          <label>Количество<input type="number" name="qty" min="0" value="${v.total_qty}"></label>
        </div>
      </div>
      <div class="acts"><button type="button" class="btn ghost" data-close>Отмена</button><button class="btn primary">Сохранить</button></div>
    </form>`, true);
    
  $('#ef', m).onsubmit = guard(async function(ev) {
    ev.preventDefault();
    const f = ev.target.elements;
    
    if (f.name.value.trim().length < 2) { 
      toast('Введите название (минимум 2 символа)', 'warn'); 
      return; 
    }
    
    const price = Number(f.price.value);
    const dep = Number(f.dep.value || 0);
    const qty = Number(f.qty.value);
    
    if (!(price > 0)) { 
      toast('Цена за сутки должна быть больше 0', 'warn'); 
      return; 
    }
    if (!(dep >= 0)) { 
      toast('Залог не может быть отрицательным', 'warn'); 
      return; 
    }
    if (!Number.isInteger(qty) || qty < 0) { 
      toast('Количество - целое число от 0', 'warn'); 
      return; 
    }
    
    const body = { 
      name: f.name.value, 
      categoryId: Number(f.cat.value), 
      description: f.desc.value, 
      imageUrl: f.img.value, 
      pricePerDay: price, 
      deposit: dep, 
      totalQty: qty, 
      cond: f.cond.value 
    };
    
    let url = '/admin/equipment';
    let method = 'POST';
    if (e) { 
      url = '/admin/equipment/' + e.id; 
      method = 'PUT'; 
    }
    
    await api(url, method, body);
    closeModal();
    toast('Сохранено');
    admin();
  });
}

// админ тарифы
async function adminTariffs() {
  await refs();
  
  let tariffsHtml = '<div class="empty">Тарифов нет</div>';
  if (S.tariffs.length > 0) {
    let rows = '';
    for (let i = 0; i < S.tariffs.length; i++) {
      const t = S.tariffs[i];
      rows += `<tr><td>${esc(t.name)}</td><td>${t.min_days}</td><td>${t.discount_percent} %</td><td><button class="btn sm danger" data-dt="${t.id}">Удалить</button></td></tr>`;
    }
    tariffsHtml = `<div class="tw"><table style="min-width:0"><thead><tr><th>Тариф</th><th>От, суток</th><th>Скидка</th><th></th></tr></thead><tbody>${rows}</tbody></table></div>`;
  }
  
  $('#apane').innerHTML = `
    <div class="two">
      <div><h3>Скидки за длительную аренду</h3>${tariffsHtml}</div>
      <form class="box form" id="tf" novalidate><h3 style="margin:0">Новый тариф</h3>
        <label>Название<input name="name" maxlength="40" placeholder="Например, «Месяц»"></label>
        <div class="row2"><label>От скольких суток<input type="number" name="days" min="2" max="30" value="5"></label>
        <label>Скидка, %<input type="number" name="pct" min="1" max="70" value="8"></label></div>
        <button class="btn violet">Добавить</button></form>
    </div>`;
    
  $('#tf').onsubmit = guard(async function(ev) {
    ev.preventDefault();
    const f = ev.target.elements;
    const days = Number(f.days.value);
    const pct = Number(f.pct.value);
    
    if (f.name.value.trim().length < 2) { 
      toast('Введите название тарифа', 'warn'); 
      return; 
    }
    if (!Number.isInteger(days) || days < 2 || days > 30) { 
      toast('Срок - от 2 до 30 суток', 'warn'); 
      return; 
    }
    if (!(pct > 0 && pct <= 70)) { 
      toast('Скидка - от 1 до 70 %', 'warn'); 
      return; 
    }
    
    await api('/admin/tariffs', 'POST', { name: f.name.value, minDays: days, discountPercent: pct });
    toast('Тариф добавлен');
    admin();
  });
  
  $('#apane').addEventListener('click', guard(async function(e) {
    const b = e.target.closest('[data-dt]');
    if (!b) return;
    await api('/admin/tariffs/' + b.dataset.dt, 'DELETE');
    toast('Тариф удалён');
    admin();
  }));
}

// админ пользователи
async function adminStaff() {
  const users = await api('/admin/users');
  
  let rows = '';
  for (let i = 0; i < users.length; i++) {
    const u = users[i];
    const isMe = u.id === S.user.id;
    const dis = isMe ? 'disabled' : '';
    
    let selHtml = '';
    const roles = ['client', 'manager', 'admin'];
    for (let j = 0; j < roles.length; j++) {
      const r = roles[j];
      const sel = u.role === r ? 'selected' : '';
      selHtml += `<option value="${r}" ${sel}>${ROLE[r]}</option>`;
    }
    
    let delBtn = '';
    if (!isMe) {
      delBtn = `<button class="btn sm danger" data-du="${u.id}">Удалить</button>`;
    }
    
    rows += `<tr><td>${esc(u.name)}</td><td>${esc(u.email)}</td>
        <td><select data-role="${u.id}" ${dis} style="width:140px;padding:5px 8px">
          ${selHtml}</select></td>
        <td>${delBtn}</td></tr>`;
  }
  
  $('#apane').innerHTML = `
    <div class="two" style="grid-template-columns:2fr 1fr">
      <div class="tw"><table><thead><tr><th>Имя</th><th>Email</th><th>Роль</th><th></th></tr></thead><tbody>
      ${rows}</tbody></table></div>
      <form class="box form" id="uf" novalidate><h3 style="margin:0">Новый пользователь</h3>
        <label>Имя<input name="name"></label>
        <label>Email<input name="email" type="email"></label>
        <label>Пароль<input name="pw" type="password" placeholder="Минимум 6 символов"></label>
        <label>Роль<select name="role"><option value="manager">Менеджер</option><option value="client">Клиент</option><option value="admin">Администратор</option></select></label>
        <button class="btn violet">Создать</button></form>
    </div>`;
    
  $('#uf').onsubmit = guard(async function(ev) {
    ev.preventDefault();
    const f = ev.target.elements;
    
    if (f.name.value.trim().length < 2) { 
      toast('Введите имя (минимум 2 символа)', 'warn'); 
      return; 
    }
    if (!f.email.value.includes('@')) { 
      toast('Введите корректный email', 'warn'); 
      return; 
    }
    if (f.pw.value.length < 6) { 
      toast('Пароль - минимум 6 символов', 'warn'); 
      return; 
    }
    
    await api('/admin/users', 'POST', { name: f.name.value, email: f.email.value, password: f.pw.value, role: f.role.value });
    toast('Пользователь создан');
    admin();
  });
  
  $('#apane').addEventListener('change', guard(async function(e) {
    const s = e.target.closest('[data-role]');
    if (!s) return;
    await api('/admin/users/' + s.dataset.role + '/role', 'PUT', { role: s.value });
    toast('Роль изменена');
  }));
  
  $('#apane').addEventListener('click', guard(async function(e) {
    const b = e.target.closest('[data-du]');
    if (!b) return;
    if (!confirm('Удалить пользователя?')) return;
    await api('/admin/users/' + b.dataset.du, 'DELETE');
    toast('Пользователь удалён');
    admin();
  }));
}

// статистика
const COLORS = ['#34d399', '#a78bfa', '#fbbf24', '#38bdf8', '#fb7185'];

async function stats() {
  const s = await api('/stats');
  view.innerHTML = `
    <h2>Статистика проката</h2>
    <div class="kpis">
      <div class="kpi"><span>Выручка (закрытые аренды)</span><b>${money(s.revenue)}</b></div>
      <div class="kpi"><span>Всего броней</span><b>${s.total}</b></div>
      <div class="kpi"><span>Сейчас на руках</span><b>${s.issued}</b></div>
      <div class="kpi"><span>Штрафы</span><b>${money(s.fines)}</b></div>
      <div class="kpi"><span>Просрочено</span><b>${s.overdue}</b></div>
    </div>
    <div class="charts">
      <div class="box chart"><h3>Выручка по месяцам</h3>${barChart(s.months)}</div>
      <div class="box chart"><h3>Аренды по категориям</h3>${donut(s.cats)}</div>
      <div class="box chart" style="grid-column:1/-1"><h3>Самое востребованное оборудование</h3>${hbars(s.top)}</div>
    </div>`;
}

// график столбики
function barChart(months) {
  if (months.length === 0) return '<div class="empty">Нет данных</div>';
  
  const W = 520, H = 250, pl = 46, pb = 28, pt = 14;
  let max = 1;
  for (let i = 0; i < months.length; i++) {
    if (months[i].sum > max) max = months[i].sum;
  }
  
  const step = Math.pow(10, Math.floor(Math.log10(max)));
  const top = Math.ceil(max / step) * step;
  const bw = (W - pl - 10) / months.length;
  
  let g = '';
  for (let i = 0; i <= 4; i++) {
    const y = pt + (H - pt - pb) * (1 - i / 4);
    g += `<line x1="${pl}" x2="${W - 6}" y1="${y}" y2="${y}" stroke="#24242b"/><text x="${pl - 6}" y="${y + 4}" text-anchor="end">${Math.round(top * i / 4 / 1000)}к</text>`;
  }
  
  let bars = '';
  for (let i = 0; i < months.length; i++) {
    const m = months[i];
    const h = (H - pt - pb) * (m.sum / top);
    const x = pl + i * bw + bw * 0.2;
    const y = H - pb - h;
    bars += `<rect x="${x}" y="${y}" width="${bw * 0.6}" height="${h}" fill="url(#bg)"><title>${money(m.sum)} · броней: ${m.cnt}</title></rect>
      <text x="${x + bw * 0.3}" y="${H - 10}" text-anchor="middle">${MONTHS[Number(m.m.slice(5)) - 1]}</text>`;
  }
  
  return `<svg viewBox="0 0 ${W} ${H}"><defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#a78bfa"/><stop offset="1" stop-color="#34d399"/></linearGradient></defs>${g}${bars}</svg>`;
}

// круговая
function donut(cats) {
  let total = 0;
  for (let i = 0; i < cats.length; i++) {
    total += cats[i].cnt;
  }
  
  if (total === 0) return '<div class="empty">Нет данных</div>';
  
  const R = 62, C = 2 * Math.PI * R;
  let off = 0;
  let segs = '';
  
  for (let i = 0; i < cats.length; i++) {
    const c = cats[i];
    const len = C * c.cnt / total;
    const color = COLORS[i % COLORS.length];
    segs += `<circle cx="100" cy="100" r="${R}" fill="none" stroke="${color}" stroke-width="30" stroke-dasharray="${len} ${C - len}" stroke-dashoffset="${-off}" transform="rotate(-90 100 100)"><title>${esc(c.name)}: ${c.cnt}</title></circle>`;
    off += len;
  }
  
  let legend = '';
  for (let i = 0; i < cats.length; i++) {
    const c = cats[i];
    const color = COLORS[i % COLORS.length];
    const pct = Math.round(c.cnt * 100 / total);
    legend += `<span><i style="background:${color}"></i>${esc(c.name)}  - ${c.cnt} (${pct}%)</span>`;
  }
  
  return `<svg viewBox="0 0 200 200" style="max-width:240px;margin:0 auto">${segs}<text x="100" y="98" text-anchor="middle" style="font-size:26px;font-weight:700;fill:#ececf0">${total}</text><text x="100" y="116" text-anchor="middle">броней</text></svg><div class="legend">${legend}</div>`;
}

// полоски
function hbars(top) {
  if (top.length === 0) return '<div class="empty">Нет данных</div>';
  
  let max = 1;
  for (let i = 0; i < top.length; i++) {
    if (top[i].cnt > max) max = top[i].cnt;
  }
  
  let html = '<div class="hb">';
  for (let i = 0; i < top.length; i++) {
    const t = top[i];
    const w = t.cnt / max * 100;
    html += `<div class="r"><span>${esc(t.name)}</span><div class="tr"><i style="width:${w}%"></i></div><b>${t.cnt}</b></div>`;
  }
  html += '</div>';
  return html;
}

// запуск
window.addEventListener('hashchange', route);

(async function init() {
  try {
    if (localStorage.getItem('token')) {
      try { 
        S.user = await api('/me'); 
      } catch (e) { 
        S.user = null; 
      }
    }
    await refs();
  } catch (err) {
    toast(err.message, 'err');
  }
  route();
})();