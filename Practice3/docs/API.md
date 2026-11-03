# Документация API (NFR-06)

Базовый адрес: `http://localhost:5080`. Форматы: JSON (camelCase), даты `yyyy-MM-dd`. Интерактивная версия: `/swagger`.
Защищённые ресурсы: заголовок `Authorization: Bearer <accessToken>` (JWT из `/api/auth/login`, срок 60 минут).

## Единый формат ошибок
`application/json`, без внутренних деталей сервера:
```json
{ "status": 422, "title": "Validation failed", "detail": "Validation failed.",
  "errors": { "amount": ["Amount must be between 0 and 1000000000."] } }
```
| Код | Когда |
|---|---|
| 200 / 201 | успех / создание (201 + тело созданного ресурса) |
| 400 | неразбираемый JSON, неверный формат даты/GUID в запросе |
| 401 | нет/неверный/просроченный токен; неверный email или пароль |
| 403 | ресурс существует, но принадлежит другому пользователю (FR-07) |
| 404 | ресурс не найден или подписка удалена |
| 409 | email уже занят; `externalId` платежа уже принадлежит другой подписке |
| 422 | ошибка валидации (поле `errors`) |
| 500 | внутренняя ошибка (подробности только в логе) |

## Endpoint-ы
| Метод | Путь | Тело / параметры | Успех |
|---|---|---|---|
| POST | `/api/auth/register` | `{email, password}` (пароль 8–128 символов) | 201 `{id, email}` |
| POST | `/api/auth/login` | `{email, password}` | 200 `{accessToken, tokenType:"Bearer", expiresAtUtc}` |
| GET | `/api/users/me` | — | 200 `{id, email}` |
| PATCH | `/api/users/me` | `{email?, newPassword?, currentPassword?}`; для смены пароля нужен `currentPassword` | 200 `{id, email}` |
| GET | `/api/subscriptions` | query: `status?` (Active/Paused/Cancelled), `page?`=1, `pageSize?`=20 (1–100) | 200 `{items, page, pageSize, total}` |
| POST | `/api/subscriptions` | `{name, amount, currency, period, startDate}` | 201 подписка |
| GET | `/api/subscriptions/{id}` | — | 200 подписка |
| PATCH | `/api/subscriptions/{id}` | любое из `name, amount, currency, period, startDate, status` | 200 подписка |
| DELETE | `/api/subscriptions/{id}` | — | 200 `{id, status:"Deleted"}` (мягкое удаление) |
| GET | `/api/payments` | query: `subscriptionId?`, `dateFrom?`, `dateTo?` | 200 массив платежей, новые сверху |
| **POST** | **`/api/subscriptions/{id}/payments`** | `{amount, currency, paymentDate, externalId, status?}` | 201 создан / **200 повтор того же `externalId`** |
| GET | `/api/analytics/expenses` | query: `dateFrom`, `dateTo` (обязательны) | 200 отчёт |

**Жирным** — точка, добавленная к таблице 2 ТЗ: без записи платежей нельзя проверить FR-05 и NFR-03.

Подписка: `{id, name, amount, currency, period, startDate, status}`.
Платёж: `{id, subscriptionId, amount, currency, paymentDate, status, externalId}`.

Отчёт расходов:
```json
{ "dateFrom": "2026-01-01", "dateTo": "2026-03-31",
  "totals": [ { "currency": "USD", "total": 20.0 } ],
  "bySubscription": [ { "subscriptionId": "…", "name": "Netflix", "currency": "USD", "total": 20.0, "paymentsCount": 2 } ],
  "paymentsCount": 2 }
```

## Правила данных
- `name` 1–100 символов; `amount` 0…1 000 000 000 (отрицательные запрещены); `currency` — 3 латинские буквы (регистр нормализуется в верхний); `period` ∈ Weekly, Monthly, Quarterly, Yearly (без учёта регистра); `startDate`/`paymentDate` обязательны.
- Платёж `status` ∈ Pending, Succeeded (по умолчанию), Failed; в расходы входят только Succeeded; границы периода включительно.
- Суммы в разных валютах не складываются: `totals` даётся по каждой валюте.
- Удаление подписки не удаляет платежи: история и аналитика сохраняются; платёж в удалённую подписку записать нельзя (404).
- Email хранится в нижнем регистре, уникален; пароль хранится только как PBKDF2-хеш.
