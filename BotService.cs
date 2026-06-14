using System;
using System.Threading;
using VkNet;
using VkNet.Enums.SafetyEnums;
using VkNet.Model;
using VkNet.Model.RequestParams;

public class BotService
{
    private readonly VkApi _api;
    private readonly CommandHandler _commandHandler;
    private readonly long _groupId;

    public BotService(VkApi api, CommandHandler commandHandler, long groupId)
    {
        _api = api;
        _commandHandler = commandHandler;
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
                            if (u.Type != GroupUpdateType.MessageNew)
                                continue;

                            var msg = u.MessageNew.Message;

                            if (string.IsNullOrWhiteSpace(msg.Text))
                                continue;

                            Logger.Info($"[PeerId: {msg.PeerId}] Сообщение: {msg.Text}");

                            long peerId = msg.PeerId ?? 0;
                            long userId = msg.FromId ?? 0;
                            string response = _commandHandler.HandleMessage(peerId, userId, msg.Text);

                            if (string.IsNullOrWhiteSpace(response))
                                continue;

                            SendMessage(peerId, response);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Ошибка обработки сообщения: {ex.Message}");
                        }
                    }

                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Ошибка получения истории: {ex.Message}");
                    Thread.Sleep(2000);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Критическая ошибка: {ex.Message}");
        }
    }

    private void SendMessage(long peerId, string message)
    {
        try
        {
            _api.Messages.Send(new MessagesSendParams
            {
                PeerId = peerId,
                RandomId = DateTime.UtcNow.Ticks,
                Message = message
            });

            Logger.Success("Ответ отправлен");
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка отправки сообщения: {ex.Message}");
        }
    }
}