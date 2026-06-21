using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.IO;
using System.Net;
using System.Text;
using System.Collections.Generic;
using VkNet;
using VkNet.Enums.SafetyEnums;
using VkNet.Model.RequestParams;
using VkNet.Model.Keyboard;
using VkNet.Utils;

public class BotService
{
    private readonly VkApi _api;
    private readonly CommandHandler _commandHandler;
    private readonly PollManager _pollManager;
    private readonly long _groupId;

    // Preconfigured mapping: filename -> attachment id (already uploaded in community album)
    private readonly Dictionary<string, string> _bannerAttachments = new()
    {
        { "bannermain.png", "photo-239555205_457239023" },
        { "bannergorod.png", "photo-239555205_457239022" },
        { "bannermeropryatie.png", "photo-239555205_457239024" },
        { "bannergolosovanie.png", "photo-239555205_457239021" }
    };

    public BotService(VkApi api, CommandHandler commandHandler, PollManager pollManager, long groupId)
    {
        _api = api;
        _commandHandler = commandHandler;
        _poll_manager_guard(pollManager); // no-op
        _pollManager = pollManager;
        _groupId = groupId;
    }

    private void _poll_manager_guard(PollManager pm) { /* no-op */ }

    public void Run()
    {
        Logger.Success("Бот запущен!");

        try
        {
            // Попробуем включить TLS 1.2/1.1/1.0 (иногда нужно на старых платформах)
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |=
                    System.Net.SecurityProtocolType.Tls12 |
                    System.Net.SecurityProtocolType.Tls11 |
                    System.Net.SecurityProtocolType.Tls;
                Logger.Info($"SecurityProtocol set to: {System.Net.ServicePointManager.SecurityProtocol}");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Не удалось установить ServicePointManager.SecurityProtocol: {ex}");
            }

            // Получаем LongPollServer с retry
            const int maxAttempts = 6;
            int attempt = 0;
            dynamic server = null;

            while (attempt < maxAttempts)
            {
                attempt++;
                try
                {
                    server = _api.Groups.GetLongPollServer((ulong)_groupId);
                    Logger.Info("Long Poll сервер подключен");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Попытка {attempt}/{maxAttempts}: Не удалось получить LongPollServer: {ex.Message}");
                    if (attempt >= maxAttempts)
                    {
                        Logger.Error("Достигнут предел попыток получения LongPollServer — прекращаем работу.");
                        throw;
                    }

                    int delayMs = 1000 * (int)Math.Pow(2, attempt); // 2s,4s,8s...
                    Logger.Info($"Повтор через {delayMs} ms...");
                    Thread.Sleep(delayMs);
                }
            }

            if (server == null)
                throw new Exception("Не удалось инициализировать LongPollServer (server == null).");

            // Основной цикл обработки LongPoll
            while (true)
            {
                try
                {
                    var history = _api.Groups.GetBotsLongPollHistory(new BotsLongPollHistoryParams
                    {
                        Key = server.Key,
                        Server = server.Server,
                        Ts = server.Ts
                    });

                    server.Ts = history.Ts;

                    foreach (var u in history.Updates)
                    {
                        try
                        {
                            // Попробуем получить сообщение устойчиво к разным версиям VkNet.
                            object msgObj = null;

#pragma warning disable 618
                            try
                            {
                                if (u.MessageNew != null)
                                    msgObj = u.MessageNew.Message;
                            }
                            catch { }
#pragma warning restore 618

                            if (msgObj == null && u.Instance != null)
                            {
                                try
                                {
                                    dynamic inst = u.Instance;

                                    try { if (inst.MessageNew != null) msgObj = inst.MessageNew.Message; } catch { }
                                    if (msgObj == null) { try { if (inst.Message != null) msgObj = inst.Message; } catch { } }
                                    if (msgObj == null) { try { if (inst.NewMessage != null) msgObj = inst.NewMessage.Message; } catch { } }
                                }
                                catch { }
                            }

                            if (msgObj == null)
                                continue;

                            dynamic msg = msgObj;

                            // Попытка получить payload (если кнопка отправила payload)
                            string payload = null;
                            try { payload = (string)msg.Payload; } catch { }
                            if (string.IsNullOrWhiteSpace(payload))
                            {
                                try
                                {
                                    var payloadObj = msg.payload;
                                    if (payloadObj != null)
                                        payload = JsonSerializer.Serialize(payloadObj);
                                }
                                catch { }
                            }

                            // Получаем текст сообщения (совместимо с разными именами свойства)
                            string rawText = null;
                            try { rawText = (string)msg.Text; } catch { }
                            if (string.IsNullOrWhiteSpace(rawText))
                            {
                                try { rawText = (string)msg.text; } catch { }
                            }

                            // Если payload задан — используем его (cmd или vote) в приоритете
                            string textForHandler = rawText ?? string.Empty;

                            if (!string.IsNullOrWhiteSpace(payload))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(payload);
                                    var root = doc.RootElement;

                                    if (root.TryGetProperty("cmd", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String)
                                    {
                                        textForHandler = cmdEl.GetString() ?? textForHandler;
                                    }
                                    else if (root.TryGetProperty("vote", out var voteEl))
                                    {
                                        if (voteEl.ValueKind == JsonValueKind.Number && voteEl.TryGetInt32(out int idx))
                                        {
                                            textForHandler = (idx + 1).ToString();
                                        }
                                        else if (voteEl.ValueKind == JsonValueKind.String && int.TryParse(voteEl.GetString(), out int idx2))
                                        {
                                            textForHandler = (idx2 + 1).ToString();
                                        }
                                    }
                                }
                                catch (JsonException) { /* payload не в JSON — проигнорируем */ }
                                catch (Exception ex)
                                {
                                    Logger.Error($"Ошибка парсинга payload: {ex}");
                                }
                            }

                            // Если payload не дал результата, очистим возможный префикс вида "[club239555205|Пост] "
                            if (string.IsNullOrWhiteSpace(textForHandler) && !string.IsNullOrWhiteSpace(rawText))
                                textForHandler = rawText;

                            if (!string.IsNullOrWhiteSpace(textForHandler))
                            {
                                // удаляем ведущие упоминания в квадратных скобках, например "[club239555205|Пост] Создать"
                                textForHandler = Regex.Replace(textForHandler, @"^\[[^\]]+\]\s*", "");
                                textForHandler = textForHandler.Trim();
                            }

                            if (string.IsNullOrWhiteSpace(textForHandler))
                                continue;

                            // Получаем peerId и fromId безопасно
                            long peerId = SafeToLong(msg.PeerId ?? msg.peer_id ?? msg.PeerIdLocal ?? null);
                            long userId = SafeToLong(msg.FromId ?? msg.from_id ?? null);

                            Logger.Info($"[PeerId: {peerId}] Сообщение: {textForHandler}");

                            string response = _commandHandler.HandleMessage(peerId, userId, textForHandler);

                            if (string.IsNullOrWhiteSpace(response))
                                continue;

                            SendMessage(peerId, response);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Ошибка обработки сообщения: {ex}");
                        }
                    }

                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка получения истории: {ex}");
                    Thread.Sleep(2000);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Критическая ошибка: {ex}");
        }
    }

    private void SendMessage(long peerId, string message)
    {
        try
        {
            var keyboardObj = BuildKeyboardForPeer(peerId); // MessageKeyboard for MessagesSendParams branch
            var keyboardJson = BuildKeyboardJsonForPeer(peerId); // JSON string for messages.send branch

            // Отключаем баннер для сообщений "Добавлено" и для подтверждений голосования
            if (!string.IsNullOrWhiteSpace(message))
            {
                var tmsg = message.TrimStart();
                if (tmsg.StartsWith("✅ Добавлено", StringComparison.Ordinal) ||
                    tmsg.IndexOf("Добавлено:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmsg.StartsWith("✅ Голос принят", StringComparison.Ordinal) ||
                    tmsg.IndexOf("Голос принят", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmsg.StartsWith("⚠️ Вы уже голосовали", StringComparison.Ordinal) ||
                    tmsg.IndexOf("Вы уже голосовали", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Info("Message is a confirmation (Добавлено/Голос принят/Вы уже голосовали) — banner suppressed for this response.");
                    var sendParamsSuppressed = new MessagesSendParams
                    {
                        PeerId = peerId,
                        RandomId = Random.Shared.NextInt64(),
                        Message = message,
                        Keyboard = keyboardObj
                    };
                    _api.Messages.Send(sendParamsSuppressed);
                    Logger.Success("Ответ отправлен (без баннера).");
                    return;
                }
            }

            // Определяем баннер (путь) и попробуем использовать заранее заданный attachment
            string? bannerPath = SelectBannerForMessage(peerId, message);
            string? attachmentString = null;

            if (!string.IsNullOrWhiteSpace(bannerPath))
            {
                Logger.Info($"Banner requested: {bannerPath}");
                var fname = Path.GetFileName(bannerPath);

                // если у нас есть заранее загруженный attachment для имени файла — используем его напрямую
                if (!string.IsNullOrWhiteSpace(fname) && _bannerAttachments.TryGetValue(fname, out var preAttachment))
                {
                    attachmentString = preAttachment;
                    Logger.Info($"Using preconfigured attachment for {fname}: {preAttachment}");
                }
                else
                {
                    // fallback: если есть локальный файл — попытаться загрузить (если токен и права позволяют)
                    try
                    {
                        string path = bannerPath;

                        if (Uri.IsWellFormedUriString(path, UriKind.Absolute) && (path.StartsWith("http://") || path.StartsWith("https://")))
                        {
                            var tmp = Path.GetTempFileName();
                            using (var wc = new WebClient()) wc.DownloadFile(path, tmp);
                            path = tmp;
                        }

                        if (File.Exists(path))
                        {
                            dynamic uploadServer = null;
                            try
                            {
                                uploadServer = _api.Photo.GetMessagesUploadServer((long?)_groupId);
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Не удалось получить upload server (photos.getMessagesUploadServer): {ex}");
                                uploadServer = null;
                            }

                            string uploadUrl = null;
                            try
                            {
                                if (uploadServer != null)
                                    uploadUrl = uploadServer.UploadUrl ?? uploadServer.upload_url ?? uploadServer.UploadUrl?.ToString();
                            }
                            catch { uploadUrl = null; }

                            Logger.Info($"UploadUrl: {uploadUrl ?? "<null>"}");

                            if (!string.IsNullOrWhiteSpace(uploadUrl))
                            {
                                using var webClient = new WebClient();
                                var responseBytes = webClient.UploadFile(uploadUrl, path);
                                var responseJson = Encoding.UTF8.GetString(responseBytes);
                                Logger.Info($"Upload response: {responseJson}");

                                using var doc = JsonDocument.Parse(responseJson);
                                var root = doc.RootElement;

                                if (root.TryGetProperty("photo", out var photoEl) &&
                                    root.TryGetProperty("server", out var serverEl) &&
                                    root.TryGetProperty("hash", out var hashEl))
                                {
                                    var photo = photoEl.GetString() ?? string.Empty;
                                    var server = serverEl.ValueKind == JsonValueKind.Number ? serverEl.GetInt32() : 0;
                                    var hash = hashEl.GetString() ?? string.Empty;

                                    var saveParams = new VkNet.Utils.VkParameters
                                    {
                                        { "photo", photo },
                                        { "server", server },
                                        { "hash", hash }
                                    };

                                    try
                                    {
                                        var saved = _api.Call("photos.saveMessagesPhoto", saveParams);
                                        Logger.Info($"Saved response (raw): {saved?.ToString() ?? "<null>"}");
                                        if (saved != null)
                                        {
                                            try
                                            {
                                                var first = saved[0];
                                                if (first != null)
                                                {
                                                    var ownerObj = first["owner_id"] ?? first["owner"];
                                                    var idObj = first["id"];
                                                    if (ownerObj != null && idObj != null)
                                                    {
                                                        var ownerStr = ownerObj.ToString();
                                                        var idStr = idObj.ToString();
                                                        if (!string.IsNullOrWhiteSpace(ownerStr) && !string.IsNullOrWhiteSpace(idStr))
                                                            attachmentString = $"photo{ownerStr}_{idStr}";
                                                    }
                                                }
                                            }
                                            catch
                                            {
                                                try
                                                {
                                                    var savedJson = saved.ToString();
                                                    using var savedDoc = JsonDocument.Parse(savedJson);
                                                    var arr = savedDoc.RootElement;
                                                    if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                                                    {
                                                        var el = arr[0];
                                                        if (el.TryGetProperty("owner_id", out var ownerEl) && el.TryGetProperty("id", out var idEl))
                                                            attachmentString = $"photo{ownerEl.GetInt64()}_{idEl.GetInt64()}";
                                                    }
                                                }
                                                catch (Exception exInner)
                                                {
                                                    Logger.Error($"Не удалось распарсить результат photos.saveMessagesPhoto: {exInner}");
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception exSave)
                                    {
                                        Logger.Error($"Ошибка вызова photos.saveMessagesPhoto: {exSave}");
                                    }
                                }
                                else
                                {
                                    Logger.Warning("Upload response did not contain photo/server/hash — cannot save.");
                                }
                            }
                            else
                            {
                                Logger.Warning("UploadUrl пустой или upload server не получен — баннер не будет отправлен.");
                            }

                            if (Path.GetTempPath() != null && path.StartsWith(Path.GetTempPath()))
                            {
                                try { File.Delete(path); } catch { }
                            }
                        }
                        else
                        {
                            Logger.Warning($"Banner file not found at: {path}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Не удалось прикрепить баннер через upload fallback: {ex}");
                    }
                }
            }
            else
            {
                Logger.Info("No banner requested for this message/state.");
            }

            // Если у нас есть готовый attachmentString (photo...), используем низкоуровневый вызов через VkParameters
            if (!string.IsNullOrWhiteSpace(attachmentString))
            {
                var parameters = new VkParameters
                {
                    { "peer_id", peerId },
                    { "random_id", Random.Shared.NextInt64() },
                    { "message", message },
                    { "attachment", attachmentString }
                };

                // добавляем keyboard JSON (в корректном формате) если есть
                if (!string.IsNullOrWhiteSpace(keyboardJson))
                {
                    parameters.Add("keyboard", keyboardJson);
                }

                // используем messages.send низкоуровнево
                _api.Call("messages.send", parameters);

                Logger.Success("Ответ отправлен (через VkParameters с attachment).");
                return;
            }

            // Иначе используем MessagesSendParams (без attachment)
            var sendParams = new MessagesSendParams
            {
                PeerId = peerId,
                RandomId = Random.Shared.NextInt64(),
                Message = message,
                Keyboard = keyboardObj
            };

            _api.Messages.Send(sendParams);
            Logger.Success("Ответ отправлен");
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка отправки сообщения: {ex}");
        }
    }

    // Build keyboard JSON in the VK API format (snake_case keys, buttons as array of arrays)
    private string BuildKeyboardJsonForPeer(long peerId)
    {
        try
        {
            var state = _pollManager.GetState(peerId);

            List<List<Dictionary<string, object>>> rows = new();

            void AddButtonToRow(int rowIndex, string label, string payload, string color)
            {
                while (rows.Count <= rowIndex)
                    rows.Add(new List<Dictionary<string, object>>());

                var action = new Dictionary<string, object>
                {
                    { "type", "text" },
                    { "label", label },
                    { "payload", payload } // payload should be string
                };

                var btn = new Dictionary<string, object>
                {
                    { "action", action },
                    { "color", color }
                };

                rows[rowIndex].Add(btn);
            }

            int currentRow = 0;

            if (state == BotState.Voting)
            {
                var options = _pollManager.GetCurrentOptions(peerId);
                if (options != null && options.Count > 0)
                {
                    for (int i = 0; i < options.Count; i++)
                    {
                        var label = (i + 1).ToString();
                        var payload = $"{{\"vote\":{i}}}";
                        AddButtonToRow(currentRow, label, payload, "primary");

                        if ((i + 1) % 3 == 0 && i + 1 < options.Count)
                            currentRow++;
                    }

                    currentRow++;
                    AddButtonToRow(currentRow, "Результат", "{\"cmd\":\"/result\"}", "default");
                    AddButtonToRow(currentRow, "Завершить", "{\"cmd\":\"/end\"}", "negative");
                }
            }
            else if (state == BotState.WaitingTitle)
            {
                AddButtonToRow(0, "Отмена", "{\"cmd\":\"/cancel\"}", "negative");
                AddButtonToRow(0, "Помощь", "{\"cmd\":\"/help\"}", "positive");
            }
            else if (state == BotState.WaitingOptions)
            {
                AddButtonToRow(0, "Начать", "{\"cmd\":\"/start\"}", "primary");
                AddButtonToRow(0, "Отмена", "{\"cmd\":\"/cancel\"}", "negative");
                currentRow++;
                AddButtonToRow(currentRow, "Варианты", "{\"cmd\":\"/options\"}", "default");
                AddButtonToRow(currentRow, "Помощь", "{\"cmd\":\"/help\"}", "positive");
            }
            else if (state == BotState.WaitingEventCategory)
            {
                // Показать кнопки с номерами категорий, если они есть
                var session = _poll_manager_session(peerId);
                var cats = session.AvailableCategories;
                if (cats != null && cats.Count > 0)
                {
                    for (int i = 0; i < cats.Count; i++)
                    {
                        var label = (i + 1).ToString();
                        var payload = $"{{\"vote\":{i}}}";
                        AddButtonToRow(currentRow, label, payload, "primary");

                        if ((i + 1) % 3 == 0 && i + 1 < cats.Count)
                            currentRow++;
                    }

                    currentRow++;
                    AddButtonToRow(currentRow, "Отмена", "{\"cmd\":\"/cancel\"}", "negative");
                    AddButtonToRow(currentRow, "Помощь", "{\"cmd\":\"/help\"}", "positive");
                }
                else
                {
                    // fallback minimal
                    AddButtonToRow(0, "Отмена", "{\"cmd\":\"/cancel\"}", "negative");
                    AddButtonToRow(0, "Помощь", "{\"cmd\":\"/help\"}", "positive");
                }
            }
            else if (state == BotState.WaitingCity)
            {
                var session = _poll_manager_session(peerId);
                var cities = session.AvailableCategories;
                if (cities != null && cities.Count > 0)
                {
                    for (int i = 0; i < cities.Count; i++)
                    {
                        var label = (i + 1).ToString();
                        var payload = $"{{\"vote\":{i}}}";
                        AddButtonToRow(currentRow, label, payload, "primary");

                        if ((i + 1) % 3 == 0 && i + 1 < cities.Count)
                            currentRow++;
                    }

                    currentRow++;
                    AddButtonToRow(currentRow, "Отмена", "{\"cmd\":\"/cancel\"}", "negative");
                    AddButtonToRow(currentRow, "Помощь", "{\"cmd\":\"/help\"}", "positive");
                }
                else
                {
                    AddButtonToRow(0, "Отмена", "{\"cmd\":\"/cancel\"}", "negative");
                    AddButtonToRow(0, "Помощь", "{\"cmd\":\"/help\"}", "positive");
                }
            }
            else // default main keyboard
            {
                AddButtonToRow(0, "Создать", "{\"cmd\":\"/create\"}", "primary");
                AddButtonToRow(0, "Мероприятия", "{\"cmd\":\"/events\"}", "primary");
                currentRow++;
                AddButtonToRow(currentRow, "Города", "{\"cmd\":\"/cities\"}", "default");
                AddButtonToRow(currentRow, "Результат", "{\"cmd\":\"/result\"}", "default");
                currentRow++;
                AddButtonToRow(currentRow, "Помощь", "{\"cmd\":\"/help\"}", "positive");
                AddButtonToRow(currentRow, "Отмена", "{\"cmd\":\"/cancel\"}", "negative");
            }

            var root = new Dictionary<string, object>
            {
                { "one_time", false },
                { "buttons", rows }
            };

            var json = JsonSerializer.Serialize(root);
            return json;
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка построения keyboard JSON: {ex}");
            return string.Empty;
        }
    }

    private MessageKeyboard? BuildKeyboardForPeer(long peerId)
    {
        try
        {
            var state = _pollManager.GetState(peerId);
            var kb = new KeyboardBuilder(true);

            // Если мы в процессе голосования — показываем кнопки с номерами вариантов
            if (state == BotState.Voting)
            {
                var options = _pollManager.GetCurrentOptions(peerId);
                if (options != null && options.Count > 0)
                {
                    for (int i = 0; i < options.Count; i++)
                    {
                        var label = (i + 1).ToString();
                        var action = new MessageKeyboardButtonAction
                        {
                            Type = KeyboardButtonActionType.Text,
                            Label = label,
                            Payload = $"{{\"vote\":{i}}}"
                        };
                        kb.AddButton(action, KeyboardButtonColor.Primary);

                        if ((i + 1) % 3 == 0 && i + 1 < options.Count)
                            kb.AddLine();
                    }

                    kb.AddLine();

                    kb.AddButton(new MessageKeyboardButtonAction
                    {
                        Type = KeyboardButtonActionType.Text,
                        Label = "Результат",
                        Payload = "{\"cmd\":\"/result\"}"
                    }, KeyboardButtonColor.Default);

                    kb.AddButton(new MessageKeyboardButtonAction
                    {
                        Type = KeyboardButtonActionType.Text,
                        Label = "Завершить",
                        Payload = "{\"cmd\":\"/end\"}"
                    }, KeyboardButtonColor.Negative);

                    return kb.Build();
                }
            }

            // Если ожидаем название — даём кнопку отмены и помощь
            if (state == BotState.WaitingTitle)
            {
                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Отмена",
                    Payload = "{\"cmd\":\"/cancel\"}"
                }, KeyboardButtonColor.Negative);

                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Помощь",
                    Payload = "{\"cmd\":\"/help\"}"
                }, KeyboardButtonColor.Positive);

                return kb.Build();
            }

            // Если ожидаем варианты (создание голосования) — показываем кнопки "Начать", "Отмена", "Варианты", "Помощь"
            if (state == BotState.WaitingOptions)
            {
                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Начать",
                    Payload = "{\"cmd\":\"/start\"}"
                }, KeyboardButtonColor.Primary);

                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Отмена",
                    Payload = "{\"cmd\":\"/cancel\"}"
                }, KeyboardButtonColor.Negative);

                kb.AddLine();

                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Варианты",
                    Payload = "{\"cmd\":\"/options\"}"
                }, KeyboardButtonColor.Default);

                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Помощь",
                    Payload = "{\"cmd\":\"/help\"}"
                }, KeyboardButtonColor.Positive);

                return kb.Build();
            }

            // WaitingEventCategory — показываем кнопки категорий (если есть) + Отмена/Помощь
            if (state == BotState.WaitingEventCategory)
            {
                var session = _poll_manager_session(peerId);
                var cats = session.AvailableCategories;
                if (cats != null && cats.Count > 0)
                {
                    for (int i = 0; i < cats.Count; i++)
                    {
                        var label = (i + 1).ToString();
                        var action = new MessageKeyboardButtonAction
                        {
                            Type = KeyboardButtonActionType.Text,
                            Label = label,
                            Payload = $"{{\"vote\":{i}}}"
                        };
                        kb.AddButton(action, KeyboardButtonColor.Primary);

                        if ((i + 1) % 3 == 0 && i + 1 < cats.Count)
                            kb.AddLine();
                    }

                    kb.AddLine();

                    kb.AddButton(new MessageKeyboardButtonAction
                    {
                        Type = KeyboardButtonActionType.Text,
                        Label = "Отмена",
                        Payload = "{\"cmd\":\"/cancel\"}"
                    }, KeyboardButtonColor.Negative);

                    kb.AddButton(new MessageKeyboardButtonAction
                    {
                        Type = KeyboardButtonActionType.Text,
                        Label = "Помощь",
                        Payload = "{\"cmd\":\"/help\"}"
                    }, KeyboardButtonColor.Positive);

                    return kb.Build();
                }

                // fallback
                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Отмена",
                    Payload = "{\"cmd\":\"/cancel\"}"
                }, KeyboardButtonColor.Negative);

                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Помощь",
                    Payload = "{\"cmd\":\"/help\"}"
                }, KeyboardButtonColor.Positive);

                return kb.Build();
            }

            // WaitingCity — после /cities отображаем кнопки с номерами городов
            if (state == BotState.WaitingCity)
            {
                var session = _poll_manager_session(peerId);
                var cities = session.AvailableCategories;
                if (cities != null && cities.Count > 0)
                {
                    for (int i = 0; i < cities.Count; i++)
                    {
                        var label = (i + 1).ToString();
                        var action = new MessageKeyboardButtonAction
                        {
                            Type = KeyboardButtonActionType.Text,
                            Label = label,
                            Payload = $"{{\"vote\":{i}}}"
                        };
                        kb.AddButton(action, KeyboardButtonColor.Primary);

                        if ((i + 1) % 3 == 0 && i + 1 < cities.Count)
                            kb.AddLine();
                    }

                    kb.AddLine();

                    kb.AddButton(new MessageKeyboardButtonAction
                    {
                        Type = KeyboardButtonActionType.Text,
                        Label = "Отмена",
                        Payload = "{\"cmd\":\"/cancel\"}"
                    }, KeyboardButtonColor.Negative);

                    kb.AddButton(new MessageKeyboardButtonAction
                    {
                        Type = KeyboardButtonActionType.Text,
                        Label = "Помощь",
                        Payload = "{\"cmd\":\"/help\"}"
                    }, KeyboardButtonColor.Positive);

                    return kb.Build();
                }

                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Отмена",
                    Payload = "{\"cmd\":\"/cancel\"}"
                }, KeyboardButtonColor.Negative);

                kb.AddButton(new MessageKeyboardButtonAction
                {
                    Type = KeyboardButtonActionType.Text,
                    Label = "Помощь",
                    Payload = "{\"cmd\":\"/help\"}"
                }, KeyboardButtonColor.Positive);

                return kb.Build();
            }

            // Основная клавиатура для остальных состояний
            kb.AddButton(new MessageKeyboardButtonAction
            {
                Type = KeyboardButtonActionType.Text,
                Label = "Создать",
                Payload = "{\"cmd\":\"/create\"}"
            }, KeyboardButtonColor.Primary);

            kb.AddButton(new MessageKeyboardButtonAction
            {
                Type = KeyboardButtonActionType.Text,
                Label = "Мероприятия",
                Payload = "{\"cmd\":\"/events\"}"
            }, KeyboardButtonColor.Primary);

            kb.AddLine();

            kb.AddButton(new MessageKeyboardButtonAction
            {
                Type = KeyboardButtonActionType.Text,
                Label = "Города",
                Payload = "{\"cmd\":\"/cities\"}"
            }, KeyboardButtonColor.Default);

            kb.AddButton(new MessageKeyboardButtonAction
            {
                Type = KeyboardButtonActionType.Text,
                Label = "Результат",
                Payload = "{\"cmd\":\"/result\"}"
            }, KeyboardButtonColor.Default);

            kb.AddLine();

            kb.AddButton(new MessageKeyboardButtonAction
            {
                Type = KeyboardButtonActionType.Text,
                Label = "Помощь",
                Payload = "{\"cmd\":\"/help\"}"
            }, KeyboardButtonColor.Positive);

            kb.AddButton(new MessageKeyboardButtonAction
            {
                Type = KeyboardButtonActionType.Text,
                Label = "Отмена",
                Payload = "{\"cmd\":\"/cancel\"}"
            }, KeyboardButtonColor.Negative);

            return kb.Build();
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка построения клавиатуры: {ex}");
            return null;
        }
    }

    private PollSession _poll_manager_session(long peerId) => _pollManager.GetOrCreateSession(peerId);

    private long SafeToLong(object? value)
    {
        try
        {
            if (value == null) return 0;
            if (value is long l) return l;
            if (value is int i) return i;
            if (value is ulong ul) return (long)ul;
            if (value is uint ui) return ui;
            if (value is string s && long.TryParse(s, out var parsed)) return parsed;
            return Convert.ToInt64(value);
        }
        catch { return 0; }
    }

    // Banner selection: tries multiple likely locations for local files (keeps existing behavior)
    private string? SelectBannerForMessage(long peerId, string message)
    {
        try
        {
            // If this is an "Добавлено" confirmation or similar, suppress banner
            if (!string.IsNullOrWhiteSpace(message))
            {
                var tmsg = message.TrimStart();
                if (tmsg.StartsWith("✅ Добавлено", StringComparison.Ordinal) ||
                    tmsg.IndexOf("Добавлено:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmsg.StartsWith("✅ Голос принят", StringComparison.Ordinal) ||
                    tmsg.IndexOf("Голос принят", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tmsg.StartsWith("⚠️ Вы уже голосовали", StringComparison.Ordinal) ||
                    tmsg.IndexOf("Вы уже голосовали", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return null;
                }
            }

            var candidates = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Assets", "Banners"),
                Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Banners"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "Assets", "Banners"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "..", "Assets", "Banners")
            };

            string? Resolve(string filename)
            {
                foreach (var baseDir in candidates)
                {
                    try
                    {
                        var p = Path.GetFullPath(Path.Combine(baseDir, filename));
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }

                if (File.Exists(filename)) return Path.GetFullPath(filename);

                // return candidate path even if file doesn't exist (we'll still use predefined attachment mapping by filename)
                return Path.Combine(candidates[0], filename);
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                var t = message.TrimStart();

                if (t.StartsWith("📊 ")) return Resolve("bannergolosovanie.png");
                if (t.StartsWith("🎭") || t.Contains("Выбери категорию")) return Resolve("bannermeropryatie.png");
                if (t.StartsWith("Доступные города:") || t.StartsWith("Доступные города")) return Resolve("bannergorod.png");
                if (t.StartsWith("Доступные команды:") || t.Contains("/create")) return Resolve("bannermain.png");
            }

            var session = _pollManager.GetOrCreateSession(peerId);
            switch (session.State)
            {
                case BotState.Voting: return Resolve("bannergolosovanie.png");
                case BotState.WaitingEventCategory: return Resolve("bannermeropryatie.png");
                case BotState.WaitingCity: return Resolve("bannergorod.png");
                default: return Resolve("bannermain.png");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка поиска баннера: {ex}");
            return null;
        }
    }
}