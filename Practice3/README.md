# Практика 3 — проект и команды

Состав:
- `part1/` — Часть 1: `Part1.Lib` (исходники препода: PrimesAmount, OrderProcessingService) и `Part1.Tests` (мои тесты, MSTest + Moq).
- `src/SubscriptionManager.Api` — Часть 2: backend подписок, **ASP.NET Core 8, модульный монолит со слоями**.
- `tests/SubscriptionManager.Tests` — юнит-тесты бизнес-логики, архитектурные тесты и сквозные smoke-тесты по HTTP.
- `scripts/smoke.sh`, `scripts/smoke.ps1` — ручные смоук-тесты через curl / PowerShell.
- `docs/` — `API.md` (контракты), `ARCHITECTURE.md` (архитектура и ревью), `PART1_REVIEW.md` (ответы на задания 1–4 части 1).

> Проект написан без возможности собрать его в моей среде (там нет .NET SDK и доступа к NuGet). Первый запуск — по шагам ниже; если `dotnet build` покажет ошибку, пришли текст ошибки, и я поправлю.

## 0. Требования
.NET SDK 8 или новее (`dotnet --version`). Если стоит только SDK 9/10, сборка пройдёт, а запуск потребует:
`setx DOTNET_ROLL_FORWARD Major` (PowerShell: `$env:DOTNET_ROLL_FORWARD="Major"`) либо замени `net8.0` на свою версию во всех `.csproj`.

## 1. Создать решение (один раз)
```
dotnet new sln -n Practice3
dotnet sln add part1/Part1.Lib part1/Part1.Tests src/SubscriptionManager.Api tests/SubscriptionManager.Tests
dotnet build
```

## 2. Часть 1 — тесты
```
dotnet test part1/Part1.Tests
```
Тесты с суффиксом `_KnownDefect` намеренно закрепляют **текущее ошибочное** поведение исходного кода (см. `docs/PART1_REVIEW.md`).

## 3. Часть 2 — юнит- и интеграционные тесты
```
dotnet test tests/SubscriptionManager.Tests
```

## 4. Покрытие (цель ТЗ: 80%+ по ветвям, 90%+ по операторам для бизнес-логики)
```
dotnet tool install -g dotnet-reportgenerator-globaltool
dotnet test tests/SubscriptionManager.Tests --settings tests/SubscriptionManager.Tests/coverage.runsettings --collect:"XPlat Code Coverage" --results-directory coverage
reportgenerator -reports:"coverage/**/coverage.cobertura.xml" -targetdir:coverage/report -reporttypes:"TextSummary;Html"
```
Открой `coverage/report/Summary.txt` и `index.html`: смотри строки по `...Application.*` (Line coverage ≥ 90%, Branch coverage ≥ 80%). Цифры я не мерила — заполни по факту в отчёте.

## 5. Запуск сервиса
```
dotnet run --project src/SubscriptionManager.Api
```
Адрес: http://localhost:5080, Swagger UI: http://localhost:5080/swagger. БД — файл `subscriptions.db` (SQLite), создаётся сам.

## 6. Смоук-тесты (сервис должен быть запущен в другом окне)
- Windows PowerShell: `powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1`
- bash / Git Bash / WSL: `bash scripts/smoke.sh`

Ожидаемый итог: `FAIL=0`. Единичные команды вручную (PowerShell, именно `curl.exe`):
```
curl.exe -i -X POST http://localhost:5080/api/auth/register -H "Content-Type: application/json" -d "{\"email\":\"a@b.com\",\"password\":\"Passw0rd!123\"}"
curl.exe -s -X POST http://localhost:5080/api/auth/login -H "Content-Type: application/json" -d "{\"email\":\"a@b.com\",\"password\":\"Passw0rd!123\"}"
curl.exe -i http://localhost:5080/api/subscriptions -H "Authorization: Bearer <accessToken>"
```

## 7. Что менялось в чужом коде
Единственная правка исходников препода: в конструкторе `PrimesAmount` условие `if (border > 0)` заменено на `if (pborder > 0)` (иначе конструктор всегда бросал исключение), помечено комментарием `FIX`.
