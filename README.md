# PAR.PartsGrabber

Консольный воркер для массового парсинга сайтов запчастей через прокси с сохранением результатов в центральный API: имена, replaces (замены), картинки и агрегированные статусы по детали.

## Что делает модуль

1. На старте:
- Читает `appsettings.json` и биндингит настройки (`ApiService`, `Module`, списки сайтов).
- Поднимает DI-контейнер, логирование, и один экземпляр Chromium (Playwright) на процесс.
- Забирает из API список прокси и список источников (`PartSource`), фильтрует активные, прогоняет чекер прокси по каждому сайту.

2. Проверка прокси/источников:
- Для каждого сайта проверяет прокси; при Cloudflare/SSL проблемах может переключаться на Playwright.
- Если для сайта не найдено ни одного подходящего прокси, модуль отключает источник (`PartSource.Status = false`) и сохраняет ошибку в API (`ErrorLog`).

3. Основной цикл:
- По таймеру (`Module.Interval` секунд) забирает из API список деталей на обработку (`PartsAndReplace`).
- Для каждой детали запускает парсинг по всем активным источникам (параллельно по источникам), сохраняет архивы результатов и обновляет агрегированное состояние детали.

Остановка: нажать `ESC` в консоли.

## Входные данные

### Конфиг (appsettings.json)
- `ApiService` (BaseUrl и набор URL’ов эндпойнтов).
- `Module.Interval` — интервал между проходами.
- `SitesToParse`, `SitesToCheckProxy` (опционально, под выборку/проверку сайтов).

### Данные из API
- `Proxy` — список прокси (используются только активные).
- `PartSource` — список источников (используются только активные).
- `PartsAndReplace` — список деталей, которые нужно обработать.

## Выходные данные

Модуль пишет результаты обратно в API:
- `PartsNamesArchive` — архив имён детали по источнику.
- `ReplacesArchive` — архив replaces по источнику.
- `PartsPicArchive` — архив ссылок/локальных путей картинок по источнику.
- `ErrorLog` — ошибки (например, сайт недоступен или не выбран прокси).
- Обновления `PartSource` (отключение проблемного источника).
- Обновления `PartsAndReplace` (агрегированные поля: имя/замены/картинка/статусы).

Локально:
- Скачанные и обработанные изображения сохраняются в `parts/pic/{partsSourceId}/{partsReplacesId}/...`.

## Как работает парсинг

### Источники и парсеры
Поддерживаются парсеры (через `IParser` + `ParsersFactory`) для сайтов, например:
- Amazon (.com/.ca), eBay, SearsPartsDirect, PartsDr, PartSelect, MajorApplianceParts, AppliancePartsHQ, XPartSupply и др.

### Получение HTML через прокси
- Основной путь: `HttpClient` через `ProxiedHttpClientPool` (пул клиентов на (proxy, host)).
- Для WAF/Cloudflare/части сайтов — принудительно Playwright или fallback на него.
- Лимиты параллелизма по host: gate на `SemaphoreSlim` (WAF-heavy хосты ограничиваются до 1 одновременного запроса).

### Лизинг прокси
- `ProxyLeaseManager` выдаёт «аренду» прокси на (proxy, host) с TTL, cooldown и ban, чтобы не перегружать один прокси и снижать частоту банов.

## Статусы и бизнес-логика

### Отключение источника (PartSource)
Если при первичной проверке у сайта не нашлось подходящих прокси:
- `PartSource.Status = false`
- `PUT {BaseUrl}{UpdatePartSourceUrl}/{id}`
- `POST {BaseUrl}{SaveErrorUrl}` с сообщением про невозможность подобрать прокси

Если в процессе парсинга источник возвращает флаг `WithErrorToSave`:
- `PartSource.Status = false`
- сохраняется `ErrorLog` `"Site {SourceName} not responding"`

### Обновление детали (PartsAndReplace)
После обработки детали модуль агрегирует результаты:
- Если не найдено ни имени, ни replaces: `Status = "3"`, `Replaces = ["{MainPartNumber}"]`.
- Если найдено имя и/или replaces: `Status = "2"`, `PartName` (самое короткое из найденных, ограничение 255), `Replaces = [MainPartNumber + distinct replaces]`, `Pic` = локальный путь первой подходящей картинки, `PhotoStatus = 1` если картинка есть.

## Эндпойнты API (через ApiServiceOptions)

Минимальный набор (используется модулем):
- `GET {BaseUrl}{GetProxiesUrl}` → `List<Proxy>`
- `GET {BaseUrl}{GetPartsSourcesUrl}` → `List<PartSource>`
- `GET {BaseUrl}{GetPartsWithStateUrl}` → `List<PartsAndReplace>`
- `PUT {BaseUrl}{UpdatePartSourceUrl}/{id}` → обновление `PartSource`
- `PUT {BaseUrl}{UpdatePartsAndReplacesUrl}` → обновление `PartsAndReplace`
- `POST {BaseUrl}{AddPartsNamesArchiveUrl}` → `PartsNamesArchive`
- `POST {BaseUrl}{AddReplacesArchiveArchiveUrl}` → `ReplacesArchive`
- `POST {BaseUrl}{AddPartsPicArchiveUrl}` → `PartsPicArchive`
- `POST {BaseUrl}{SaveErrorUrl}` → `ErrorLog`

## Запуск

Требования:
- .NET runtime/SDK под вашу версию проекта
- Доступ к API и рабочие прокси

Запуск:
- `dotnet run` (или запуск собранного exe)
- Для остановки нажмите `ESC` в окне консоли.

