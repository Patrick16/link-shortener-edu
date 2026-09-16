# Архитектура

## Компоненты

- **AuthApi** — регистрация / логин, пишет в `Postgres users`. Отдаёт `UserAuth` остальным сервисам.
- **LinkApi** — приём запроса на создание короткой ссылки. Если пользователь авторизован — `userId` пишется в hash. Публикует `origin link` в RabbitMQ для `ShortenerService`.
- **ShortenerService** (воркер) — слушает RabbitMQ, генерирует hash, пишет короткую ссылку в `Postgres Links` (шардированную).
- **RedirectApi** — принимает короткую ссылку, резолвит origin link через `Redis Links` (кэш) либо напрямую, отдаёт редирект, публикует событие клика в RabbitMQ.
- **TrafficService** (воркер) — слушает RabbitMQ, пишет клики в `Postgres Clicks Users` и метаданные (user-agent, referrer, headers) в `Mongo Clicks meta`.

## Хранилища

- `Postgres users` — Users(id, name, email, passwordHash, sault)
- `Postgres Links` — **шардирована** (см. `infra/pgcat/shard1.toml`, `shard2.toml`): Links(hash PK, originLink, shortenLink, userId?)
- `Redis Links` — кэш hash → originLink для быстрого редиректа
- `Postgres Clicks Users` — Clicks(id, clickedAt, inboundLink, outboundLink, hash)
- `Mongo Clicks meta` — ClicksMeta(id, clickedAt, userAgent, referrer, origin, headers)

## Инфраструктура для практики хайлоада

- **Шардирование** Postgres Links по hash(key) % N через `ShardResolver` + pgcat-пулы на шард
- **Реплики** сервисов (несколько инстансов каждого API/воркера за nginx)
- **Партиционирование** Clicks/ClicksMeta по времени
- **Шина** RabbitMQ между API и воркерами (LinkApi → ShortenerService, RedirectApi → TrafficService)
- **pgcat/pgbouncer** — пулинг соединений к каждому шарду Postgres
- **nginx** — балансировщик нагрузки перед API-сервисами

## TODO

Детали (schema, конфиги pgcat/nginx/rabbitmq, docker-compose) заполняются по ходу практики — см. `infra/` и корневые `docker-compose*.yml`.
