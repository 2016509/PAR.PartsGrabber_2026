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

### Конфиг 
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


БИЗНЕС-ЛОГИКА PartsGrabber — ПОЛНЫЙ ЦИКЛ РАБОТЫ
⏰ ПРОЦЕСС ЗАПУСКАЕТСЯ КАЖДЫЙ Interval секунд (напр. 300s = 5мин)

1) Старт → проверка прокси и сайтов (CheckProxyResult)
text
ProcessService.Process() [каждые 300s]
│
└── SiteProxyCheckerService.CheckProxies(proxies, partSources)
    ├── GET https://partselect.com/   → Proxy1 → 200 OK          → ✅ partselect.com: [Proxy1]
    ├── GET https://partsdr.com/      → Proxy1 → 403 CF         → Playwright → 200 → ✅ partsdr.com: [Proxy1]
    ├── GET https://amazon.com/      → Proxy1 → 503            → Proxy2 → 200 → ✅ amazon.com: [Proxy2]
    └── GET https://xpartsupply.com/ → Proxy1 → 200 OK          → ✅ xpartsupply.com: [Proxy1]
✅ Результат: CheckProxyResult[9 сайтов] с рабочими прокси.

2) API хвост → берём записи на парсинг (Status = 0)
text
_apiService.Get("/GetPartsWithStateUrl")
    → PartsAndReplace[] где Status="0" (новые части)
✅ Пример результата:

json
[
  { "Id": 123, "MainPartNumber": "WPW10381562", "Status": "0" },
  { "Id": 124, "MainPartNumber": "WPW10381561", "Status": "0" }
]
3) Обработка каждой записи (пример: WPW10381562) → максимум 1 минута
text
foreach (part in partsFromAPI)
{
  using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

  ├── ParseService.Parse("WPW10381562", CheckProxyResult) → 9 парсеров параллельно |||
  │   ├── partsdr.com      → Name + 3xReplaces + Availability ✅
  │   ├── partselect.com   → Name + Replaces ✅
  │   ├── ebay.com         → 3xImages ✅
  │   ├── amazon.com       → 2xImages ✅
  │   ├── xpartsupply.com  → TIMEOUT → WithErrorToSave=true ❌
  │   └── ... остальные сайты
  │
  ├── [60s] TIMEOUT CHECK:
  │   └── Telegram: "Timeout 1min WPW10381562" ✅
  │
  └── PROCESS RESULTS:
      ├── УСПЕШНЫЕ (WithErrorToSave=false):
      │   └── Save(Name/Replaces/Images) → Archive + parts/pic/ ✅
      ├── НЕУСПЕШНЫЕ (WithErrorToSave=true):
      │   ├── PartSource.Status = false
      │   ├── ErrorLog: "Site xpartsupply.com not responding (timeout)"
      │   └── API PUT /partSource/{id} ✅
      └── UpdatePartsAndReplace() → итоговый статус:
          ├── minName = "WPW10381562 Motor" (самое короткое)
          ├── Pic = "parts/pic/1/123/1.jpg" (по confidence)
          ├── Replaces = ["WPW10381562","WPW10381561"] (уникальные)
          └── Status = "2" (есть данные) ✅
}
4) Если сайт прошёл ✅
partsdr.com → CheckSiteResult.Valid:

text
ParsingPart {
  Name: "WPW10381562 Motor Assembly",
  Replaces: ["WPW10381561", "WPW10381563"],
  ParsingPictures: ["https://partsdr.com/img1.jpg"],
  WithErrorToSave: false,  // ✅ успех
  AttempsCount: 2
}

↓ Save() → Archive + parts/pic/1/123/1.jpg
↓ PartSource.Status = true
5) Если сайт не прошёл ❌
xpartsupply.com → 8 attempts failed:

text
ParsingPart {
  WithErrorToSave: true,  // ❌ ошибка
  AttempsCount: 8
}

↓ PartSource.Status = false
↓ ErrorLog: "Site xpartsupply.com not responding (timeout)"
↓ Save() НЕ вызывается → нет Archive
6) Итоговый результат в БД (WPW10381562)
PartsAndReplace:

Id	MainPartNumber	Status	PartName	Pic	PhotoStatus
123	WPW10381562	"2"	WPW10381562 Motor	parts/pic/1/123/1.jpg	1
PartSource (9 записей):

SourceName	Status	Confidence	AttempsCount
partsdr.com	true	5	2
partselect.com	true	5	1
xpartsupply.com	false	4	8
Archive:

✅ PartsNamesArchive: 4 записи (Name с 4 сайтов)

✅ ReplacesArchive: 5 записей (все Replaces)

✅ PartsPicArchive: 12 записей (12 картинок)

ErrorLog:

✅ "Site xpartsupply.com not responding (timeout)"

7) Цикл повторяется
text
return DateTime.UtcNow.AddSeconds(_options.Interval); // +300s
↓ Следующая итерация → новые PartsAndReplace.Status="0"
📊 Резюме бизнес-логики
CheckProxies → рабочие прокси для 9 сайтов ✅

API хвост → берём PartsAndReplace.Status="0" ✅

1min таймаут → 9 парсеров параллельно → partial results ✅

Сайт прошёл → Archive + PartSource.Status=true ✅

Сайт не прошёл → ErrorLog + PartSource.Status=false ✅

Итоговый Status → "2" (partial данные) / "3" (пусто) ✅

Повтор каждые 300s → следующий PartNumber ✅
