using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace DynamicIsland.Services;

public sealed class GeminiLiveVoice : IAsyncDisposable
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    private const string Model = "gemini-3.8-live";

    private readonly string _apiKey;
    private readonly string _voice;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _playbackSync = new();
    private ClientWebSocket? _socket;
    private WaveIn? _microphone;
    private WaveOut? _speaker;
    private BufferedWaveProvider? _playback;
    private float _outputVolume = 1;
    private Task? _receiver;
    private int _failureReported;
    private int _stopped;
    private int _assistantSpeaking;
    private int _playbackGeneration;

    public event Action<string>? StatusChanged;
    public event Action<string>? UserTranscribed;
    public event Action<Exception>? Failed;

    public GeminiLiveVoice(string apiKey, string voice)
    {
        _apiKey = apiKey;
        _voice = voice;
    }

    public void SetOutputVolume(int percent)
    {
        _outputVolume = Math.Clamp(percent, 0, 100) / 100f;
        if (_speaker != null)
            _speaker.Volume = _outputVolume;
    }

    public static string? ResolveApiKey(string? configuredApiKey = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredApiKey))
            return configuredApiKey.Trim();

        var environmentKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(environmentKey))
            return environmentKey.Trim();

        var searchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (searchedDirectories.Add(directory.FullName))
                {
                    var kikConfig = Path.Combine(directory.FullName, "KIK-Assistant-main", "config.json");
                    var localConfig = Path.Combine(directory.FullName, "config.json");
                    foreach (var path in new[] { kikConfig, localConfig })
                    {
                        if (!File.Exists(path)) continue;

                        using var config = JsonDocument.Parse(File.ReadAllText(path));
                        if (config.RootElement.TryGetProperty("ai", out var ai) &&
                            ai.TryGetProperty("gemini_api_key", out var key) &&
                            key.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(key.GetString()))
                            return key.GetString()!.Trim();
                    }
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Не задан GEMINI_API_KEY.");

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));

        _socket = new ClientWebSocket();
        await _socket.ConnectAsync(
            new Uri($"{Endpoint}?key={Uri.EscapeDataString(_apiKey)}"),
            connectTimeout.Token);
        await SendJsonAsync(new
        {
            setup = new
            {
                model = $"models/{Model}",
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    speechConfig = new
                    {
                        languageCode = "ru-RU",
                        voiceConfig = new
                        {
                            prebuiltVoiceConfig = new { voiceName = _voice },
                        },
                    },
                },
                systemInstruction = new
                {
                    parts = new[]
                    {
                        new
                        {
                            text = "Ты — голосовой ассистент Atoll. Отвечай естественно и кратко на языке пользователя. Говори тепло и дружелюбно, с лёгкой живой эмоциональной интонацией: проявляй интерес, радуйся хорошим новостям, сочувствуй, когда уместно, иногда используй мягкий юмор. Делай естественные паузы и меняй интонацию, но не переигрывай, не восторгайся без причины и не изображай человека. Atoll умеет открывать сайты по произнесённому адресу, если это обычный веб-адрес http или https, а также YouTube и Google в браузере по умолчанию. Atoll может запускать установленные Steam, Roblox, Spotify, Telegram и Discord; если Discord, Telegram или Spotify не найдены или не запускаются, открывает их веб-версии, а для Steam пытается открыть клиент и затем сайт Steam. Если пользователь явно просит открыть сайт или запустить приложение, кратко подтверди команду. Не говори, что не умеешь открывать веб-сайты или перечисленные приложения. Не утверждай, что уже открыл приложение: Atoll запускает его отдельно и может сообщить, если не удалось открыть. Любые другие действия на компьютере ты выполнять не можешь; не заявляй, что выполнил их.",
                        },
                    },
                },
                inputAudioTranscription = new { },
            },
        }, connectTimeout.Token);

        using (var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectTimeout.Token))
        {
            setupTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                using var setup = await ReceiveJsonAsync(setupTimeout.Token);
                if (setup.RootElement.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"Gemini Live: {error}");
                if (setup.RootElement.TryGetProperty("setupComplete", out _))
                    break;
            }
        }

        _playback = new BufferedWaveProvider(
            new WaveFormat(24_000, 16, 1),
            TimeSpan.FromMinutes(2))
        {
            DiscardOnBufferOverflow = false,
        };
        _speaker = new WaveOut();
        _speaker.Volume = _outputVolume;
        _speaker.Init(_playback);
        _speaker.Play();

        _microphone = new WaveIn
        {
            WaveFormat = new WaveFormat(16_000, 16, 1),
            BufferMilliseconds = 64,
            NumberOfBuffers = 3,
        };
        _microphone.DataAvailable += Microphone_DataAvailable;
        _microphone.StartRecording();

        _receiver = ReceiveLoopAsync(_stop.Token);
        StatusChanged?.Invoke("Слушаю…");
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        if (!_stop.IsCancellationRequested)
            _stop.Cancel();

        _socket?.Abort();
        if (_microphone != null)
        {
            _microphone.DataAvailable -= Microphone_DataAvailable;
            _microphone.StopRecording();
            _microphone.Dispose();
            _microphone = null;
        }

        if (_speaker != null)
        {
            _speaker.Stop();
            _speaker.Dispose();
            _speaker = null;
        }

        if (_receiver != null)
        {
            await _receiver;
            _receiver = null;
        }

        _socket?.Dispose();
        _socket = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async void Microphone_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_stop.IsCancellationRequested ||
            Volatile.Read(ref _assistantSpeaking) != 0 ||
            e.BytesRecorded == 0) return;

        try
        {
            var audio = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
            if (Volatile.Read(ref _assistantSpeaking) != 0) return;
            await SendJsonAsync(new
            {
                realtimeInput = new
                {
                    audio = new
                    {
                        mimeType = "audio/pcm;rate=16000",
                        data = audio,
                    },
                },
            }, _stop.Token);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ReportFailure(ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = await ReceiveJsonAsync(cancellationToken);
                var root = message.RootElement;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException($"Gemini Live: {error}");

                if (root.TryGetProperty("serverContent", out var content))
                {
                    if (content.TryGetProperty("modelTurn", out var turn) &&
                        turn.TryGetProperty("parts", out var parts))
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("inlineData", out var inlineData) &&
                                inlineData.TryGetProperty("data", out var audioData))
                            {
                                var bytes = Convert.FromBase64String(audioData.GetString() ?? "");
                                lock (_playbackSync)
                                {
                                    Interlocked.Increment(ref _playbackGeneration);
                                    Volatile.Write(ref _assistantSpeaking, 1);
                                    _playback?.AddSamples(bytes, 0, bytes.Length);
                                }
                                StatusChanged?.Invoke("Говорю…");
                            }
                        }
                    }

                    if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
                    {
                        lock (_playbackSync)
                        {
                            _playback?.ClearBuffer();
                            Volatile.Write(ref _assistantSpeaking, 0);
                        }
                        StatusChanged?.Invoke("Слушаю…");
                    }
                    else
                    {
                        if (content.TryGetProperty("inputTranscription", out var transcription) &&
                            transcription.TryGetProperty("text", out var heard) &&
                            !string.IsNullOrWhiteSpace(heard.GetString()))
                            UserTranscribed?.Invoke(heard.GetString()!);

                        if (content.TryGetProperty("turnComplete", out var complete) && complete.GetBoolean())
                            _ = ResumeMicrophoneAfterPlaybackAsync(cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                ReportFailure(ex);
        }
    }

    private async Task ResumeMicrophoneAfterPlaybackAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var generation = Volatile.Read(ref _playbackGeneration);
                if ((_playback?.BufferedBytes ?? 0) > 0)
                {
                    await Task.Delay(60, cancellationToken);
                    continue;
                }

                await Task.Delay(400, cancellationToken);
                lock (_playbackSync)
                {
                    if ((_playback?.BufferedBytes ?? 0) != 0 ||
                        generation != Volatile.Read(ref _playbackGeneration))
                        continue;

                    Volatile.Write(ref _assistantSpeaking, 0);
                }

                StatusChanged?.Invoke("Слушаю…");
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendJsonAsync(object message, CancellationToken cancellationToken)
    {
        if (_socket?.State != WebSocketState.Open)
            throw new WebSocketException("Gemini Live соединение закрыто.");

        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<JsonDocument> ReceiveJsonAsync(CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new WebSocketException("Gemini Live соединение не открыто.");
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Gemini Live закрыл соединение.");
            message.Write(buffer, 0, result.Count);
            if (message.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Слишком большой ответ Gemini Live.");
        } while (!result.EndOfMessage);

        return JsonDocument.Parse(message.ToArray());
    }

    private void ReportFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) == 0)
            Failed?.Invoke(exception);
    }
}
