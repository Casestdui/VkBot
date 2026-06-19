using VkNet;
using VkNet.Model;

const long GroupId = 239555205;

const string Token = "Токен";

var api = new VkApi();

api.Authorize(new ApiAuthParams
{
    AccessToken = Token
});

Console.WriteLine("Авторизация успешна");

var pollManager = new PollManager();

var commandHandler = new CommandHandler(pollManager);

var botService = new BotService(
    api,
    commandHandler,
    pollManager,
    GroupId
);

botService.Run();
