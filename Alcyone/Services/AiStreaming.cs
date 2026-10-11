using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace StateMachine;

public sealed record AiStreamProgress(string Content, string Reasoning, string Status);

public static class AiStreaming
{
    public static async Task<string> ReadAsync(HttpClient client, HttpRequestMessage request, Func<AiStreamProgress, Task> progress)
    {
        try { return await ReadCoreAsync(client, request, progress); }
        catch (OperationCanceledException) { throw new InvalidOperationException("AI 请求超时（5 分钟），请检查网络或重试。"); }
    }
    private static async Task<string> ReadCoreAsync(HttpClient client, HttpRequestMessage request, Func<AiStreamProgress, Task> progress)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var clock = Stopwatch.StartNew();
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        long lastUpdate = -100;
        async Task Report(bool force = false)
        {
            if (!force && clock.ElapsedMilliseconds - lastUpdate < 100) return;
            lastUpdate = clock.ElapsedMilliseconds;
            await progress(new(content.ToString(), reasoning.ToString(),
                $"{(content.Length > 0 ? "正在接收回复" : reasoning.Length > 0 ? "正在接收思考内容" : "等待服务响应")} · {clock.Elapsed.TotalSeconds:F0} 秒 · 已接收 {content.Length + reasoning.Length} 字符"));
        }
        async Task<T> WithHeartbeat<T>(Task<T> task)
        {
            while (await Task.WhenAny(task, Task.Delay(1000, timeout.Token)) != task)
            { timeout.Token.ThrowIfCancellationRequested(); await Report(true); }
            return await task;
        }
        await Report(true);
        using var response = await WithHeartbeat(client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token));
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(timeout.Token);
            if (request.Headers.Authorization?.Parameter is { Length: > 0 } key) error = error.Replace(key, "[已隐藏]");
            throw new InvalidOperationException($"AI 请求失败 {(int)response.StatusCode}：{error[..Math.Min(error.Length, 1500)]}");
        }
        void Append(JsonElement message)
        {
            if (message.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String) content.Append(text.GetString());
            if (message.TryGetProperty("reasoning_content", out var thought) && thought.ValueKind == JsonValueKind.String) reasoning.Append(thought.GetString());
            else if (message.TryGetProperty("reasoning", out thought) && thought.ValueKind == JsonValueKind.String) reasoning.Append(thought.GetString());
        }
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            using var document = JsonDocument.Parse(await WithHeartbeat(response.Content.ReadAsStringAsync(timeout.Token)));
            Append(document.RootElement.GetProperty("choices")[0].GetProperty("message"));
        }
        else
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
            var data = new StringBuilder();
            var finished = false;
            bool ProcessEvent()
            {
                var payload = data.ToString().Trim(); data.Clear();
                if (payload.Length == 0) return false;
                if (payload == "[DONE]") { finished = true; return true; }
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("AI 流式响应返回错误，请检查服务端状态。");
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    foreach (var choice in choices.EnumerateArray())
                        if (!choice.TryGetProperty("index", out var index) || index.GetInt32() == 0)
                        {
                            if (choice.TryGetProperty("delta", out var delta)) Append(delta);
                            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                            {
                                if (reason.GetString() is "length" or "content_filter") throw new InvalidOperationException("AI 回复被截断，未应用结果，请重试或缩小需求。");
                                finished = true;
                            }
                        }
                return false;
            }
            while (true)
            {
                var line = await WithHeartbeat(reader.ReadLineAsync(timeout.Token).AsTask());
                if (line is null) { ProcessEvent(); break; }
                if (line.Length == 0) { if (ProcessEvent()) break; await Report(); }
                else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line.AsSpan(5).TrimStart()); }
            }
            if (!finished) throw new InvalidOperationException("AI 流式连接中断，未收到完整回复，请重试。");
        }
        await Report(true);
        if (content.Length == 0) throw new InvalidOperationException("AI 未返回正文，只有思考内容或空响应。");
        return content.ToString();
    }
}
