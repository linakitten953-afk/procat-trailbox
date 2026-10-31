using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

var root = H.FindRoot();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = root,
    WebRootPath = Path.Combine(root, "wwwroot"),
    Args = args
});
builder.WebHost.UseUrls("http://localhost:5080");
var app = builder.Build();

Db.DbFile = Path.Combine(root, "rental.db");
Db.Init();

app.UseDefaultFiles();
app.UseStaticFiles();

// функция для ошибок
IResult Err(string message, int code = 400) 
{
    return Results.Json(new { error = message }, statusCode: code);
}

// проверка токена
Dictionary<string, object?>? Auth(HttpRequest r)
{
    var authHeader = r.Headers["Authorization"].ToString();
    if (!authHeader.StartsWith("Bearer ")) 
    {
        return null;
    }
    
    string token = authHeader.Substring(7);
    var usersList = Db.Q("SELECT u.id, u.name, u.email, u.role FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.token = @token", new { token });
    
    if (usersList.Count == 0) 
    {
        return null;
    }
    return usersList[0];
}

// проверка роли
IResult WithRole(HttpRequest r, string[] roles, Func<Dictionary<string, object?>, IResult> act)
{
    var user = Auth(r);
    if (user == null) 
    {
        return Err("Требуется вход в систему", 401);
    }
    
    string userRole = user.Str("role");
    if (roles.Length > 0) 
    {
        bool hasRole = false;
        foreach (var role in roles) 
        {
            if (role == userRole) 
            {
                hasRole = true;
                break;
            }
        }
        if (!hasRole) 
        {
            return Err("Недостаточно прав", 403);
        }
    }
    return act(user);
}

// старт сессии
IResult StartSession(string email)
{
    var usersList = Db.Q("SELECT id, name, email, role FROM users WHERE email = @email", new { email });
    var user = usersList[0];
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    
    Db.Exec("INSERT INTO sessions(token, user_id, created) VALUES(@token, @uid, @c)", new { token, uid = user.Int("id"), c = H.Now() });
    return Results.Json(new { token, user = user });
}

// регистрация
app.MapPost("/api/register", (RegisterDto d) =>
{
    string name = (d.Name ?? "").Trim();
    string email = (d.Email ?? "").Trim().ToLowerInvariant();
    string pw = d.Password ?? "";
    
    if (name.Length < 2) 
    {
        return Err("Введите имя (минимум 2 символа)");
    }
    if (!H.ValidEmail(email)) 
    {
        return Err("Некорректный email");
    }
    if (pw.Length < 6) 
    {
        return Err("Пароль - минимум 6 символов");
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM users WHERE email = @email", new { email });
    if (count > 0) 
    {
        return Err("Этот email уже зарегистрирован");
    }
    
    Db.Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES(@name, @email, @h, 'client', @c)", 
        new { name, email, h = H.Hash(pw), c = H.Now() });
        
    return StartSession(email);
});

// вход
app.MapPost("/api/login", (LoginDto d) =>
{
    string email = (d.Email ?? "").Trim().ToLowerInvariant();
    var usersList = Db.Q("SELECT pass_hash FROM users WHERE email = @email", new { email });
    
    if (usersList.Count == 0) 
    {
        return Err("Неверный email или пароль");
    }
    
    var user = usersList[0];
    string storedHash = user.Str("pass_hash");
    
    if (!H.Verify(d.Password ?? "", storedHash)) 
    {
        return Err("Неверный email или пароль");
    }
    
    return StartSession(email);
});

// выход
app.MapPost("/api/logout", (HttpRequest r) =>
{
    var authHeader = r.Headers["Authorization"].ToString();
    if (authHeader.StartsWith("Bearer ")) 
    {
        string token = authHeader.Substring(7);
        Db.Exec("DELETE FROM sessions WHERE token = @t", new { t = token });
    }
    return Results.Json(new { ok = true });
});

app.MapGet("/api/me", (HttpRequest r) => WithRole(r, H.Any, u => Results.Json(u)));
app.MapGet("/api/categories", () => Results.Json(Db.Q("SELECT id, name FROM categories ORDER BY id")));
app.MapGet("/api/tariffs", () => Results.Json(Db.Q("SELECT id, name, min_days, discount_percent FROM tariffs ORDER BY min_days")));

// каталог товаров
app.MapGet("/api/equipment", (HttpRequest r) =>
{
    var d1 = DateTime.Today;
    var d2 = DateTime.Today;
    
    string fromStr = r.Query["from"].ToString();
    string toStr = r.Query["to"].ToString();
    
    DateTime a, b;
    if (H.D(fromStr, out a) && H.D(toStr, out b)) 
    {
        if (b >= a && (b - a).Days <= 60) 
        {
            d1 = a;
            d2 = b;
        }
    }
    
    var list = Db.Q("SELECT e.*, c.name AS category_name FROM equipment e JOIN categories c ON c.id = e.category_id ORDER BY e.id");
    
    foreach (var item in list) 
    {
        long eqId = item.Int("id");
        item["available"] = H.Available(eqId, d1, d2);
    }
    
    return Results.Json(list);
});

// расчет стоимости
app.MapGet("/api/quote", (HttpRequest r) =>
{
    long id;
    if (!long.TryParse(r.Query["id"].ToString(), out id)) 
    {
        return Err("Оборудование не выбрано");
    }
    
    int qty;
    if (!int.TryParse(r.Query["qty"].ToString(), out qty)) 
    {
        qty = 1;
    }
    
    DateTime d1, d2;
    string msg = H.CheckDates(r.Query["from"].ToString(), r.Query["to"].ToString(), out d1, out d2);
    
    if (msg != null) 
    {
        return Err(msg);
    }
    if (qty < 1) 
    {
        return Err("Количество должно быть больше 0");
    }
    
    var eqList = Db.Q("SELECT * FROM equipment WHERE id = @id", new { id });
    if (eqList.Count == 0) 
    {
        return Err("Оборудование не найдено", 404);
    }
    
    var eq = eqList[0];
    return Results.Json(H.Quote(eq, d1, d2, qty));
});

// создание брони
app.MapPost("/api/bookings", (HttpRequest r, BookDto d) => WithRole(r, H.Any, u =>
{
    DateTime d1, d2;
    string msg = H.CheckDates(d.DateFrom, d.DateTo, out d1, out d2);
    
    if (msg != null) 
    {
        return Err(msg);
    }
    if (d.Qty < 1) 
    {
        return Err("Количество должно быть больше 0");
    }
    
    var eqList = Db.Q("SELECT * FROM equipment WHERE id = @id", new { id = d.EquipmentId });
    if (eqList.Count == 0) 
    {
        return Err("Оборудование не найдено", 404);
    }
    
    var eq = eqList[0];
    var quoteData = H.Quote(eq, d1, d2, d.Qty);
    
    long avail = Convert.ToInt64(quoteData["available"]);
    if (avail < d.Qty) 
    {
        return Err("На выбранные даты доступно только " + avail + " шт.");
    }
    
    double total = Convert.ToDouble(quoteData["total"]);
    double deposit = Convert.ToDouble(quoteData["deposit"]);
    
    Db.Exec(@"INSERT INTO bookings(user_id, equipment_id, qty, date_from, date_to, status, total, deposit, created)
              VALUES(@uid, @eid, @qty, @df, @dt, 'booked', @total, @dep, @c)",
        new { uid = u.Int("id"), eid = d.EquipmentId, qty = d.Qty, df = H.Fmt(d1), dt = H.Fmt(d2), total = total, dep = deposit, c = H.Now() });
        
    return Results.Json(new { ok = true });
}));

// мои брони
app.MapGet("/api/bookings/my", (HttpRequest r) => WithRole(r, H.Any, u =>
{
    long userId = u.Int("id");
    var list = Db.Q(@"SELECT b.*, e.name AS equipment_name, e.image_url, " + H.OverdueSql + @" AS overdue_days
                      FROM bookings b JOIN equipment e ON e.id = b.equipment_id
                      WHERE b.user_id = @uid ORDER BY b.id DESC", new { uid = userId });
    return Results.Json(list);
}));

// отмена брони
app.MapPost("/api/bookings/{id:long}/cancel", (long id, HttpRequest r) => WithRole(r, H.Any, u =>
{
    var bookingList = Db.Q("SELECT * FROM bookings WHERE id = @id", new { id });
    if (bookingList.Count == 0) 
    {
        return Err("Бронь не найдена", 404);
    }
    
    var b = bookingList[0];
    string userRole = u.Str("role");
    
    if (userRole == "client" && b.Int("user_id") != u.Int("id")) 
    {
        return Err("Нет доступа", 403);
    }
    if (b.Str("status") != "booked") 
    {
        return Err("Отменить можно только ещё не выданную бронь");
    }
    
    Db.Exec("UPDATE bookings SET status = 'cancelled' WHERE id = @id", new { id });
    return Results.Json(new { ok = true });
}));

// все брони
app.MapGet("/api/manage/bookings", (HttpRequest r) => WithRole(r, H.Staff, u =>
{
    var list = Db.Q(@"SELECT b.*, u.name AS user_name, u.email AS user_email, e.name AS equipment_name, e.price_per_day, " + H.OverdueSql + @" AS overdue_days
                      FROM bookings b JOIN users u ON u.id = b.user_id JOIN equipment e ON e.id = b.equipment_id
                      ORDER BY b.id DESC LIMIT 500");
    return Results.Json(list);
}));

// выдача
app.MapPost("/api/manage/bookings/{id:long}/issue", (long id, HttpRequest r, IssueDto d) => WithRole(r, H.Staff, u =>
{
    var bookingList = Db.Q("SELECT * FROM bookings WHERE id = @id", new { id });
    if (bookingList.Count == 0) 
    {
        return Err("Бронь не найдена", 404);
    }
    
    var b = bookingList[0];
    if (b.Str("status") != "booked") 
    {
        return Err("Выдать можно только забронированное оборудование");
    }
    
    string dateFrom = b.Str("date_from");
    string dateTo = b.Str("date_to");
    string today = H.Fmt(DateTime.Today);
    
    if (string.CompareOrdinal(dateFrom, today) > 0) 
    {
        return Err("Выдача возможна не раньше даты начала аренды");
    }
    if (string.CompareOrdinal(dateTo, today) < 0) 
    {
        return Err("Срок брони истёк - отмените бронь");
    }
    
    string note = (d.Note ?? "").Trim();
    if (note.Length > 500) 
    {
        return Err("Комментарий слишком длинный");
    }
    
    Db.Exec("UPDATE bookings SET status = 'issued', issue_note = @note WHERE id = @id", new { id, note });
    return Results.Json(new { ok = true });
}));

// возврат
app.MapPost("/api/manage/bookings/{id:long}/return", (long id, HttpRequest r, ReturnDto d) => WithRole(r, H.Staff, u =>
{
    var bookingList = Db.Q("SELECT b.*, e.price_per_day FROM bookings b JOIN equipment e ON e.id = b.equipment_id WHERE b.id = @id", new { id });
    if (bookingList.Count == 0) 
    {
        return Err("Бронь не найдена", 404);
    }
    
    var b = bookingList[0];
    if (b.Str("status") != "issued") 
    {
        return Err("Принять можно только выданное оборудование");
    }
    
    int qty = (int)b.Int("qty");
    
    if (d.DamageFine < 0 || d.DamageFine > 1000000) 
    {
        return Err("Штраф за повреждение - от 0 до 1 000 000");
    }
    if (d.RepairQty < 0 || d.RepairQty > qty) 
    {
        return Err("В ремонт можно отправить от 0 до " + qty + " шт.");
    }
    
    string note = (d.Note ?? "").Trim();
    if (note.Length > 500) 
    {
        return Err("Комментарий слишком длинный");
    }
    
    DateTime dueDate;
    H.D(b.Str("date_to"), out dueDate);
    
    int overdueDays = (DateTime.Today - dueDate).Days;
    if (overdueDays < 0) 
    {
        overdueDays = 0;
    }
    
    double price = b.Dbl("price_per_day");
    double fineOverdue = Math.Round(overdueDays * price * qty * 1.5, 2);
    double fineDamage = Math.Round(d.DamageFine, 2);
    
    Db.Exec("UPDATE bookings SET status = 'returned', return_note = @note, fine_overdue = @fo, fine_damage = @fd WHERE id = @id",
        new { id, note, fo = fineOverdue, fd = fineDamage });
        
    if (d.RepairQty > 0) 
    {
        long eqId = b.Int("equipment_id");
        Db.Exec(@"UPDATE equipment SET repair_qty = MIN(total_qty - written_off_qty, repair_qty + @n) WHERE id = @eid",
            new { n = d.RepairQty, eid = eqId });
    }
    
    return Results.Json(new { ok = true, fineOverdue, overdueDays });
}));

// склад
app.MapGet("/api/stock", (HttpRequest r) => WithRole(r, H.Staff, u =>
{
    var list = Db.Q("SELECT e.*, c.name AS category_name FROM equipment e JOIN categories c ON c.id = e.category_id ORDER BY e.id");
    
    foreach (var item in list) 
    {
        long eqId = item.Int("id");
        int total = (int)item.Int("total_qty");
        int repair = (int)item.Int("repair_qty");
        int off = (int)item.Int("written_off_qty");
        
        int usable = total - repair - off;
        long onHand = Db.Scalar("SELECT COALESCE(SUM(qty), 0) FROM bookings WHERE equipment_id = @id AND status = 'issued'", new { id = eqId });
        
        item["on_hand"] = onHand;
        item["free_now"] = H.Available(eqId, DateTime.Today, DateTime.Today);
        item["usable"] = usable;
    }
    return Results.Json(list);
}));

app.MapPut("/api/stock/{id:long}", (long id, HttpRequest r, StockDto d) => WithRole(r, H.Staff, u =>
{
    long count = Db.Scalar("SELECT COUNT(*) FROM equipment WHERE id = @id", new { id });
    if (count == 0) 
    {
        return Err("Оборудование не найдено", 404);
    }
    
    if (d.Total < 0 || d.Repair < 0 || d.WrittenOff < 0) 
    {
        return Err("Количество не может быть отрицательным");
    }
    if (d.Total > 10000) 
    {
        return Err("Слишком большое количество");
    }
    if (d.Repair + d.WrittenOff > d.Total) 
    {
        return Err("Ремонт + списание не могут превышать общее количество");
    }
    
    string cond = (d.Condition ?? "").Trim();
    if (cond.Length == 0 || cond.Length > 60) 
    {
        return Err("Укажите состояние (до 60 символов)");
    }
    
    Db.Exec("UPDATE equipment SET total_qty = @t, repair_qty = @r, written_off_qty = @w, cond = @c WHERE id = @id",
        new { id, t = d.Total, r = d.Repair, w = d.WrittenOff, c = cond });
        
    return Results.Json(new { ok = true });
}));

// проверка товара
string? CheckEquip(EquipDto d)
{
    if ((d.Name ?? "").Trim().Length < 2) 
    {
        return "Введите название (минимум 2 символа)";
    }
    
    long catCount = Db.Scalar("SELECT COUNT(*) FROM categories WHERE id = @id", new { id = d.CategoryId });
    if (catCount == 0) 
    {
        return "Выберите категорию";
    }
    
    if (d.PricePerDay <= 0 || d.PricePerDay > 1000000) 
    {
        return "Цена за сутки - больше 0";
    }
    if (d.Deposit < 0 || d.Deposit > 10000000) 
    {
        return "Залог не может быть отрицательным";
    }
    if (d.TotalQty < 0 || d.TotalQty > 10000) 
    {
        return "Количество - от 0 до 10000";
    }
    if ((d.ImageUrl ?? "").Length > 1000) 
    {
        return "Ссылка на изображение слишком длинная";
    }
    if ((d.Cond ?? "").Trim().Length > 60) 
    {
        return "Состояние - до 60 символов";
    }
    
    return null;
}

// добавление товара
app.MapPost("/api/admin/equipment", (HttpRequest r, EquipDto d) => WithRole(r, H.AdminOnly, u =>
{
    string msg = CheckEquip(d);
    if (msg != null) 
    {
        return Err(msg);
    }
    
    string name = d.Name.Trim();
    string desc = (d.Description ?? "").Trim();
    string img = (d.ImageUrl ?? "").Trim();
    string cond = string.IsNullOrWhiteSpace(d.Cond) ? "Хорошее" : d.Cond.Trim();
    
    Db.Exec(@"INSERT INTO equipment(name, category_id, description, image_url, price_per_day, deposit, total_qty, cond)
              VALUES(@n, @c, @ds, @im, @p, @dp, @t, @cd)",
        new { n = name, c = d.CategoryId, ds = desc, im = img, p = d.PricePerDay, dp = d.Deposit, t = d.TotalQty, cd = cond });
        
    return Results.Json(new { ok = true });
}));

// изменение товара
app.MapPut("/api/admin/equipment/{id:long}", (long id, HttpRequest r, EquipDto d) => WithRole(r, H.AdminOnly, u =>
{
    string msg = CheckEquip(d);
    if (msg != null) 
    {
        return Err(msg);
    }
    
    var curList = Db.Q("SELECT repair_qty, written_off_qty FROM equipment WHERE id = @id", new { id });
    if (curList.Count == 0) 
    {
        return Err("Оборудование не найдено", 404);
    }
    
    var cur = curList[0];
    int currentRepair = (int)cur.Int("repair_qty");
    int currentOff = (int)cur.Int("written_off_qty");
    
    if (currentRepair + currentOff > d.TotalQty) 
    {
        return Err("Общее количество меньше, чем в ремонте и списано");
    }
    
    string name = d.Name.Trim();
    string desc = (d.Description ?? "").Trim();
    string img = (d.ImageUrl ?? "").Trim();
    string cond = string.IsNullOrWhiteSpace(d.Cond) ? "Хорошее" : d.Cond.Trim();
    
    Db.Exec(@"UPDATE equipment SET name = @n, category_id = @c, description = @ds, image_url = @im, price_per_day = @p, deposit = @dp, total_qty = @t, cond = @cd WHERE id = @id",
        new { id, n = name, c = d.CategoryId, ds = desc, im = img, p = d.PricePerDay, dp = d.Deposit, t = d.TotalQty, cd = cond });
        
    return Results.Json(new { ok = true });
}));

// удаление товара
app.MapDelete("/api/admin/equipment/{id:long}", (long id, HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    long bookingsCount = Db.Scalar("SELECT COUNT(*) FROM bookings WHERE equipment_id = @id", new { id });
    if (bookingsCount > 0) 
    {
        return Err("У позиции есть история аренд - спишите её в разделе «Склад» вместо удаления");
    }
    
    Db.Exec("DELETE FROM equipment WHERE id = @id", new { id });
    return Results.Json(new { ok = true });
}));

// добавление категории
app.MapPost("/api/admin/categories", (HttpRequest r, CatDto d) => WithRole(r, H.AdminOnly, u =>
{
    string name = (d.Name ?? "").Trim();
    if (name.Length < 2 || name.Length > 40) 
    {
        return Err("Название категории - от 2 до 40 символов");
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM categories WHERE name = @name", new { name });
    if (count > 0) 
    {
        return Err("Такая категория уже есть");
    }
    
    Db.Exec("INSERT INTO categories(name) VALUES(@name)", new { name });
    return Results.Json(new { ok = true });
}));

// тарифы
app.MapPost("/api/admin/tariffs", (HttpRequest r, TariffDto d) => WithRole(r, H.AdminOnly, u =>
{
    string name = (d.Name ?? "").Trim();
    if (name.Length < 2 || name.Length > 40) 
    {
        return Err("Название тарифа - от 2 до 40 символов");
    }
    if (d.MinDays < 2 || d.MinDays > 30) 
    {
        return Err("Минимальный срок - от 2 до 30 суток");
    }
    if (d.DiscountPercent <= 0 || d.DiscountPercent > 70) 
    {
        return Err("Скидка - от 1 до 70 %");
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM tariffs WHERE min_days = @m", new { m = d.MinDays });
    if (count > 0) 
    {
        return Err("Тариф на этот срок уже существует");
    }
    
    Db.Exec("INSERT INTO tariffs(name, min_days, discount_percent) VALUES(@name, @m, @p)", new { name, m = d.MinDays, p = d.DiscountPercent });
    return Results.Json(new { ok = true });
}));

app.MapDelete("/api/admin/tariffs/{id:long}", (long id, HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    Db.Exec("DELETE FROM tariffs WHERE id = @id", new { id });
    return Results.Json(new { ok = true });
}));

// пользователи админ
app.MapGet("/api/admin/users", (HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    var users = Db.Q("SELECT id, name, email, role, created FROM users ORDER BY id");
    return Results.Json(users);
}));

app.MapPost("/api/admin/users", (HttpRequest r, UserDto d) => WithRole(r, H.AdminOnly, u =>
{
    string name = (d.Name ?? "").Trim();
    string email = (d.Email ?? "").Trim().ToLowerInvariant();
    string role = d.Role ?? "";
    
    if (name.Length < 2) 
    {
        return Err("Введите имя (минимум 2 символа)");
    }
    if (!H.ValidEmail(email)) 
    {
        return Err("Некорректный email");
    }
    if ((d.Password ?? "").Length < 6) 
    {
        return Err("Пароль - минимум 6 символов");
    }
    if (role != "client" && role != "manager" && role != "admin") 
    {
        return Err("Некорректная роль");
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM users WHERE email = @email", new { email });
    if (count > 0) 
    {
        return Err("Этот email уже зарегистрирован");
    }
    
    Db.Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES(@name, @email, @h, @role, @c)",
        new { name, email, h = H.Hash(d.Password), role, c = H.Now() });
        
    return Results.Json(new { ok = true });
}));

app.MapPut("/api/admin/users/{id:long}/role", (long id, HttpRequest r, RoleDto d) => WithRole(r, H.AdminOnly, u =>
{
    string role = d.Role ?? "";
    if (role != "client" && role != "manager" && role != "admin") 
    {
        return Err("Некорректная роль");
    }
    
    if (id == u.Int("id")) 
    {
        return Err("Нельзя изменить роль самому себе");
    }
    
    Db.Exec("UPDATE users SET role = @role WHERE id = @id", new { id, role });
    return Results.Json(new { ok = true });
}));

app.MapDelete("/api/admin/users/{id:long}", (long id, HttpRequest r) => WithRole(r, H.AdminOnly, u =>
{
    if (id == u.Int("id")) 
    {
        return Err("Нельзя удалить самого себя");
    }
    
    long count = Db.Scalar("SELECT COUNT(*) FROM bookings WHERE user_id = @id", new { id });
    if (count > 0) 
    {
        return Err("У пользователя есть история аренд - удаление невозможно");
    }
    
    Db.Exec("DELETE FROM users WHERE id = @id", new { id });
    return Results.Json(new { ok = true });
}));

// статистика
app.MapGet("/api/stats", (HttpRequest r) => WithRole(r, H.Staff, u =>
{
    double revenue = Db.Num("SELECT COALESCE(SUM(total + fine_overdue + fine_damage), 0) FROM bookings WHERE status = 'returned'");
    double fines = Db.Num("SELECT COALESCE(SUM(fine_overdue + fine_damage), 0) FROM bookings WHERE status = 'returned'");
    long issued = Db.Scalar("SELECT COUNT(*) FROM bookings WHERE status = 'issued'");
    long booked = Db.Scalar("SELECT COUNT(*) FROM bookings WHERE status = 'booked'");
    long overdue = Db.Scalar("SELECT COUNT(*) FROM bookings b WHERE " + H.OverdueSql + " > 0");
    long total = Db.Scalar("SELECT COUNT(*) FROM bookings WHERE status != 'cancelled'");
    
    var months = Db.Q(@"SELECT substr(date_from, 1, 7) AS m, SUM(total + fine_overdue + fine_damage) AS sum, COUNT(*) AS cnt
                        FROM bookings WHERE status IN ('returned', 'issued') GROUP BY m ORDER BY m DESC LIMIT 6");
    months.Reverse();
    
    var cats = Db.Q(@"SELECT c.name, COUNT(b.id) AS cnt, COALESCE(SUM(b.total), 0) AS sum
                      FROM categories c LEFT JOIN equipment e ON e.category_id = c.id
                      LEFT JOIN bookings b ON b.equipment_id = e.id AND b.status != 'cancelled'
                      GROUP BY c.id ORDER BY c.id");
                      
    var top = Db.Q(@"SELECT e.name, COUNT(b.id) AS cnt FROM bookings b JOIN equipment e ON e.id = b.equipment_id
                     WHERE b.status != 'cancelled' GROUP BY e.id ORDER BY cnt DESC, e.id LIMIT 6");
                     
    return Results.Json(new { revenue, fines, issued, booked, overdue, total, months, cats, top });
}));

// запуск браузера
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        string url = "http://localhost:5080";
        if (OperatingSystem.IsWindows()) 
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (OperatingSystem.IsMacOS()) 
        {
            Process.Start("open", url);
        }
        else 
        {
            Process.Start("xdg-open", url);
        }
    }
    catch
    {
    }
});

app.Run();

// модели данных
record RegisterDto(string? Name, string? Email, string? Password);
record LoginDto(string? Email, string? Password);
record BookDto(long EquipmentId, int Qty, string? DateFrom, string? DateTo);
record IssueDto(string? Note);
record ReturnDto(string? Note, double DamageFine, int RepairQty);
record StockDto(int Total, int Repair, int WrittenOff, string? Condition);
record EquipDto(string? Name, long CategoryId, string? Description, string? ImageUrl, double PricePerDay, double Deposit, int TotalQty, string? Cond);
record CatDto(string? Name);
record TariffDto(string? Name, int MinDays, double DiscountPercent);
record UserDto(string? Name, string? Email, string? Password, string? Role);
record RoleDto(string? Role);

// вспомогательный класс для словарей
static class X
{
    public static string Str(this Dictionary<string, object?> d, string k) 
    {
        var val = d[k];
        if (val == null) return "";
        return Convert.ToString(val, CultureInfo.InvariantCulture) ?? "";
    }
    
    public static long Int(this Dictionary<string, object?> d, string k) 
    {
        var val = d[k];
        if (val == null) return 0;
        return Convert.ToInt64(val, CultureInfo.InvariantCulture);
    }
    
    public static double Dbl(this Dictionary<string, object?> d, string k) 
    {
        var val = d[k];
        if (val == null) return 0;
        return Convert.ToDouble(val, CultureInfo.InvariantCulture);
    }
}

// вспомогательные методы
static class H
{
    public static readonly string[] Any = Array.Empty<string>();
    public static readonly string[] Staff = new[] { "manager", "admin" };
    public static readonly string[] AdminOnly = new[] { "admin" };

    public const string OverdueSql = "(CASE WHEN b.status = 'issued' AND b.date_to < date('now', 'localtime') THEN CAST(julianday(date('now', 'localtime')) - julianday(b.date_to) AS INTEGER) ELSE 0 END)";

    public static string FindRoot()
    {
        string[] starts = new string[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            var d = new DirectoryInfo(start);
            while (d != null)
            {
                string wwwrootPath = Path.Combine(d.FullName, "wwwroot");
                string csprojPath = Path.Combine(d.FullName, "RentalApp.csproj");
                
                if (Directory.Exists(wwwrootPath) && File.Exists(csprojPath))
                {
                    return d.FullName;
                }
                d = d.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }

    public static string Now()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string Fmt(DateTime d)
    {
        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    public static bool D(string? s, out DateTime d)
    {
        return DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }

    public static bool ValidEmail(string e)
    {
        if (e.Length < 5 || e.Length > 100) 
        {
            return false;
        }
        
        int at = e.IndexOf('@');
        if (at <= 0) 
        {
            return false;
        }
        if (at != e.LastIndexOf('@')) 
        {
            return false;
        }
        if (e.IndexOf('.', at) <= at + 1) 
        {
            return false;
        }
        if (e.EndsWith(".") || e.Contains(' ')) 
        {
            return false;
        }
        
        return true;
    }

    public static string Hash(string pw)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(key);
    }

    public static bool Verify(string pw, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) 
        {
            return false;
        }
        
        var salt = Convert.FromBase64String(parts[0]);
        var key = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100000, HashAlgorithmName.SHA256, 32);
        var storedKey = Convert.FromBase64String(parts[1]);
        
        return CryptographicOperations.FixedTimeEquals(key, storedKey);
    }

    public static string? CheckDates(string? a, string? b, out DateTime d1, out DateTime d2)
    {
        d2 = default;
        if (!D(a, out d1) || !D(b, out d2)) 
        {
            return "Укажите корректные даты";
        }
        if (d1 < DateTime.Today) 
        {
            return "Дата начала не может быть в прошлом";
        }
        if (d2 < d1) 
        {
            return "Дата окончания раньше даты начала";
        }
        if ((d2 - d1).Days + 1 > 30) 
        {
          return "Максимальный срок аренды - 30 суток";
        }
        return null;
    }

    // расчет доступного количества
    public static long Available(long eqId, DateTime d1, DateTime d2)
    {
        var eqList = Db.Q("SELECT total_qty, repair_qty, written_off_qty FROM equipment WHERE id = @id", new { id = eqId });
        if (eqList.Count == 0) 
        {
            return 0;
        }
        
        var eq = eqList[0];
        int total = (int)eq.Int("total_qty");
        int repair = (int)eq.Int("repair_qty");
        int off = (int)eq.Int("written_off_qty");
        
        long usable = total - repair - off;
        string today = Fmt(DateTime.Today);
        
        var bs = Db.Q(@"SELECT qty, status, date_from, date_to FROM bookings
                        WHERE equipment_id = @id AND status IN ('booked', 'issued') AND date_from <= @b AND date_to >= @a",
            new { id = eqId, a = Fmt(d1), b = Fmt(d2) });
            
        var overdueOnHand = Db.Q(@"SELECT qty, date_from FROM bookings
                                   WHERE equipment_id = @id AND status = 'issued' AND date_to < @t AND date_to < @a",
            new { id = eqId, t = today, a = Fmt(d1) });
            
        long maxUsed = 0;
        for (var d = d1; d <= d2; d = d.AddDays(1))
        {
            string ds = Fmt(d);
            long used = 0;
            
            foreach (var x in bs)
            {
                string end = x.Str("date_to");
                if (x.Str("status") == "issued" && string.CompareOrdinal(end, today) < 0) 
                {
                    end = today;
                }
                
                if (string.CompareOrdinal(x.Str("date_from"), ds) <= 0 && string.CompareOrdinal(end, ds) >= 0) 
                {
                    used += x.Int("qty");
                }
            }
            
            if (string.CompareOrdinal(ds, today) >= 0)
            {
                foreach (var x in overdueOnHand) 
                {
                    used += x.Int("qty");
                }
            }
            
            if (used > maxUsed) 
            {
                maxUsed = used;
            }
        }
        
        long result = usable - maxUsed;
        if (result < 0) 
        {
            result = 0;
        }
        return result;
    }

    // расчет стоимости
    public static Dictionary<string, object?> Quote(Dictionary<string, object?> eq, DateTime d1, DateTime d2, int qty)
    {
        int days = (d2 - d1).Days + 1;
        double price = eq.Dbl("price_per_day");
        double baseSum = price * qty * days;
        
        var tariffList = Db.Q("SELECT discount_percent FROM tariffs WHERE min_days <= @d ORDER BY discount_percent DESC LIMIT 1", new { d = days });
        double disc = 0;
        if (tariffList.Count > 0) 
        {
            disc = tariffList[0].Dbl("discount_percent");
        }
        
        double totalSum = Math.Round(baseSum * (1 - disc / 100), 2);
        double depositSum = Math.Round(eq.Dbl("deposit") * qty, 2);
        long avail = Available(eq.Int("id"), d1, d2);
        
        var result = new Dictionary<string, object?>();
        result["days"] = days;
        result["base"] = Math.Round(baseSum, 2);
        result["discount"] = disc;
        result["total"] = totalSum;
        result["deposit"] = depositSum;
        result["available"] = avail;
        
        return result;
    }
}

// работа с базой данных
static class Db
{
    public static string DbFile = "";

    static SqliteConnection Open()
    {
        var c = new SqliteConnection("Data Source=" + DbFile);
        c.Open();
        using var m = c.CreateCommand();
        m.CommandText = "PRAGMA foreign_keys = ON;";
        m.ExecuteNonQuery();
        return c;
    }

    static void Bind(SqliteCommand m, object? p)
    {
        if (p == null) 
        {
            return;
        }
        
        foreach (var pr in p.GetType().GetProperties())
        {
            var val = pr.GetValue(p);
            if (val == null) 
            {
                val = DBNull.Value;
            }
            m.Parameters.AddWithValue("@" + pr.Name, val);
        }
    }

    public static List<Dictionary<string, object?>> Q(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        using var r = m.ExecuteReader();
        
        var list = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++) 
            {
                if (r.IsDBNull(i)) 
                {
                    row[r.GetName(i)] = null;
                }
                else 
                {
                    row[r.GetName(i)] = r.GetValue(i);
                }
            }
            list.Add(row);
        }
        return list;
    }

    public static int Exec(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        return m.ExecuteNonQuery();
    }

    public static long Scalar(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        var o = m.ExecuteScalar();
        
        if (o == null || o is DBNull) 
        {
            return 0;
        }
        return Convert.ToInt64(o, CultureInfo.InvariantCulture);
    }

    public static double Num(string sql, object? p = null)
    {
        using var c = Open();
        using var m = c.CreateCommand();
        m.CommandText = sql;
        Bind(m, p);
        var o = m.ExecuteScalar();
        
        if (o == null || o is DBNull) 
        {
            return 0;
        }
        return Convert.ToDouble(o, CultureInfo.InvariantCulture);
    }

    public static void Init()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS users(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  email TEXT NOT NULL UNIQUE,
  pass_hash TEXT NOT NULL,
  role TEXT NOT NULL CHECK(role IN ('client', 'manager', 'admin')),
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS sessions(
  token TEXT PRIMARY KEY,
  user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  created TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS categories(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL UNIQUE
);
CREATE TABLE IF NOT EXISTS equipment(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  category_id INTEGER NOT NULL REFERENCES categories(id),
  description TEXT NOT NULL DEFAULT '',
  image_url TEXT NOT NULL DEFAULT '',
  price_per_day REAL NOT NULL CHECK(price_per_day > 0),
  deposit REAL NOT NULL DEFAULT 0 CHECK(deposit >= 0),
  total_qty INTEGER NOT NULL CHECK(total_qty >= 0),
  repair_qty INTEGER NOT NULL DEFAULT 0 CHECK(repair_qty >= 0),
  written_off_qty INTEGER NOT NULL DEFAULT 0 CHECK(written_off_qty >= 0),
  cond TEXT NOT NULL DEFAULT 'Хорошее',
  CHECK(repair_qty + written_off_qty <= total_qty)
);
CREATE TABLE IF NOT EXISTS tariffs(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  min_days INTEGER NOT NULL UNIQUE CHECK(min_days >= 2),
  discount_percent REAL NOT NULL CHECK(discount_percent > 0 AND discount_percent <= 70)
);
CREATE TABLE IF NOT EXISTS bookings(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id INTEGER NOT NULL REFERENCES users(id),
  equipment_id INTEGER NOT NULL REFERENCES equipment(id),
  qty INTEGER NOT NULL CHECK(qty > 0),
  date_from TEXT NOT NULL,
  date_to TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'booked' CHECK(status IN ('booked', 'issued', 'returned', 'cancelled')),
  total REAL NOT NULL,
  deposit REAL NOT NULL DEFAULT 0,
  issue_note TEXT NOT NULL DEFAULT '',
  return_note TEXT NOT NULL DEFAULT '',
  fine_overdue REAL NOT NULL DEFAULT 0,
  fine_damage REAL NOT NULL DEFAULT 0,
  created TEXT NOT NULL,
  CHECK(date_to >= date_from)
);");
        
        long userCount = Scalar("SELECT COUNT(*) FROM users");
        if (userCount == 0) 
        {
            Seed();
        }
    }

    static void Seed()
    {
        var now = H.Now();
        
        // создание пользователей
        Db.Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES('Администратор', 'admin@mail.ru', @h, 'admin', @c)", new { h = H.Hash("admin123"), c = now });
        Db.Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES('Мишель Крылова', 'manager@mail.ru', @h, 'manager', @c)", new { h = H.Hash("manager123"), c = now });
        Db.Exec("INSERT INTO users(name, email, pass_hash, role, created) VALUES('Марк Светаев', 'client@mail.ru', @h, 'client', @c)", new { h = H.Hash("client123"), c = now });

        Db.Exec("INSERT INTO categories(name) VALUES('Спорт'), ('Туризм'), ('Строительство')");
        Db.Exec("INSERT INTO tariffs(name, min_days, discount_percent) VALUES('Выходные', 3, 5), ('Неделя', 7, 10), ('Две недели', 14, 15)");

        var items = new (string Name, int Cat, string Desc, string Img, double Price, double Dep, int Qty, int Repair, int Off, string Cond)[]
        {
            ("Горный велосипед", 1, "Алюминиевая рама, 21 скорость, дисковые тормоза. Шлем в комплекте.", "https://th.bing.com/th/id/OIP.pWamWPsvOh1FGNdwFaGmcAHaEm?r=0&o=7rm=3&rs=1&pid=ImgDetMain&o=7&rm=3", 900, 5000, 6, 0, 0, "Отличное"),
            ("Сноуборд с креплениями", 1, "Универсальный сноуборд 155 см для катания по трассе и парку.", "https://tse3.mm.bing.net/th/id/OIP.aBScvW2JexjB2kYrlXmdqgHaHa?r=0&rs=1&pid=ImgDetMain&o=7&rm=3", 700, 4000, 5, 0, 0, "Хорошее"),
            ("Беговые лыжи, комплект", 1, "Лыжи с палками и ботинками, подбор по росту и размеру.", "https://50.img.avito.st/image/1/1.HEwmvra5sKUQF3KgEvo0Hswdtq-Snbhnlx2yoZoXuqc.___XaB8Z2xWn8QGVpVfTMhq1hI3OmR0n2GnGI41oiTI", 500, 3000, 8, 0, 0, "Хорошее"),
            ("Каяк двухместный", 1, "Надувной двухместный каяк с вёслами, насосом и спасжилетами.", "https://openmark.ru/UserFiles/Image/Lodki/Naduvnye-lodki/img711_94185.jpg", 1500, 8000, 3, 1, 0, "Хорошее"),
            ("Палатка 4-местная", 2, "Трёхсезонная палатка с тамбуром, вес 5,2 кг, водостойкость 3000 мм.", "https://ir.ozone.ru/s3/multimedia-1-b/c1000/6987625139.jpg", 600, 3000, 7, 0, 0, "Отличное"),
            ("Спальный мешок до −15 °C", 2, "Тёплый спальный мешок-кокон с компрессионным чехлом.", "https://ir.ozone.ru/s3/multimedia-1-6/c1000/8772581598.jpg", 250, 1000, 12, 0, 0, "Хорошее"),
            ("Рюкзак туристический 70 л", 2, "Анатомическая спинка, дождевик, боковые карманы для бутылок.", "https://tse2.mm.bing.net/th/id/OIP.17uBPpoq255VCCvYH2372gHaHa?r=0&rs=1&pid=ImgDetMain&o=7&rm=3", 300, 1500, 9, 0, 0, "Хорошее"),
            ("Газовая горелка с набором", 2, "Горелка, котелок и кружки на двоих. Баллон оплачивается отдельно.", "https://japan-power.ru/image/cache/catalog/2025/gorelka-soto-amicus-cooker-combo-700x500.jpg", 150, 800, 10, 0, 0, "Хорошее"),
            ("Перфоратор", 3, "Мощность 900 Вт, режимы сверления и долбления, кейс и набор буров.", "https://tse3.mm.bing.net/th/id/OIP.OJ86UvLEQj_aENSv_UG1MQHaFY?r=0&rs=1&pid=ImgDetMain&o=7&rm=3", 800, 4000, 5, 0, 1, "Хорошее"),
            ("Бензопила", 3, "Шина 40 см, мощность 2,2 кВт. Масло и бензин оплачиваются отдельно.", "https://tse4.mm.bing.net/th/id/OIP.0FlsjYw8KIniGMStscWdKQHaD6?r=0&rs=1&pid=ImgDetMain&o=7&rm=3", 1000, 6000, 3, 0, 0, "Отличное"),
            ("Строительные леса, секция", 3, "Стальная секция 2×1 м, нагрузка до 200 кг. Продаётся комплектами.", "https://tse1.mm.bing.net/th/id/OIP.mXXClvwbYY7l2UUc4w6ygQHaEg?r=0&rs=1&pid=ImgDetMain&o=7&rm=3", 400, 2000, 10, 0, 0, "Хорошее"),
            ("Бетономешалка 160 л", 3, "Электрическая, барабан 160 л, колёса для перемещения по участку.", "https://tse1.mm.bing.net/th/id/OIP.4gF6nxfTBkpKw3ft8p7gIwHaHZ?r=0&rs=1&pid=ImgDetMain&o=7&rm=3", 900, 5000, 2, 0, 0, "Удовлетворительное")
        };

        // заполнение каталога
        foreach (var i in items)
        {
            Db.Exec(@"INSERT INTO equipment(name, category_id, description, image_url, price_per_day, deposit, total_qty, repair_qty, written_off_qty, cond)
                   VALUES(@n, @c, @d, @i, @p, @dp, @q, @r, @o, @cd)",
                new { n = i.Name, c = i.Cat, d = i.Desc, i = i.Img, p = i.Price, dp = i.Dep, q = i.Qty, r = i.Repair, o = i.Off, cd = i.Cond });
        }

        var rnd = new Random(7);
        var today = DateTime.Today;
        var prices = new double[items.Length];
        var deps = new double[items.Length];
        for (int k = 0; k < items.Length; k++) 
        {
            prices[k] = items[k].Price;
            deps[k] = items[k].Dep;
        }

        // тестовые брони
        void Add(int eq, int qty, DateTime begin, int days, string status, double fineOver = 0, double fineDmg = 0)
        {
            var to = begin.AddDays(days - 1);
            double tot = prices[eq - 1] * qty * days;
            double dep = deps[eq - 1] * qty;
            
            Db.Exec(@"INSERT INTO bookings(user_id, equipment_id, qty, date_from, date_to, status, total, deposit, fine_overdue, fine_damage, created)
                   VALUES(3, @e, @q, @f, @t, @s, @tot, @dep, @fo, @fd, @c)",
                new { e = eq, q = qty, f = H.Fmt(begin), t = H.Fmt(to), s = status, tot = tot, dep = dep, fo = fineOver, fd = fineDmg, c = H.Now() });
        }

        for (var m = 5; m >= 1; m--)
        {
            var start = new DateTime(today.Year, today.Month, 1).AddMonths(-m);
            var count = 3 + rnd.Next(0, 4);
            for (var k = 0; k < count; k++)
            {
                int eq = rnd.Next(1, 13);
                int qty = rnd.Next(1, 3);
                int days = rnd.Next(2, 7);
                double fine = 0;
                if (rnd.Next(0, 6) == 0) 
                {
                    fine = 600;
                }
                double dmg = 0;
                if (rnd.Next(0, 8) == 0) 
                {
                    dmg = 1500;
                }
                
                Add(eq, qty, start.AddDays(rnd.Next(0, 22)), days, "returned", fine, dmg);
            }
        }
        
        Add(5, 1, today.AddDays(-14), 4, "returned");
        Add(1, 2, today.AddDays(-10), 3, "returned", 0, 500);
        Add(9, 1, today.AddDays(-6), 3, "issued");
        Add(5, 1, today.AddDays(-1), 4, "issued");
        Add(2, 1, today.AddDays(2), 3, "booked");
    }
}