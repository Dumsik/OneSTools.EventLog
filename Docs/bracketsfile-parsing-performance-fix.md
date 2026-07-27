# Квадратичный парсинг больших полей Comment в журнале регистрации

## Проблема

Событие журнала регистрации с большим полем `Comment` (десятки мегабайт) парсится
экспортером аномально долго. Зафиксированный случай: `Comment` размером 70 МБ парсился
~20 часов вместо секунд.

## Причина

Парсинг записи выполнялся сторонним NuGet-пакетом `OneSTools.BracketsFile` (v2.1.9,
MIT, Akpaev Evgeniy, github.com/akpaevj/OneSTools.BracketsFile), который
`LgpReader.ParseEventLogItemData` вызывает через `BracketsParser.ParseBlock(eventLogItemData)`
(`OneSTools.EventLog/LgpReader.cs:105`), где `eventLogItemData` — это уже полностью
прочитанный в память `StringBuilder` со всей записью, включая `Comment`
(`parsedData[9]`, `LgpReader.cs:150`).

Внутри `BracketsParser` методы `ParseBlock`, `GetNodeEndIndex` и `GetTextValueEndIndex`
посимвольно сканировали этот `StringBuilder`, обращаясь к нему как к обычному массиву:
`text[i]`, `text[i-1]`, `text[i+1]`. Но `StringBuilder` хранит данные не непрерывным
буфером, а связанным списком чанков (`m_ChunkPrevious`, ~8000 символов на чанк), и его
индексатор `this[index]` при каждом обращении идёт от последнего (текущего) чанка назад
по цепочке, пока не найдёт чанк с нужным индексом.

Для 70 МБ `Comment` это ≈8750 чанков. Каждое обращение `text[i]` внутри цикла по всей
длине поля стоит `O(число чанков от i до конца)`, поэтому линейный по замыслу проход по
записи фактически становится квадратичным — `O(n²/8000)` вместо `O(n)`. Поле к тому же
сканировалось минимум дважды (один раз в `GetNodeEndIndex` — найти конец всей записи,
второй раз в `ParseBlock` — извлечь само значение), с тремя такими обращениями к
индексатору на каждой итерации (`prevChar`/`currentChar`/`nextChar`).

Важный нюанс: инкрементальный построчный ридер `BracketsListReader.NextNodeAsStringBuilder`
(читает файл посимвольно, накапливая `StringBuilder`) этой проблемы не имел — там
`GetTextValueEndIndex`/`GetNodeEndIndex` всегда вызываются с индексом последнего
добавленного символа (`itemData.Length - 1`), то есть всегда попадают в хвостовой чанк —
O(1) на символ. Квадратичность возникала исключительно при повторном разборе уже
полностью прочитанной записи через `BracketsParser.ParseBlock`.

## Решение

1. **Перенос парсера в состав `OneSTools.EventLog`.** Исходники `OneSTools.BracketsFile`
   вендорены в `OneSTools.EventLog/BracketsFile/` (`BracketsNode.cs`,
   `BracketsListReader.cs`, `BracketsStreamReaderExtensions.cs`, `BracketsParser.cs`),
   namespace `OneSTools.BracketsFile` сохранён — это не потребовало правок в
   `LgpReader.cs`, `LgfReader.cs` и `OneSTools.EventLog.Exporter.Manager/ClstWatcher.cs`
   (последний получает эти типы транзитивно через цепочку `ProjectReference`
   `Manager → Core → EventLog`). `PackageReference` на `OneSTools.BracketsFile` убран из
   `OneSTools.EventLog.csproj`.

2. **Фикс алгоритма.** `BracketsParser.ParseBlock(StringBuilder, ...)` теперь только
   материализует буфер в `string` один раз (`text.ToString()`, O(n)) и делегирует в новый
   `ParseBlock(string, ...)`, который со всеми вспомогательными методами
   (`GetNodeEndIndex`, `GetValueEndIndex`, `GetTextValueEndIndex`) работает уже по
   `string` — индексация там всегда O(1), поэтому разбор записи снова линейный.
   `StringBuilder`-версии этих же вспомогательных методов оставлены нетронутыми и
   используются только `BracketsListReader` — там они и раньше были не квадратичными.

Лицензия: `OneSTools.BracketsFile` распространяется под MIT тем же автором, что и сам
`OneSTools.EventLog` (см. корневой `LICENSE`), поэтому отдельного файла атрибуции не
потребовалось — в шапке каждого вендоренного файла оставлена ссылка на источник.

## Ограничение размера поля Comment

Чтобы снизить устойчивый пик памяти (поле `Comment` держится в `EventLogItem` по всему батчу
экспорта и целиком уходит в ClickHouse), в `LgpReader.ParseEventLogItemData` добавлена обрезка
поля: если `Comment` длиннее `MaxCommentLength` (10 * 1024 * 1024 символов, UTF-16), берутся
первые 10 МБ, к которым дописывается маркер `[Комментарий обрезан из-за превышения размера в 10
мегабайт]`. На границе среза не разрывается суррогатная пара.

Важно: обрезка на уровне поля убирает **устойчивый** пик (70 МБ → 10 МБ на такое событие) и
размер вставки в хранилище, но **не** уменьшает транзиентный пик разбора одной записи
(`StringBuilder` + `ToString()` + `Substring` в парсере) — к моменту присваивания `Comment`
полная строка уже материализована. Транзиентный пик неустраним без вмешательства в общий
`BracketsParser`, куда тащить событийную семантику («поле comment», «10 МБ») нежелательно.

## Не проверено

В момент внесения изменений в окружении отсутствовал .NET SDK, поэтому солюшн не
собирался и не запускался — только вручную сверены все точки вызова (`ParseBlock`,
`GetNodeEndIndex`, `GetTextValueEndIndex`) на совпадение сигнатур между `string`- и
`StringBuilder`-перегрузками. Перед мержем нужно собрать `OneSTools.EventLog.sln` и
прогнать экспортер на записи с большим `Comment`, чтобы подтвердить, что время парсинга
вернулось к норме.

## Удалён мёртвый код

`OneSTools.EventLog/StreamReaderExtensions.cs` (namespace `OneSTools.EventLog`) —
неиспользуемый файл, дублировавший логику `BracketsStreamReaderExtensions`, но ни разу не
вызываемый напрямую (все вызовы `.GetPosition()`/`.SetPosition()` в решении идут через методы
`LgfReader`/`LgpReader`/`BracketsListReader`, а работу со `StreamReader` делает вендоренный
`BracketsStreamReaderExtensions`). Удалён.
