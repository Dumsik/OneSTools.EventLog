using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NodaTime;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    internal class LgpReader : IDisposable
    {
        private readonly DateTimeZone _timeZone;
        private BracketsListReader _bracketsReader;
        private bool _disposedValue;
        private FileStream _fileStream;
        private LgfReader _lgfReader;
        private FileSystemWatcher _lgpFileWatcher;

        public LgpReader(string lgpPath, DateTimeZone timeZone, LgfReader lgfReader)
        {
            LgpPath = lgpPath;
            _timeZone = timeZone;
            _lgfReader = lgfReader;
        }

        public string LgpPath { get; }
        public string LgpFileName => Path.GetFileName(LgpPath);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public EventLogItem ReadNextEventLogItem(CancellationToken cancellationToken = default)
        {
            if (_disposedValue)
                throw new ObjectDisposedException(nameof(LgpReader));

            InitializeStreams();

            return ReadEventLogItemData(cancellationToken);
        }

        public void SetPosition(long position)
        {
            InitializeStreams();

            _bracketsReader.Position = position;
        }

        private void InitializeStreams()
        {
            if (_fileStream is null)
            {
                if (!File.Exists(LgpPath))
                    throw new Exception($"Cannot find lgp file by path {LgpPath}");

                _lgpFileWatcher = new FileSystemWatcher(Path.GetDirectoryName(LgpPath)!, "*.lgp")
                {
                    NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.FileName |
                                   NotifyFilters.Attributes
                };
                _lgpFileWatcher.Deleted += LgpFileWatcher_Deleted;
                _lgpFileWatcher.EnableRaisingEvents = true;

                _fileStream = new FileStream(LgpPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                _bracketsReader = new BracketsListReader(_fileStream);
            }
        }

        private void LgpFileWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Deleted && LgpPath == e.FullPath) Dispose();
        }

        private (StringBuilder Data, long EndPosition) ReadNextEventLogItemData()
        {
            var data = _bracketsReader.NextNodeAsStringBuilder();

            return (data, _bracketsReader.Position);
        }

        private EventLogItem ReadEventLogItemData(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var (data, endPosition) = ReadNextEventLogItemData();
                if (data.Length == 0)
                    return null;

                var eventLogItem = ParseEventLogItemData(data, endPosition, cancellationToken);
                if (eventLogItem != null)
                    return eventLogItem;
            }
        }

        private EventLogItem ParseEventLogItemData(StringBuilder eventLogItemData, long endPosition,
            CancellationToken cancellationToken = default)
        {
            var parsedData = BracketsParser.ParseBlock(eventLogItemData);

            var eventLogItem = new EventLogItem
            {
                TransactionStatus = GetTransactionPresentation(parsedData[1]),
                FileName = LgpFileName,
                EndPosition = endPosition,
                LgfEndPosition = _lgfReader.GetPosition()
            };

            try
            {
                eventLogItem.DateTime = _timeZone.ToUtc(DateTime.ParseExact(parsedData[0], "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture));
            }
            catch
            {
                eventLogItem.DateTime = DateTime.MinValue;
            }

            var transactionData = parsedData[2];
            eventLogItem.TransactionNumber = Convert.ToInt64(transactionData[1], 16);

            var transactionDate = new DateTime().AddSeconds(Convert.ToInt64(transactionData[0], 16) / 10000);
            eventLogItem.TransactionDateTime = transactionDate == DateTime.MinValue
                ? transactionDate
                : _timeZone.ToUtc(transactionDate);

            var (value, uuid) = _lgfReader.GetReferencedObjectValue(ObjectType.Users, parsedData[3], cancellationToken);
            eventLogItem.UserUuid = uuid;
            eventLogItem.User = value;

            eventLogItem.Computer = _lgfReader.GetObjectValue(ObjectType.Computers, parsedData[4], cancellationToken);

            var application = _lgfReader.GetObjectValue(ObjectType.Applications, parsedData[5], cancellationToken);
            eventLogItem.Application = GetApplicationPresentation(application);

            eventLogItem.Connection = parsedData[6];

            var ev = _lgfReader.GetObjectValue(ObjectType.Events, parsedData[7], cancellationToken);
            eventLogItem.Event = GetEventPresentation(ev);

            var severity = (string)parsedData[8];
            eventLogItem.Severity = GetSeverityPresentation(severity);

            eventLogItem.Comment = TruncateLargeComment(parsedData[9]);

            (value, uuid) = _lgfReader.GetReferencedObjectValue(ObjectType.Metadata, parsedData[10], cancellationToken);
            List<string> metadataNames = new List<string>();
            List<string> metadataUuid = new List<string>();
            if (value != string.Empty) 
            { 
                metadataNames.Add(value);
                metadataUuid.Add(uuid);
            }

            eventLogItem.Data = GetData(parsedData[11]).Trim();
            eventLogItem.DataPresentation = parsedData[12];
            eventLogItem.Server = _lgfReader.GetObjectValue(ObjectType.Servers, parsedData[13], cancellationToken);

            var mainPort = _lgfReader.GetObjectValue(ObjectType.MainPorts, parsedData[14], cancellationToken);
            if (mainPort != "")
                eventLogItem.MainPort = int.Parse(mainPort);

            var addPort = _lgfReader.GetObjectValue(ObjectType.AddPorts, parsedData[15], cancellationToken);
            if (addPort != "")
                eventLogItem.AddPort = int.Parse(addPort);

            eventLogItem.Session = parsedData[16];

            var additionalMetadataCount = parsedData[17];
            var strMetadataNames = new StringBuilder();
            var strMetadataUuid = new StringBuilder();
            if (additionalMetadataCount > 0)
                for (var i = 0; i < additionalMetadataCount; i++)
                {
                    (value, uuid) = _lgfReader.GetReferencedObjectValue(ObjectType.Metadata, parsedData[18 + i], cancellationToken);
                    metadataNames.Add(value);
                    metadataUuid.Add(uuid);
                }
            eventLogItem.Metadata = strMetadataNames.AppendJoin(";", metadataNames.ToArray()).ToString();
            eventLogItem.MetadataUuid = strMetadataUuid.AppendJoin(";", metadataUuid.ToArray()).ToString();
            return eventLogItem;
        }

        private const int MaxCommentLengthMb = 10;

        // Ограничение считается в символах (UTF-16): аномально большие Comment (десятки МБ) держатся
        // в EventLogItem по всему батчу экспорта и уходят в хранилище, раздувая устойчивый пик памяти.
        private const int MaxCommentLength = MaxCommentLengthMb * 1024 * 1024;

        private static readonly string CommentTruncatedMarker =
            $"\n\n[Комментарий обрезан из-за превышения размера в {MaxCommentLengthMb} мегабайт]";

        private static string TruncateLargeComment(string comment)
        {
            if (comment is null || comment.Length <= MaxCommentLength)
                return comment;

            var cutLength = MaxCommentLength;
            // не разрываем суррогатную пару на границе среза
            if (char.IsHighSurrogate(comment[cutLength - 1]))
                cutLength--;

            return string.Concat(comment.AsSpan(0, cutLength), CommentTruncatedMarker);
        }

        private static string GetData(BracketsNode node)
        {
            var dataType = (string) node[0];

            switch (dataType)
            {
                case "R": // Reference
                    return node[1];
                case "U": // Undefined
                    return "";
                case "S": // String
                    return node[1];
                case "B": // Boolean
                    return (string) node[1] == "0" ? "false" : "true";
                case "N": // Numeric
                    return node[1];
                case "P": // Complex data
                    var str = new StringBuilder();

                    var subDataNode = node[1];

                    //var subDataType = (int)subDataNode[0];
                    // What's known (subDataNode):
                    // 1 - additional data of "Authentication (Windows auth) in thin or thick client"
                    // 2 - additional data of "Authentication in COM connection" event
                    // 6 - additional data of "Authentication in thin or thick client" event
                    // 11 - additional data of "Access denied" event

                    // I hope this is temporarily method
                    var subDataCount = subDataNode.Count - 1;

                    if (subDataCount > 0)
                        for (var i = 0; i <= subDataCount; i++)
                        {
                            if (subDataNode[i].Count > 0)
                            {
                                var value = GetData(subDataNode[i]);

                                if (value != string.Empty)
                                    str.AppendLine($"Item {i}: {value}");
                            }
                            else
                            {
                                str.AppendLine($"frmt: {subDataNode[i]}");
                            }
                        }

                    return str.ToString();
                default:
                    return "";
            }
        }

        private static string GetTransactionPresentation(string str)
        {
            return str switch
            {
                "U" => "Зафиксирована",
                "C" => "Отменена",
                "R" => "Не завершена",
                "N" => "Нет транзакции",
                _ => ""
            };
        }

        private static string GetSeverityPresentation(string str)
        {
            return str switch
            {
                "I" => "Информация",
                "E" => "Ошибка",
                "W" => "Предупреждение",
                "N" => "Примечание",
                _ => ""
            };
        }

        private static string GetApplicationPresentation(string str)
        {
            return str switch
            {
                "1CV8" => "Толстый клиент",
                "1CV8C" => "Тонкий клиент",
                "WebClient" => "Веб-клиент",
                "Designer" => "Конфигуратор",
                "COMConnection" => "Внешнее соединение (COM, обычное)",
                "WSConnection" => "Сессия web-сервиса",
                "BackgroundJob" => "Фоновое задание",
                "SystemBackgroundJob" => "Системное фоновое задание",
                "SrvrConsole" => "Консоль кластера",
                "COMConsole" => "Внешнее соединение (COM, административное)",
                "JobScheduler" => "Планировщик заданий",
                "Debugger" => "Отладчик",
                "RAS" => "Сервер администрирования",
                _ => str
            };
        }

        private static string GetEventPresentation(string str)
        {
            return str switch
            {
                "_$Access$_.Access" => "Доступ. Доступ",
                "_$Access$_.AccessDenied" => "Доступ. Отказ в доступе",
                "_$Data$_.Delete" => "Данные. Удаление",
                "_$Data$_.DeletePredefinedData" => " Данные. Удаление предопределенных данных",
                "_$Data$_.DeleteVersions" => "Данные. Удаление версий",
                "_$Data$_.New" => "Данные. Добавление",
                "_$Data$_.NewPredefinedData" => "Данные. Добавление предопределенных данных",
                "_$Data$_.NewVersion" => "Данные. Добавление версии",
                "_$Data$_.Pos" => "Данные. Проведение",
                "_$Data$_.PredefinedDataInitialization" => "Данные. Инициализация предопределенных данных",
                "_$Data$_.PredefinedDataInitializationDataNotFound" => "Данные. Инициализация предопределенных данных.Данные не найдены",
                "_$Data$_.SetPredefinedDataInitialization" => "Данные. Установка инициализации предопределенных данных",
                "_$Data$_.SetStandardODataInterfaceContent" => "Данные. Изменение состава стандартного интерфейса OData",
                "_$Data$_.TotalsMaxPeriodUpdate" => "Данные. Изменение максимального периода рассчитанных итогов",
                "_$Data$_.TotalsMinPeriodUpdate" => "Данные. Изменение минимального периода рассчитанных итогов",
                "_$Data$_.Post" => "Данные. Проведение",
                "_$Data$_.Unpost" => "Данные. Отмена проведения",
                "_$Data$_.Update" => "Данные. Изменение",
                "_$Data$_.UpdatePredefinedData" => "Данные. Изменение предопределенных данных",
                "_$Data$_.VersionCommentUpdate" => "Данные. Изменение комментария версии",
                "_$InfoBase$_.ConfigExtensionUpdate" => "Информационная база. Изменение расширения конфигурации",
                "_$InfoBase$_.ConfigUpdate" => "Информационная база. Изменение конфигурации",
                "_$InfoBase$_.DBConfigBackgroundUpdateCancel" => "Информационная база. Отмена фонового обновления",
                "_$InfoBase$_.DBConfigBackgroundUpdateFinish" => "Информационная база. Завершение фонового обновления",
                "_$InfoBase$_.DBConfigBackgroundUpdateResume" => "Информационная база. Продолжение (после приостановки) процесса фонового обновления",
                "_$InfoBase$_.DBConfigBackgroundUpdateStart" => "Информационная база. Запуск фонового обновления",
                "_$InfoBase$_.DBConfigBackgroundUpdateSuspend" => "Информационная база. Приостановка (пауза) процесса фонового обновления",
                "_$InfoBase$_.DBConfigExtensionUpdate" => "Информационная база. Изменение расширения конфигурации",
                "_$InfoBase$_.DBConfigExtensionUpdateError" => "Информационная база. Ошибка изменения расширения конфигурации",
                "_$InfoBase$_.DBConfigUpdate" => "Информационная база. Изменение конфигурации базы данных",
                "_$InfoBase$_.DBConfigUpdateStart" => "Информационная база. Запуск обновления конфигурации базы данных",
                "_$InfoBase$_.DBConfigUpdateError" => "Информационная база. Ошибка изменения конфигурации базы данных",
                "_$InfoBase$_.DumpError" => "Информационная база. Ошибка выгрузки в файл",
                "_$InfoBase$_.DumpFinish" => "Информационная база. Окончание выгрузки в файл",
                "_$InfoBase$_.DumpStart" => "Информационная база. Начало выгрузки в файл",
                "_$InfoBase$_.EraseData" => " Информационная база. Удаление данных информационной баз",
                "_$InfoBase$_.EventLogReduce" => "Информационная база. Сокращение журнала регистрации",
                "_$InfoBase$_.EventLogReduceError" => "Информационная база. Ошибка сокращения журнала регистрации",
                "_$InfoBase$_.EventLogSettingsUpdate" => "Информационная база. Изменение параметров журнала регистрации",
                "_$InfoBase$_.EventLogSettingsUpdateError" => "Информационная база. Ошибка при изменение настроек журнала регистрации",
                "_$InfoBase$_.InfoBaseAdmParamsUpdate" => "Информационная база. Изменение параметров информационной базы",
                "_$InfoBase$_.InfoBaseAdmParamsUpdateError" => "Информационная база. Ошибка изменения параметров информационной базы",
                "_$InfoBase$_.IntegrationServiceActiveUpdate" => "Информационная база. Изменение активности сервиса интеграции",
                "_$InfoBase$_.IntegrationServiceSettingsUpdate" => "Информационная база. Изменение настроек сервиса интеграции",
                "_$InfoBase$_.MasterNodeUpdate" => "Информационная база. Изменение главного узла",
                "_$InfoBase$_.PredefinedDataUpdate" => "Информационная база. Обновление предопределенных данных",
                "_$InfoBase$_.RegionalSettingsUpdate" => "Информационная база. Изменение региональных установок",
                "_$InfoBase$_.RestoreError" => "Информационная база. Ошибка загрузки из файла",
                "_$InfoBase$_.RestoreFinish" => "Информационная база. Окончание загрузки из файла",
                "_$InfoBase$_.RestoreStart" => "Информационная база. Начало загрузки из файла",
                "_$InfoBase$_.SecondFactorAuthTemplateDelete" => "Информационная база. Удаление шаблона вторго фактора аутентификации",
                "_$InfoBase$_.SecondFactorAuthTemplateNew" => "Информационная база. Добавление шаблона вторго фактора аутентификации",
                "_$InfoBase$_.SecondFactorAuthTemplateUpdate" => "Информационная база. Изменение шаблона вторго фактора аутентификации",
                "_$InfoBase$_.SetPredefinedDataUpdate" => "Информационная база. Установить обновление предопределенных данных",
                "_$InfoBase$_.TARImportant" => "Тестирование и исправление. Ошибка",
                "_$InfoBase$_.TARInfo" => "Тестирование и исправление. Сообщение",
                "_$InfoBase$_.TARMess" => "Тестирование и исправление. Предупреждение",
                "_$Job$_.Cancel" => "Фоновое задание. Отмена",
                "_$Job$_.Fail" => "Фоновое задание. Ошибка выполнения",
                "_$Job$_.Start" => "Фоновое задание. Запуск",
                "_$Job$_.Succeed" => "Фоновое задание. Успешное завершение",
                "_$Job$_.Terminate" => "Фоновое задание. Принудительное завершение",
                "_$Job$_.Finish" => "Фоновое задание. Успешное завершение",
                "_$Job$_.Error" => "Фоновое задание. Ошибка выполнения",
                "_$Licensing$_.ConfigUpdate" => "_$Licensing$_.ConfigUpdate",
                "_$OpenIDProvider$_.NegativeAssertion" => "Провайдер OpenID. Отклонено",
                "_$OpenIDProvider$_.PositiveAssertion" => "Провайдер OpenID. Подтверждено",
                "_$PerformError$_" => "Ошибка выполнения",
                "_$Session$_.Authentication" => "Сеанс. Аутентификация",
                "_$Session$_.AuthenticationError" => "Сеанс. Ошибка аутентификации",
                "_$Session$_.AuthenticationFirstFactor" => "Сеанс. Аутентификация первый фактор",
                "_$Session$_.ConfigExtensionApplyError" => "Сеанс. Ошибка применения расширения конфигурации",
                "_$Session$_.Finish" => "Сеанс. Завершение",
                "_$Session$_.Start" => "Сеанс. Начало",
                "_$Transaction$_.Begin" => "Транзакция. Начало",
                "_$Transaction$_.Commit" => "Транзакция. Фиксация",
                "_$Transaction$_.Rollback" => "Транзакция. Отмена",
                "_$User$_.AuthenticationLock" => "Пользователи. Блокировка аутентификации",
                "_$User$_.AuthenticationUnlock" => "Пользователи. Разблокировка аутентификации",
                "_$User$_.AuthenticationUnlockError " => "Пользователи. Ошибка разблокировки аутентификации",
                "_$User$_.Delete" => "Пользователи. Удаление",
                "_$User$_.DeleteError" => "Пользователи. Ошибка удаления",
                "_$User$_.New" => "Пользователи. Добавление",
                "_$User$_.NewError" => "Пользователи. Ошибка добавления",
                "_$User$_.Update" => "Пользователи. Изменение",
                "_$User$_.UpdateError" => "Пользователи. Ошибка изменения",
                "_$User$_.PermissionChange" => "Пользователи. Изменение прав доступа",
                "_$InfoBase$_.RoleUpdate" => "Информационная база. Изменение роли",
                "_$InfoBase$_.SessionLockChange" => "Информационная база. Изменение блокировки начала сеансов",
                "_$InfoBase$_.SessionLockChangeError" => "Информационная база. Ошибка изменения блокировки начала сеансов",
                "_$InfoBase$_.RegionalSettingsChange" => "Информационная база. Изменение региональных установок",
                "_$InfoBase$_.RegionalSettingsChangeError" => "Информационная база. Ошибка изменения региональных установок",
                "_$InfoBase$_.EraseDataError" => "Информацbонная база. Ошибка удаления данных информационной базы",
                "_$InfoBase$_.ConfigUpdateError" => "Информационная база. Ошибка изменения конфигурации",
                "_$InfoBase$_.DBConfigBackgroundUpdateError" => "Информационная база. Ошибка фонового обновления конфигурации базы данных",
                "_$InfoBase$_.DBConfigExtensionInsert" => "Информационная база. Добавление расширения конфигурации базы данных",
                "_$InfoBase$_.DBConfigExtensionInsertError" => "Информационная база. Ошибка добавления расширения конфигурации базы данных",
                "_$InfoBase$_.DBConfigExtensionDelete" => "Информационная база. Удаление расширения конфигурации базы данных",
                "_$InfoBase$_.DBConfigExtensionDeleteError" => "Информационная база. Ошибка удаления расширения конфигурации базы данных",
                "_$InfoBase$_.ExclusiveModeChange" => "Информационная база. Изменение режима монопольного доступа к текущей области",
                "_$InfoBase$_.ExclusiveModeChangeError" => "Информационная база. Ошибка изменения режима монопольного доступа к текущей области",
                "_$InfoBase$_.AdministrationParametersChange" => "Информационная база. Изменение административных параметров",
                "_$InfoBase$_.SecurityProfileChange" => "Информационная база. Изменение профиля безопасности",
                "_$Session$_.DataZoneChange" => "Сеанс. Изменение текущей области",
                "_$Session$_.DataZoneChangeError" => "Сеанс. Ошибка изменения текущей области",
                "_$Session$_.ExternalDataProcessorConnect" => "Сеанс. Подключение внешней обработки",
                "_$Session$_.ExternalDataProcessorConnectError" => "Сеанс. Ошибка подключения внешней обработки",
                "_$Session$_.ExternalReportConnect" => "Сеанс. Подключение внешнего отчета",
                "_$Session$_.ExternalReportConnectError" => "Сеанс. Ошибка подключения внешнего отчета",
                "_$Session$_.AddInAttach" => "Сеанс. Подключение внешней компоненты",
                "_$Session$_.AddInAttachError" => "Сеанс. Ошибка подключения внешней компоненты",
                "_$Data$_.DataHistorySettingsUpdate" => "Данные. Изменение настроек истории данных",
                "_$InfoBase$_.ParametersUpdate" => "Информационная база. Изменение параметров информационной базы",
                "_$InfoBase$_.VerifyAndRepairInfo" => "Тестирование и исправление. Сообщение",
                "_$InfoBase$_.VerifyAndRepairMessage" => "Тестирование и исправление. Предупреждение",
                "_$InfoBase$_.UserPasswordPolicyNew" => "Информационная база. Добавление политики паролей пользователей",
                _ => str
            };
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposedValue) return;

            _bracketsReader?.Dispose();
            _bracketsReader = null;
            _fileStream = null;

            _lgpFileWatcher?.Dispose();
            _lgpFileWatcher = null;

            _lgfReader = null;

            _disposedValue = true;
        }

        ~LgpReader()
        {
            Dispose(false);
        }
    }
}