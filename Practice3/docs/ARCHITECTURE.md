# Архитектура и ревью

## Решение
ASP.NET Core 8 (minimal API) + C#; **модульный монолит со слоистым делением внутри модулей**; EF Core + SQLite; JWT; MSTest + Moq.
Выбор: единая кодовая база на C#, без интеграции Python; нет сетевых вызовов между компонентами.

## Модули и слои
```
Modules/
  Identity/       Domain (User) · Application (IdentityService, порты) · Infrastructure (EF, PBKDF2, JWT) · IdentityModule (Api + DI)
  Subscriptions/  Domain · Application (SubscriptionService) · Contracts (ISubscriptionsApi) · Infrastructure · SubscriptionsModule
  Payments/       Domain · Application (PaymentService) · Contracts (IPaymentsApi) · Infrastructure · PaymentsModule
  Analytics/      Application (AnalyticsService) · AnalyticsModule
Shared/           ошибки -> HTTP-коды, ValidationErrors, IClock, AppDbContext
```
Зависимости: Api → Application → Domain; Infrastructure реализует порты Application (инверсия зависимостей).
Между модулями — только через `Contracts` (Payments → `ISubscriptionsApi`, Analytics → `ISubscriptionsApi` + `IPaymentsApi`); проверяется тестом `ArchitectureTests`.
Модель данных соответствует рис. 2 практики 2: User 1—* Subscription 1—* Payment. Внешних ключей между модулями нет (целостность — через контракты), история платежей не удаляется (мягкое удаление подписок).

## Соответствие требованиям
| Требование | Реализация |
|---|---|
| FR-01/02 | `IdentityService.Register/Login`, уникальный индекс email + проверка |
| FR-03 | `GET/PATCH /api/users/me` |
| FR-04 | `SubscriptionService` CRUD, пагинация |
| FR-05 | `PaymentService`, `GET /api/payments`, запись платежа |
| FR-06 | `AnalyticsService` (по валютам и подпискам) |
| FR-07 | проверка владельца в `RequireOwnedAsync` -> 403 |
| FR-08 | правила валидации в Application -> 422 |
| NFR-01 | PBKDF2-SHA256, соль, 100 000 итераций; единое сообщение при неверном логине/пароле |
| NFR-02 | запись в SQLite через EF Core транзакционно (`SaveChanges`) |
| NFR-03 | уникальный индекс `ExternalId` + идемпотентный повтор (200) + обработка гонки |
| NFR-05 | бизнес-логика за интерфейсами, без зависимости от HTTP/EF; юнит-тесты на фейках и Moq |
| NFR-06 | `docs/API.md`, Swagger |

## Ревью сгенерированного кода: решения и известные ограничения
1. **403 вместо 404 для чужих ресурсов** — следует списку кодов ТЗ; раскрывает факт существования id, но id — случайные GUID.
2. **Добавлена точка `POST /api/subscriptions/{id}/payments`** — вне таблицы 2; нужна, чтобы платежи вообще появлялись и проверялась идемпотентность.
3. **`EnsureCreated()` вместо миграций** — достаточно для учебного проекта; в production нужны миграции EF Core.
4. **SQLite и decimal** — суммы агрегируются в памяти (SQLite не умеет SUM по decimal); для больших объёмов нужен PostgreSQL и агрегация в SQL.
5. **Один `AppDbContext` на все модули** — упрощение; строгая изоляция потребовала бы отдельных контекстов/схем.
6. **JWT-ключ** в `appsettings.json` — только для разработки; в Production приложение не стартует с этим ключом (`Jwt__Key` через переменную окружения).
7. **Нет** refresh-токенов, блокировки перебора паролей (rate limiting), подтверждения email, конвертации валют.
8. Валюта платежа не обязана совпадать с валютой подписки (в ТЗ такого ограничения нет).

## Тестовая стратегия
- Юнит-тесты `Application` (фейки репозиториев + Moq для хешера/токенов) — цель покрытия 90%/80%.
- Архитектурные тесты (границы модулей, чистота Domain).
- Сквозные тесты по HTTP (`WebApplicationFactory`, временная SQLite) — автоматический аналог ручных смоук-тестов; ручные — `scripts/smoke.*`.
