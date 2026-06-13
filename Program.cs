using System;
using VkNet;
using VkNet.Model;
using VkNet.Enums.SafetyEnums;
using VkNet.Model.RequestParams;

var api = new VkApi();

api.Authorize(new ApiAuthParams
{
    AccessToken = "СЮДА ТОКЕН ДЛЯ ПЕЛЕНАНИЯ ЛЯЛЬКИ"
});

Console.WriteLine("Бот запущен!");

var server = api.Groups.GetLongPollServer(239555205);

while (true)
{
    var history = api.Groups.GetBotsLongPollHistory(new BotsLongPollHistoryParams
    {
        Key = server.Key,
        Server = server.Server,
        Ts = server.Ts
    });

    server.Ts = history.Ts;

    foreach (var u in history.Updates)
    {
        if (u.Type != GroupUpdateType.MessageNew)
            continue;

        var msg = u.MessageNew.Message;

        Console.WriteLine($"[{msg.PeerId}] {msg.Text}");

        var text = (msg.Text ?? "").Trim().ToLower();

        if (text == "привет")
        {
            try
            {
                api.Messages.Send(new MessagesSendParams
                {
                    PeerId = msg.PeerId,
                    RandomId = DateTime.Now.Ticks,
                    Message = "Запеленать ляльку!"
                });

                Console.WriteLine("Ответ отправлен");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ошибка отправки: " + ex.Message);
            }
        }
    }

    System.Threading.Thread.Sleep(500);
}