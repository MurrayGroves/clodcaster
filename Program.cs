using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Clodcaster;
using Clodcaster.Modules;
using Discord.Audio;

namespace Clodcaster;


public class Program
{
    private static IConfiguration _configuration;
    private static IServiceProvider _services;


    private static readonly DiscordSocketConfig _socketConfig = new()
    {
        GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers,
        AlwaysDownloadUsers = true,
    };
    
    private static readonly InteractionServiceConfig _interactionServiceConfig = new()
    {
    };

    public static async Task Main(string[] args)
    {
        var recorder = new RecorderService();
        
        _configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix: "DC_")
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        
        _services = new ServiceCollection()
            .AddSingleton(_configuration)
            .AddSingleton(_socketConfig)
            .AddSingleton(recorder)
            .AddSingleton<DiscordSocketClient>()
            .AddSingleton(x => new InteractionService(x.GetRequiredService<DiscordSocketClient>(), _interactionServiceConfig))
            .AddSingleton<InteractionHandler>()
            .BuildServiceProvider();

        var client = _services.GetRequiredService<DiscordSocketClient>();

        client.Log += LogAsync;
        client.UserVoiceStateUpdated += async (user, oldState, newState) =>
        {
            if (user.IsBot)
            {
                return;
            }
            
            // If the user was in the recording channel and left, stop recording them
            if (recorder.GetCurrentChannel() != null && oldState.VoiceChannel == recorder.GetCurrentChannel() && newState.VoiceChannel != recorder.GetCurrentChannel())
            {
                Console.WriteLine($"{(user as IGuildUser).DisplayName} left the recording channel. Stopping recording.");
                // Don't await this, we don't want to block the event handler
                recorder.StopRecording(user as IGuildUser);
            }
            
            // If the user joined the recording channel, start recording them
            if (recorder.GetCurrentChannel() != null && oldState.VoiceChannel != recorder.GetCurrentChannel() && newState.VoiceChannel == recorder.GetCurrentChannel())
            {
                Console.WriteLine($"{(user as IGuildUser).DisplayName} joined the recording channel. Starting recording.");
                // Don't await this, we don't want to block the event handler
                recorder.StartRecording(user as IGuildUser);
            }
        };

        // Here we can initialize the service that will register and execute our commands
        await _services.GetRequiredService<InteractionHandler>()
            .InitializeAsync();

        // Bot token can be provided from the Configuration object we set up earlier
        await client.LoginAsync(TokenType.Bot, _configuration["token"]);
        await client.StartAsync();
        
        Directory.CreateDirectory("Recordings/Fragments");

        // Never quit the program until manually forced to.
        await Task.Delay(Timeout.Infinite);
    }

    private static Task LogAsync(LogMessage message)
    {
        Console.WriteLine(message.ToString());
        return Task.CompletedTask;
    }
}