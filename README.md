# Task API — тестовое задание

.NET 9, ASP.NET Core Minimal API, PostgreSQL / EF Core, RabbitMQ.Client 7.

## Запуск

Нужен только Docker с Linux containers. Из корня проекта:

```powershell
docker compose up
```

API: `http://localhost:5180`. PostgreSQL: `localhost:5433`, БД/пользователь/пароль: `tasks`.
Swagger UI: `http://localhost:5180/swagger`; OpenAPI: `/swagger/v1/swagger.json`.
Все эндпоинты открыты, аутентификация не используется.
RabbitMQ: `localhost:5672`; management UI: `http://localhost:15672`, `tasks` / `tasks`.
Пароли в Compose — только для локального тестового окружения.

Compose соберёт API в многоэтапном Dockerfile и запустит его после готовности PostgreSQL
и RabbitMQ. .NET на компьютере для этого не нужен. API работает от непривилегированного
пользователя; на корректную остановку контейнера отведено 10 секунд (бюджет хоста — 5 секунд).
В фоне: `docker compose up -d`. После изменения кода: `docker compose up --build`.
Остановка: `docker compose down`; данные PostgreSQL сохраняются в Docker volume.

### Запуск API без контейнера

Для разработки нужен .NET SDK 9. Поднимите только зависимости, затем запустите API:

```powershell
docker compose up -d postgres rabbitmq
$env:RabbitMq__Uri = 'amqp://tasks:tasks@localhost:5672/'
dotnet run --project TaskApi --launch-profile http
```

Таблицы создаются через `EnsureCreatedAsync` при запуске. Миграций нет; для последующих
изменений схемы следует заменить EnsureCreated на миграции. PostgreSQL должен быть доступен
при старте; RabbitMQ может быть выключен.

Для собственных сервисов переопределите настройки, например:

```powershell
$env:ConnectionStrings__Tasks = 'Host=localhost;Port=5432;Database=tasks;Username=tasks;Password=tasks'
$env:RabbitMq__Uri = 'amqp://guest:guest@localhost:5672/'
dotnet run --project TaskApi --launch-profile http
```

## API

| Запрос | Ответ |
| --- | --- |
| `POST /tasks` | `201`, созданная задача; некорректный title/priority → `400` |
| `GET /tasks` | `200`, список `items` и метаданные; без пагинации — все задачи |
| `GET /tasks/{id}` | `200`, задача; отсутствует → `404` |
| `PUT /tasks/{id}/complete` | `200`, задача; отсутствует → `404`; уже завершена / конкурентно изменена → `409` |
| `DELETE /tasks/{id}` | `204`; отсутствует → `404` |

Некорректный GUID → `400`. Priority передаётся строкой `Low`, `Medium`, `High`,
по умолчанию `Medium`; числовые значения отвергаются. Title: не null, не пустой,
не только пробелы, не длиннее 200 символов до обрезки пробелов по краям.
При создании `IsCompleted = false`, `CompletedAt = null`; клиент не управляет этими полями.
Валидация POST и query-параметров выполнена через FluentValidation и общий endpoint filter.
Ошибки возвращаются как `application/problem+json` с полями `errors` и `traceId`.

```powershell
$task = Invoke-RestMethod http://localhost:5180/tasks -Method Post -ContentType application/json -Body '{"title":"Buy milk","priority":"High"}'
Invoke-RestMethod "http://localhost:5180/tasks/$($task.id)/complete" -Method Put
Invoke-RestMethod http://localhost:5180/tasks
Invoke-RestMethod "http://localhost:5180/tasks/$($task.id)" -Method Delete
```

### Пагинация, фильтры и сортировка

```http
GET /tasks?page=1&pageSize=20&isCompleted=false&priority=High&search=milk&sortBy=CreatedAt&sortDirection=Desc
```

| Параметр | Допустимые значения / поведение |
| --- | --- |
| `page` | От 1 до 1 000 000; по умолчанию 1, если указан только `pageSize` |
| `pageSize` | От 1 до 100; по умолчанию 20, если указан только `page` |
| `isCompleted` | `true` / `false`; без параметра — оба состояния |
| `priority` | `Low`, `Medium`, `High`; без параметра — все приоритеты |
| `search` | Поиск подстроки в Title без учёта регистра, до 200 символов; `%`, `_`, `\` считаются буквальными символами |
| `sortBy` | `CreatedAt` (по умолчанию), `CompletedAt`, `Title`, `Priority` |
| `sortDirection` | `Asc` (по умолчанию) / `Desc` |

Фильтры комбинируются через AND. Приоритет сортируется как Low → Medium → High,
задачи без CompletedAt идут последними в обоих направлениях. При равенстве значений
сортировки используется Id. Фильтрация, сортировка и пагинация выполняются в БД.
Некорректные параметры → `400`; страница за пределами списка → пустой `items`.

Формат ответа списка теперь единый: **объект, а не прежний массив**.
Без `page` и `pageSize` все задачи находятся в `items`, поля пагинации равны `null`.

```json
{
  "items": [],
  "totalCount": 0,
  "page": 1,
  "pageSize": 20,
  "totalPages": 0
}
```

### Логирование

Serilog пишет структурированный JSON в stdout: метод, путь, HTTP-статус, время запроса,
`TraceId`, события создания/завершения/удаления с `TaskId`, результаты отправки RabbitMQ.
Заголовок `X-Trace-Id` и `traceId` в ProblemDetails связывают ответ с логом запроса.
Тела обычных запросов не логируются; JSON неотправленного события пишется в warning-лог
в соответствии с ТЗ. Логи контейнера: `docker compose logs -f api`.

## Решения и компромиссы

- `uint RowVersion` + `IsRowVersion()` в Npgsql используют системную колонку PostgreSQL
  `xmin`. EF включает исходный xmin в UPDATE; проигравший конкурентный запрос получает
  `DbUpdateConcurrencyException` → `409`. Повторный запрос также получает `409` и не меняет дату.
  [Документация Npgsql](https://www.npgsql.org/efcore/modeling/concurrency.html).
- Сначала `SaveChangesAsync`, затем постановка события в `Channel` ёмкостью 1024.
  HTTP не ждёт подключения, отправки или confirms. Один фоновый читатель владеет AMQP-каналом.
- Durable topic exchange `task.events`, routing key `task.completed`, persistent JSON:
  `taskId`, `title`, `completedAt` (UTC), `priority` (строка). На попытку подключения/публикации
  отводится до 3 секунд. Ошибка или переполненная очередь → полный JSON события в warning-логе;
  состояние задачи остаётся завершённым. Следующее событие инициирует новое подключение.
  [Асинхронный API RabbitMQ](https://www.rabbitmq.com/client-libraries/dotnet-api-guide).
- На остановке закрывается запись в очередь, до 4 секунд даётся на её опустошение,
  затем отменяется worker. Общий бюджет остановки хоста — 5 секунд. AMQP-канал и соединение
  освобождаются асинхронно в `finally`; оставшиеся события логируются.
- Это **best effort**, без outbox и повторной отправки: при аварийном завершении процесса
  между commit и публикацией событие теряется. Без publisher confirms получение брокером
  не гарантируется. Для production добавил бы transactional outbox и confirms.
- Приложение объявляет exchange, но не очереди: получатель должен заранее создать очередь
  и привязать её к `task.events` по `task.completed`. Без binding RabbitMQ отбросит сообщение.
- Consumer отсутствует — по ТЗ требуется только publish.

## Проверка

```powershell
dotnet build
dotnet test
dotnet test --filter Category=Unit
dotnet format whitespace --verify-no-changes
```

Для тестов нужен работающий Docker. Testcontainers поднимает отдельный PostgreSQL на случайном
порту и автоматически удаляет его. Тест RabbitMQ также использует отдельный контейнер;
`docker compose up` для тестов не требуется. При первом запуске скачиваются образы.

Проверено локально 15.10.2026: сборка без предупреждений и ошибок, **41 тест прошёл,
0 ошибок, 0 пропусков**; использовались настоящие PostgreSQL и RabbitMQ в контейнерах.

Проверяются создание → завершение → событие через подставленный интерфейс, данные в БД,
повторное завершение, удаление, валидация, значения по умолчанию, недоступный RabbitMQ.
Два конкурентных запроса синхронизированы перед SaveChanges interceptor-ом: оба гарантированно
читают одинаковый xmin, затем ровно один получает `200`, другой `409`, событие только одно.
Отдельный тест получает реальное сообщение из RabbitMQ после немедленной остановки publisher-а,
проверяя JSON и опустошение очереди при graceful shutdown.

Unit-тесты (19) проверяют инварианты сущности, неизменность CompletedAt после повторного
завершения, UTC/точность времени, FluentValidation и правила сортировки/фильтрации.
Интеграционные тесты (22) дополнительно проверяют SQL-фильтры, пагинацию, поиск буквальных
wildcard-символов, сортировку приоритетов в PostgreSQL, Swagger и корреляцию ошибок с логами.
Только unit-тесты можно запускать без Docker командой с `--filter Category=Unit`.

## Файлы

- `TaskApi/Program.cs` — короткая точка входа и HTTP pipeline.
- `TaskApi/DependencyInjection.cs` — регистрация сервисов, Swagger, Serilog.
- `TaskApi/Api/` — эндпоинты, общий validation filter, trace middleware.
- `TaskApi/Contracts/` — модели запросов и ответов; EF-сущности не возвращаются в API.
- `TaskApi/Domain/` — сущность и её инварианты.
- `TaskApi/Application/Tasks/` — сценарии работы, валидаторы и построение запросов.
- `TaskApi/Infrastructure/Persistence/` — EF mapping, xmin, создание БД.
- `TaskApi/Infrastructure/Messaging/` — очередь публикаций, RabbitMQ, graceful shutdown.
- `TaskApi.Tests/Unit/` — unit-тесты без инфраструктуры.
- `TaskApi.Tests/` — интеграционные тесты с PostgreSQL и RabbitMQ.
- `.editorconfig` — общий стиль: file-scoped namespace, отступы, обязательные фигурные скобки.
