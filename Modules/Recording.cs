using Discord;
using Discord.Interactions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Discord.Audio;
using Discord.Audio.Streams;
using Discord.WebSocket;

namespace Clodcaster.Modules;

class AudioFragment
{
    public long Start { get; set; }
    public long End { get; set; }
    public IGuildUser User { get; set; }
}

public class RecorderService
{
    private Dictionary<IGuildUser, AudioStream> _streams = new();
    private Dictionary<IGuildUser, Process> _ffmpegProcesses = new();
    private Dictionary<IGuildUser, List<AudioFragment>> _fragments = new();
    private Dictionary<IGuildUser, CancellationTokenSource> _cancellationTokens = new();
    private IGuildUser? _killingStream = null;
    private IVoiceChannel? _currentChannel = null;
    public IAudioClient? AudioClient = null;

    private static Process CreateFfmpegOut(String savePath)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $" -ac 2 -f s16le -ar 48000 -i pipe:0 -acodec pcm_u8 -ar 22050 \"{savePath}\"",
            // Minimal version for piping etc
            //Arguments = $"-c 2 -f S16_LE -r 44100",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException();
    }

    public async Task StartRecording(IGuildUser user)
    {
        var socketUser = (user as SocketGuildUser);
        var userAudioStream = (InputStream) socketUser.AudioStream;

        // Add new incomplete audio fragment for this stream
        var timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        var fragment = new AudioFragment
        {
            Start = timestamp,
            User = user
        };
        if (!_fragments.ContainsKey(user))
        {
            _fragments[user] =
            [
                fragment
            ];
        }
        else
        {
            _fragments[user].Add(fragment);
        }

        var ffmpeg = RecorderService.CreateFfmpegOut("Recordings/Fragments/" + user.DisplayName + "-" + timestamp + ".wav");
        var ffmpegOutStdinStream = ffmpeg.StandardInput.BaseStream;
        _streams[user] = userAudioStream;
        _ffmpegProcesses[user] = ffmpeg;
        var cts = new CancellationTokenSource();
        _cancellationTokens[user] = cts;
        try
        {
            var buffer = new byte[3840];
            Console.WriteLine("Starting audio stream");
            
            // Spawn new task to print ffmpeg stdout
            Task.Run(async () =>
            {
                var ffmpegBuffer = new byte[3840];
                while (await (ffmpeg.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                {
                    await Console.Out.WriteAsync(System.Text.Encoding.Default.GetString(ffmpegBuffer));
                    await Console.Out.FlushAsync();
                }
            });
            
            // Spawn new task to print ffmpeg stderr
            Task.Run(async () =>
            {
                var ffmpegBuffer = new byte[3840];
                while (await (ffmpeg.StandardError.BaseStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                {
                    await Console.Out.WriteAsync(System.Text.Encoding.Default.GetString(ffmpegBuffer));
                    await Console.Out.FlushAsync();
                }
            });

            Task.Run(async () =>
            {
                while (true)
                {
                    if (cts.Token.IsCancellationRequested)
                    {
                        await ffmpegOutStdinStream.FlushAsync();
                        ffmpegOutStdinStream.Close();
                        break;
                    }
                    await Task.Delay(100);
                }
            });

            while (await (userAudioStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
            {
                await ffmpegOutStdinStream.WriteAsync(buffer, 0, buffer.Length);
                await ffmpegOutStdinStream.FlushAsync();
            }
            cts.Dispose();
        }
        catch (Exception e)
        {
            if (cts.Token.IsCancellationRequested)
            {
                return;
            }
            
            Console.WriteLine("Error in audio stream");
            Console.WriteLine(e);
            Console.WriteLine(ffmpeg.HasExited);
            Console.WriteLine(ffmpeg.ExitCode);
            Console.WriteLine(await ffmpeg.StandardError.ReadToEndAsync());
            Console.WriteLine(await ffmpeg.StandardOutput.ReadToEndAsync());
            Console.WriteLine("Sent output");
        }
        finally
        {
            Console.WriteLine("Closing ffmpeg stream");
            ffmpegOutStdinStream.Close();
            ffmpeg.Close();
            _streams.Remove(user);
            _ffmpegProcesses.Remove(user);
            Console.WriteLine("Closed ffmpeg stream");
        }
    }

    public async Task StopRecording(IGuildUser user)
    {
        Console.WriteLine("Stopping recording for " + user.DisplayName);

        // Cancel the audio stream
        await _cancellationTokens[user].CancelAsync();
        _streams[user].Close();
        
        Console.WriteLine("Cancelled");
        
        Console.WriteLine("Closed audio stream for " + user.DisplayName);
        
        // Add the end time to the fragment
        _fragments[user].Last().End = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
        Console.WriteLine("Added end time to fragment for " + user.DisplayName);
        
        while (_ffmpegProcesses.ContainsKey(user) && !_ffmpegProcesses[user].HasExited)
        {
            await Task.Delay(100);
        }
        
        Console.WriteLine("Stopped recording for " + user.DisplayName);
    }

    public async Task StopRecordings()
    {
        // Close audio stream for each user
        foreach (var user in _streams.Keys)
        {
            await StopRecording(user);
        }

        _streams = new Dictionary<IGuildUser, AudioStream>();

        var earliestStart = _fragments.Values.SelectMany(x => x).Min(x => x.Start);
        
        foreach (var user in _fragments.Keys)
        {
            var clipNames = new List<string>();
            var currentEnd = earliestStart;
            
            // Interleave fragments with silence for correct amount of time to line up audio
            foreach (var fragment in _fragments[user])
            {
                if (fragment.Start != currentEnd)
                {
                    // Generate silence
                    var duration = fragment.Start - currentEnd;
                    var ffmpeg = Process.Start(new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-f lavfi -i anullsrc=channel_layout=2:sample_rate=48000 -t {duration}ms \"Recordings/Fragments/silence-{duration}.wav\"",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }) ?? throw new InvalidOperationException();

                    await ffmpeg.WaitForExitAsync();
                    
                    clipNames.Add($"silence-{duration}.wav");
                }
                clipNames.Add($"{user.DisplayName}-{fragment.Start}.wav");
                currentEnd = fragment.End;
            }
            
            // Write instruction file for FFMPEG
            var instructionFile = $"Recordings/Fragments/{user.DisplayName}-instructions.txt";
            await File.WriteAllLinesAsync(instructionFile, clipNames.Select(x => $"file '{x}'"));
            
            // Concatenate all clips
            var ffmpegConcat = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f concat -safe 0 -i \"{instructionFile}\" -c copy \"Recordings/{user.DisplayName}-{earliestStart}.wav\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }) ?? throw new InvalidOperationException();
            
            await ffmpegConcat.WaitForExitAsync();
            Console.WriteLine(ffmpegConcat.HasExited);
            Console.WriteLine(ffmpegConcat.ExitCode);
            Console.WriteLine(ffmpegConcat.StandardError.ReadToEnd());
        }
        
        _fragments = new Dictionary<IGuildUser, List<AudioFragment>>();
        _ffmpegProcesses = new Dictionary<IGuildUser, Process>();
        _cancellationTokens = new Dictionary<IGuildUser, CancellationTokenSource>();
        _streams = new Dictionary<IGuildUser, AudioStream>();
        _killingStream = null;
    }
    
    public void SetCurrentChannel(IVoiceChannel channel)
    {
        _currentChannel = channel;
    }
    
    public IVoiceChannel? GetCurrentChannel()
    {
        return _currentChannel;
    }
    
    public void ClearCurrentChannel()
    {
        _currentChannel = null;
    }
}

// Interaction modules must be public and inherit from an IInteractionModuleBase
public class Recording : InteractionModuleBase<SocketInteractionContext>
{
    // Dependencies can be accessed through Property injection, public properties with public setters will be set by the service provider
    public InteractionService Commands { get; set; }

    private InteractionHandler _handler;

    // Constructor injection is also a valid way to access the dependencies
    public Recording(InteractionHandler handler)
    {
        _handler = handler;
    }


    // You can use a number of parameter types in you Slash Command handlers (string, int, double, bool, IUser, IChannel, IMentionable, IRole, Enums) by default. Optionally,
    // you can implement your own TypeConverters to support a wider range of parameter types. For more information, refer to the library documentation.
    // Optional method parameters(parameters with a default value) also will be displayed as optional on Discord.

    // [Summary] lets you customize the name and the description of a parameter
    [SlashCommand("echo", "Repeat the input")]
    public async Task Echo(string echo, [Summary(description: "mention the user")] bool mention = false)
        => await RespondAsync(echo + (mention ? Context.User.Mention : string.Empty));

    [SlashCommand("ping", "Pings the bot and returns its latency.")]
    public async Task GreetUserAsync()
        => await RespondAsync(text: $":ping_pong: It took me {Context.Client.Latency}ms to respond to you!", ephemeral: true);
    // [Group] will create a command group. [SlashCommand]s and [ComponentInteraction]s will be registered with the group prefix
    [Group("recording", "Commands to control recording")]
    public class RecordingGroup : InteractionModuleBase<SocketInteractionContext>
    {
        private RecorderService _recorder;
        
        public RecordingGroup(RecorderService recorder)
        {
            _recorder = recorder;
        }
        
        private Process CreateStream(string path)
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-hide_banner -loglevel panic -i \"{path}\" -ac 2 -f s16le -ar 48000 pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
        }
        
        private async Task SendAsync(IAudioClient client, string path)
        {
            // Create FFmpeg using the previous example
            using (var ffmpeg = CreateStream(path))
            using (var output = ffmpeg.StandardOutput.BaseStream)
            using (var discord = client.CreatePCMStream(AudioApplication.Mixed))
            {
                try { await output.CopyToAsync(discord); }
                finally { await discord.FlushAsync(); }
            }
        }
        
        [SlashCommand("start", "Start a voice channel recording", runMode: RunMode.Async)]
        public async Task StartRecording(IVoiceChannel? channel = null)
        {
            // Get the audio channel
            channel = channel ?? (Context.User as IGuildUser)?.VoiceChannel;
            if (channel == null) { await RespondAsync("You must be in a voice channel, or a voice channel must be passed as an argument."); return; }

            // For the next step with transmitting audio, you would want to pass this Audio Client in to a service.
            var audioClient = await channel.ConnectAsync();
            await RespondAsync("Starting recording");
            _recorder.SetCurrentChannel(channel);
            _recorder.AudioClient = audioClient;

            await SendAsync(audioClient, "announcement.mp3");
            await _recorder.StartRecording(Context.User as IGuildUser ?? throw new InvalidOperationException());
        }
        
        [SlashCommand("stop", "Stop a voice channel recording", runMode: RunMode.Async)]
        public async Task StopRecording()
        {
            await RespondAsync("Stopping recording");
            await _recorder.AudioClient.StopAsync();
            await _recorder.StopRecordings();
            _recorder.ClearCurrentChannel();
            await FollowupAsync("Stopped recording");
        }
    }
}