using VkNet;
using VkNet.Model;

const long GroupId = 239555205;

const string Token = "vk1.a.vQyiAcS08_gmRAuQ_3rweRvbAjNruoMcN9qryALdJyEfs5QI_9QeLZLXuVVgG7aciiuOOlyPcY-s5RfddtlRsUDiqXOwrl9Z1uCsSdK3fAEednj5y_PjkpYaTifUdZbjKQuEo9wtox1i66JSu3eWa_NAkUqANP5W79I-nxtvu_kTmmEbKgYzHAEakaWbIB8FEA3P6RiULrGLT1sLGlU1nw";

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