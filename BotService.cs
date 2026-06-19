using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using VkNet;
using VkNet.Enums.SafetyEnums;
using VkNet.Model.RequestParams;
using VkNet.Model.Keyboard;

public class BotService
{
    private readonly VkApi _api;
    private readonly CommandHandler _commandHandler;
    private readonly PollManager _pollManager;
    private readonly long _groupId;

    public BotService(VkApi api, CommandHandler commandHandler, PollManager pollManager, long groupId)
    {
        _api = api;
        _commandHandler = commandHandler;
        _pollManager = pollManager;
        _groupId = groupId;
    }

    public void Run()
    {
        Logger.Success("Бот запущен!");

        try
        {
            var server = _api.Groups.GetLongPollServer((ulong)_groupId);
            Logger.Info("Long Poll сервер подключен");

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

                                    try
                                    {
                                        if (inst.MessageNew != null)
                                            msgObj = inst.MessageNew.Message;
                                    }
                                    catch { }

                                    if (msgObj == null)
                                    {
                                        try
                                        {
                                            if (inst.Message != null)
                                                msgObj = inst.Message;
                                        }
                                        catch { }
                                    }

                                    if (msgObj == null)
                                    {
                                        try
                                        {
                                            if (inst.NewMessage != null)
                                                msgObj = inst.NewMessage.Message;
                                        }
                                        catch { }
                                    }
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
            var keyboard = BuildKeyboardForPeer(peerId);

            _api.Messages.Send(new MessagesSendParams
            {
                PeerId = peerId,
                RandomId = Random.Shared.NextInt64(),
                Message = message,
                Keyboard = keyboard
            });

            Logger.Success("Ответ отправлен");
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка отправки сообщения: {ex}");
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

            // WaitingEventCategory — минимальные кнопки (Отмена/Помощь)
            if (state == BotState.WaitingEventCategory)
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

    private long SafeToLong(object? value)
    {
        try
        {
            if (value == null)
                return 0;

            if (value is long l) return l;
            if (value is int i) return i;
            if (value is ulong ul) return (long)ul;
            if (value is uint ui) return ui;
            if (value is string s && long.TryParse(s, out var parsed)) return parsed;

            return Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }
}