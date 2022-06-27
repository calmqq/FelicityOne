using System.Reflection;
using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using Felicity.Options;
using Felicity.Util;
using Microsoft.Extensions.Options;
using Serilog;
using IResult = Discord.Interactions.IResult;

namespace Felicity.Services.Hosted;

public class DiscordStartupService : BackgroundService
{
    private readonly LogAdapter<BaseSocketClient> _adapter;
    private readonly CommandService _commandService;
    private readonly IOptions<DiscordBotOptions> _discordBotOptions;
    private readonly DiscordShardedClient _discordShardedClient;
    private readonly InteractionService _interactionService;
    private readonly IServiceProvider _serviceProvider;
    private int _shardsReady;
    private TaskCompletionSource<bool>? _taskCompletionSource;

    public DiscordStartupService(
        DiscordShardedClient discordShardedClient,
        IOptions<DiscordBotOptions> discordBotOptions,
        InteractionService interactionService,
        CommandService commandService,
        IServiceProvider serviceProvider,
        LogAdapter<BaseSocketClient> adapter)
    {
        _discordShardedClient = discordShardedClient;
        _discordBotOptions = discordBotOptions;
        _interactionService = interactionService;
        _commandService = commandService;
        _serviceProvider = serviceProvider;
        _adapter = adapter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _discordShardedClient.Log += async logMessage =>
        {
            await _adapter.Log(logMessage);
        };

        _discordShardedClient.MessageReceived += OnMessageReceived;
        _discordShardedClient.InteractionCreated += OnInteractionCreated;
        _interactionService.SlashCommandExecuted += OnSlashCommandExecuted;

        _discordShardedClient.UserJoined += HandleJoin;
        _discordShardedClient.UserLeft += HandleLeft;

        PrepareClientAwaiter();
        await _discordShardedClient.LoginAsync(TokenType.Bot, _discordBotOptions.Value.Token);
        await _discordShardedClient.StartAsync();
        await WaitForReadyAsync(stoppingToken);

        await _commandService.AddModulesAsync(Assembly.GetEntryAssembly(), _serviceProvider);
        await _interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), _serviceProvider);

        if (BotVariables.IsDebug)
        {
            await _interactionService.RegisterCommandsToGuildAsync(_discordBotOptions.Value.LogServerId);
        }
        else
        {
            await _interactionService.RegisterCommandsGloballyAsync();
        }
    }

    private static Task OnSlashCommandExecuted(SlashCommandInfo arg1, IInteractionContext arg2, IResult result)
    {
        if (result.IsSuccess || !result.Error.HasValue)
            return Task.CompletedTask;

        var msg = $"Failed to execute command: {result.Error.GetType()}: {result.ErrorReason}";
        Log.Error(msg);

        return Task.CompletedTask;
    }

    private async Task OnMessageReceived(SocketMessage socketMessage)
    {
        if (socketMessage is not SocketUserMessage socketUserMessage)
            return;

        if (socketUserMessage.Author.IsBot)
        {
            // TODO: check if CP channel
            return;
        }

        var argPos = 0;
        if (socketUserMessage.HasStringPrefix(_discordBotOptions.Value.Prefix, ref argPos))
            return;

        if (!_discordBotOptions.Value.BotStaff.Contains(socketMessage.Author.Id))
            return;

        var context = new ShardedCommandContext(_discordShardedClient, socketUserMessage);
        await _commandService.ExecuteAsync(context, argPos, _serviceProvider);
    }

    private async Task OnInteractionCreated(SocketInteraction socketInteraction)
    {
        if (_discordBotOptions.Value.BannedUsers.Contains(socketInteraction.User.Id))
        {
            await socketInteraction.DeferAsync();
            Log.Information($"Banned user `{socketInteraction.User}` tried to run a command.");
            return;
        }

        var shardedInteractionContext = new ShardedInteractionContext(_discordShardedClient, socketInteraction);
        await _interactionService.ExecuteCommandAsync(shardedInteractionContext, _serviceProvider);
    }

    private static Task HandleJoin(SocketGuildUser arg)
    {
        return Task.CompletedTask;
    }

    private static Task HandleLeft(SocketGuild arg1, SocketUser arg2)
    {
        return Task.CompletedTask;
    }

    private void PrepareClientAwaiter()
    {
        _taskCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shardsReady = 0;

        _discordShardedClient.ShardReady += OnShardReady;
    }

    private Task OnShardReady(DiscordSocketClient discordClient)
    {
        Log.Information($"Connected as {discordClient.CurrentUser.Username}#{discordClient.CurrentUser.DiscriminatorValue}");
        BotVariables.DiscordLogChannel ??= (SocketTextChannel)discordClient.GetChannel(_discordBotOptions.Value.LogChannelId);
        
        _shardsReady++;

        if (_shardsReady != _discordShardedClient.Shards.Count)
            return Task.CompletedTask;

        _taskCompletionSource!.TrySetResult(true);
        _discordShardedClient.ShardReady -= OnShardReady;

        return Task.CompletedTask;
    }

    private Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        if (_taskCompletionSource is null)
            throw new InvalidOperationException(
                "The sharded client has not been registered correctly. Did you use ConfigureDiscordShardedHost on your HostBuilder?");

        if (_taskCompletionSource.Task.IsCompleted)
            return _taskCompletionSource.Task;

        var registration = cancellationToken.Register(
            state => { ((TaskCompletionSource<object>) state!).TrySetResult(null!); },
            _taskCompletionSource);

        return _taskCompletionSource.Task.ContinueWith(_ => registration.DisposeAsync(), cancellationToken);
    }
}