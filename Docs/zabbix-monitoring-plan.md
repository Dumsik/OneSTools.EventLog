# Мониторинг экспорта журнала регистрации в Zabbix

Исходное ТЗ: [task_monitoring.md](task_monitoring.md).

## Контекст

Сейчас `OneSTools.EventLog.Exporter.Manager` (`ExportersManager`) поднимает по одному
`EventLogExporter` (проект `Core`) на каждую базу, найденную через `ClstWatcher`, и просто
логирует ошибки — снаружи невозможно понять, идёт ли выгрузка успешно, зависла она или
хранилище недоступно. Задача — добавить отправку статуса выгрузки и LLD discovery в Zabbix,
включаемую только в Manager (standalone `OneSTools.EventLog.Exporter` не трогаем).

Ключевая находка при анализе кода: `ClickHouseStorage.WriteEventLogDataAsync` (Core,
`ClickHouse/ClickHouseStorage.cs:110-123`) при ошибке записи уходит в **бесконечный retry**
(`while(true)` + `Task.Delay(1000)`) и никогда не бросает исключение наружу. Значит
`EventLogExporter` в принципе не видит "провалившуюся" запись — она либо ещё не завершилась,
либо успешна. Чтобы статус `FAIL` из ТЗ был вообще достижим, storage должен сигнализировать о
каждой неудачной попытке отдельным событием, а не только о финальном результате.

По уточнению пользователя: ретраи ClickHouse идут раз в секунду, поэтому наивная пересылка
каждого события в Zabbix зальёт его запросами при любом продолжительном простое БД. Финальное
решение пользователя: **не дедуплицировать по смене состояния, а просто ограничить частоту
отправки — не чаще одного статуса (OK или FAIL) раз в 5 минут** на базу. Это специально нужно,
чтобы на стороне Zabbix можно было настроить триггеры (в том числе `nodata()`/просрочку
последнего значения) на предсказуемом интервале.

Частота реальных циклов выгрузки (см. `EventLogExporter.StartAsync`, `Core/EventLogExporter.cs:105-157`):
порция пишется либо когда накопится `Exporter:Portion` записей (по умолчанию 10000, редко
достижимо быстро), либо принудительно каждые `Exporter:ReadingTimeout` секунд (по умолчанию
1 секунда, `Manager/appsettings.json`), когда чтение догнало хвост lgp-файла — в том числе с
пустой порцией (⇒ `OK` по п.2 ТЗ). При этом `WritingMaxDegreeOfParallelism` по умолчанию
равен 1, а `ClickHouseStorage.WriteEventLogDataAsync` при ошибке ретраит бесконечно **внутри
одного и того же вызова записи** — то есть при падении БД зависает ровно одна порция, и
`WriteAttemptFailed` от неё продолжает прилетать примерно раз в секунду, пока БД не
восстановится. Без троттлинга это давало бы ~300 запросов `FAIL` за 5 минут простоя.

Троттлинг делается **в Manager, внутри `ZabbixSender`** (а не в Core и не в
`ExportersManager`) — так вся политика "когда реально стучимся в Zabbix" (включение,
интервал, URL) остаётся в одном месте: `Core`/`EventLogExporter` по-прежнему честно
поднимает событие на каждую попытку/порцию, `ExportersManager` просто пересылает их все как
есть, а `ZabbixSender` решает, какие из вызовов реально долетают до HTTP.

## Конфигурация (appsettings.json)

Новая секция верхнего уровня `Zabbix` (рядом с `Manager`, `Exporter`, `ClickHouse`):

```json
"Zabbix": {
  "Enabled": false,
  "Server": "",
  "ItemsHost": "",
  "StatusSendIntervalMinutes": 5
}
```

- `Enabled` — единый флаг для отправки и статуса, и discovery, по умолчанию `false` (п.3 ТЗ:
  оба вида отправки включаются/выключаются вместе одним параметром).
- `Server` / `ItemsHost` — общие для обоих видов отправки (п.4 ТЗ): `Server` = "Сервер
  zabbix" (используется в URL хоста), `ItemsHost` = "host zabbix для элементов данных"
  (параметр `server=` в запросе, как и указано в ТЗ — это host-параметр zabbix_sender, не
  путать с именем сервера).
- `StatusSendIntervalMinutes` — минимальный интервал между отправками статуса **одной и той
  же** базы, по умолчанию 5 минут (по указанию пользователя — для настройки триггеров на
  стороне Zabbix). На discovery не влияет (оно и так раз в сутки).

## Изменения в Core (`OneSTools.EventLog.Exporter.Core`)

Core не должен ничего знать про Zabbix — он только даёт Manager'у возможность узнать
результат каждой порции выгрузки.

1. **`IEventLogStorage.cs`** — добавить `event EventHandler<Exception> WriteAttemptFailed;`.
2. **`ClickHouse/ClickHouseStorage.cs`** — в catch-блоке retry-цикла (`WriteEventLogDataAsync`)
   поднимать `WriteAttemptFailed` перед `Task.Delay(1000)`.
3. **`ElasticSearch/ElasticSearchStorage.cs`** — добавить пустую реализацию события ради
   соответствия интерфейсу (тип хранилища всё равно не поддерживается, см.
   `ExportersManager.GetStorage`).
4. **`EventLogExporter.cs`**:
   - новое публичное событие `event EventHandler<ExportPortionResult> ExportPortionCompleted;`
     (`ExportPortionResult` — новый небольшой класс/record в Core: `Success`, `ItemsCount`).
     Событие поднимается честно на каждую попытку/порцию, без внутреннего дедупа — троттлинг
     частоты отправки в Zabbix живёт в Manager (`ZabbixSender`, см. ниже), Core об этом не
     знает.
   - подписка на `_storage.WriteAttemptFailed` в конструкторе → поднимает
     `ExportPortionCompleted(success:false, 0)` на каждую неудачную попытку записи (в т.ч.
     повторные ретраи одной и той же зависшей порции).
   - в `InitializeDataflow` (строки 159-180) лямбда `_writeBlock` после успешного
     `await _storage.WriteEventLogDataAsync(...)` поднимает
     `ExportPortionCompleted(success:true, count)`.
   - в главном цикле `StartAsync` (строки 113-152): вести локальный счётчик количества
     элементов, отправленных в `_batchBlock` с последнего сброса. При срабатывании
     `forceSending` (таймаут чтения, по умолчанию каждую `Exporter:ReadingTimeout` секунду)
     — если счётчик равен 0 (реально нечего выгружать), сразу поднять
     `ExportPortionCompleted(success:true, 0)` (п.2 ТЗ — "если данных нет, отправляется OK");
     если счётчик > 0, обнулить его и вызвать `TriggerBatch()` как сейчас — событие
     поднимется через `_writeBlock`.
   - события не поднимаются при отмене (`cancellationToken.IsCancellationRequested`) —
     штатная остановка не должна выглядеть как провал экспорта.

## Изменения в Manager (`OneSTools.EventLog.Exporter.Manager`)

Новая папка `Zabbix/`:

1. **`ZabbixOptions.cs`** — POCO: `bool Enabled`, `string Server`, `string ItemsHost`,
   `int StatusSendIntervalMinutes`.
2. **`IZabbixSender.cs` / `ZabbixSender.cs`** — принимает `HttpClient` (через
   `IHttpClientFactory`/typed client), `IOptions<ZabbixOptions>`, `ILogger<ZabbixSender>`.
   - `Task SendStatusAsync(string dataBaseName, bool success, CancellationToken ct)` — если
     `Enabled == false`, no-op. Иначе троттлинг: `ConcurrentDictionary<string, DateTime>
     _lastSentAt` по `dataBaseName` — если с последней **фактической** отправки для этой базы
     прошло меньше `StatusSendIntervalMinutes`, вызов молча игнорируется (значение просто не
     отправляется, ни в каком виде); если прошло достаточно (или это первая отправка для
     базы — тогда сразу, чтобы не начинать с "nodata" в Zabbix), строит
     `https://{Server}/zabbix_sender/index.php?server={ItemsHost}&key=ExportStatus[{dataBaseName}]&value={OK|FAIL}`
     (значения key/value экранируются через `Uri.EscapeDataString`), делает `POST` с пустым
     телом (п.3 ТЗ секции статуса) и обновляет `_lastSentAt[dataBaseName]`.
   - `Task SendDiscoveryAsync(IReadOnlyCollection<string> dataBaseNames, CancellationToken ct)`
     — если `Enabled == false`, no-op. Иначе формирует LLD JSON вида
     `{"data":[{"{#IBNAME}":"base1"},{"{#IBNAME}":"base2"}]}` через `System.Text.Json` и
     отправляет `POST https://{Server}/zabbix_sender/index.php` с телом
     `FormUrlEncodedContent` (`server`, `key=EventLog.ExportStatus.Discovery`, `value=<json>`),
     явно проставив `Content-Type: application/x-www-form-urlencoded; charset=utf-8` (п.3 ТЗ
     секции discovery).
   - Ошибки HTTP-запроса — логируются (`LogWarning`) и не бросаются наружу дальше вызывающего
     кода (не должны ронять `ExportersManager`).
3. **`ExportersManager.cs`**:
   - конструктор принимает `IZabbixSender`.
   - `_runExporters` меняет тип значения с `CancellationTokenSource` на небольшой
     record/class, хранящий `Cts` + `Name` (имя информационной базы 1С, как оно приходит из
     `ClstWatcher`/`ClstEventArgs.Name`, **не** `dataBaseName` из ClickHouse-шаблона) — нужно
     для построения списка баз для discovery без дублирования состояния.
   - в `StartExporter` — подписка `exporter.ExportPortionCompleted += (_, r) =>
     ReportExportStatus(name, r.Success);` (`name` — тот же параметр, что уже приходит в
     `StartExporter(string path, string name, string dataBaseName)`). `ReportExportStatus` —
     fire-and-forget (`_ = Task.Run(...)` с try/catch и логированием), просто пересылает
     каждое полученное от `EventLogExporter` событие в `_zabbixSender.SendStatusAsync` без
     какой-либо своей логики — троттлинг до раза в 5 минут целиком внутри `ZabbixSender`.
   - в `ExecuteAsync` — фоновый цикл раз в 24 часа (`PeriodicTimer(TimeSpan.FromHours(24))`,
     первая отправка сразу при старте), собирающий текущие `Name` из `_runExporters`
     и вызывающий `_zabbixSender.SendDiscoveryAsync(...)`. Discovery отражает **только базы,
     для которых сейчас реально запущен экспорт** (совпадает с трактовкой "базы, для которых
     выполняется выгрузка" из п.1 секции discovery ТЗ), а не пересылается при каждом
     добавлении/удалении базы — строго раз в сутки, как написано в ТЗ.
4. **`Program.cs`** — `services.Configure<ZabbixOptions>(configuration.GetSection("Zabbix"))`,
   `services.AddHttpClient<IZabbixSender, ZabbixSender>().ConfigurePrimaryHttpMessageHandler(() =>
   new HttpClientHandler { ServerCertificateCustomValidationCallback =
   HttpClientHandler.DangerousAcceptAnyServerCertificateValidationCallback })` — сервер Zabbix
   отдаёт самоподписанный сертификат, поэтому валидация цепочки отключена **только для этого
   именованного/типизированного клиента**, никакой другой HTTP-трафик в решении это не
   затрагивает.
5. **`appsettings.json`** — добавить пример секции `Zabbix` (отключено по умолчанию).

## Допущения (проговорить на ревью)

- Discovery шлётся сразу при старте Manager, затем раз в 24 часа от момента старта (в ТЗ не
  указано конкретное время суток — фиксированного расписания типа "в 00:00" не делаем).
- Ключ Zabbix для статуса (`ExportStatus[%1]`) и значение `{#IBNAME}` в discovery — это имя
  информационной базы 1С (`name` из `ClstWatcher`/`1CV8Clst.lst`), а не `dataBaseName`,
  который используется для подстановки в шаблон имени БД ClickHouse. Это две разные строки:
  первая — читаемое имя базы для человека/мониторинга, вторая — техническое имя БД.
- Статус `FAIL` для ClickHouse-хранилища технически наблюдаем только благодаря новому событию
  `WriteAttemptFailed`; сам retry-цикл в `ClickHouseStorage` не меняется (по-прежнему ретраит
  бесконечно) — меняется только видимость происходящего наружу.
- `HttpClient` — без Polly/retry-политик: разовая неудачная отправка в Zabbix просто
  логируется и не повторяется — следующий реальный статус (следующая порция/восстановление)
  отправится обычным порядком.
- Проверка серверного сертификата отключена по прямому указанию пользователя (Zabbix отдаёт
  самоподписанный сертификат) — это ослабляет защиту от MITM на этом конкретном соединении;
  ограничиваем эффект только клиентом `IZabbixSender`, никакой другой код в решении сейчас
  HTTPS вообще не использует.

## Проверка

Тестовых проектов в решении нет. Проверять:
1. `dotnet build OneSTools.EventLog.sln` — компиляция всех проектов, включая `Core` (события)
   и standalone `Exporter` (не должен требовать изменений — он не использует
   `ExportPortionCompleted`/Zabbix).
2. Ручной прогон Manager с `Zabbix:Enabled: true` и тестовым URL (например, локальный
   HTTP-эхо/лог-сервер вместо реального Zabbix) — убедиться, что:
   - сразу при старте базы летит первый `OK` с правильным `key` (без ожидания интервала);
   - при постоянном потоке успешных порций повторные `OK` не летят чаще, чем раз в
     `StatusSendIntervalMinutes` (для проверки можно временно уменьшить интервал в
     конфиге, например до 10-20 секунд);
   - при отключении ClickHouse `WriteAttemptFailed` продолжает сыпаться раз в секунду, но в
     Zabbix уходит не чаще одного `FAIL` за интервал, и при восстановлении БД следующий
     отправленный статус — `OK`;
   - discovery-запрос уходит сразу при старте с корректным LLD JSON.

Unit-тесты пока не делаем (тестового проекта в решении нет, отдельно не заводим).
