namespace QwenLocalChat.Core;

public static class HanhuaArbitration
{
    public static string? RefuseChat(bool hanhuaRunning)
        => hanhuaRunning ? "汉化任务正在运行，请先等待完成或取消后再发送。" : null;

    public static string? RefuseVideo(bool hanhuaRunning)
        => hanhuaRunning ? "汉化任务正在运行，请先等待完成或取消后再生成视频。" : null;

    public static string? RefuseHanhua(bool hanhuaRunning, bool chatGenerating, bool videoGenerating)
    {
        if (hanhuaRunning) return "已有汉化任务在运行。";
        if (chatGenerating) return "仍有聊天回复正在生成，请先在对应会话停止后再开始汉化。";
        if (videoGenerating) return "视频仍在生成，请先等待完成或取消视频任务。";
        return null;
    }
}
