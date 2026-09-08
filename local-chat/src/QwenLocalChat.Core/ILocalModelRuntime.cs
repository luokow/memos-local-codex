namespace QwenLocalChat.Core;

/// <summary>Common local-only seams used by the chat and video modes.</summary>
public interface ILocalModelService : IAsyncDisposable
{
    bool OwnsModel { get; }
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
    Task StopServiceAsync(CancellationToken cancellationToken = default);
}

public interface ILocalChatClient : IDisposable
{
    Task<ChatCompletionResult> CompleteWithDetailsAsync(IReadOnlyList<ChatMessage> messages, ChatGenerationOptions options, CancellationToken cancellationToken = default);
    Task<ChatCompletionResult> CompleteStreamingAsync(IReadOnlyList<ChatMessage> messages, ChatGenerationOptions options, Action<string>? onDelta = null, CancellationToken cancellationToken = default);
}
