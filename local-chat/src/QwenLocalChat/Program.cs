using QwenLocalChat.Core;

namespace QwenLocalChat;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var paths = AppPaths.Discover();
            if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = SelfTest.RunAsync(paths).GetAwaiter().GetResult() ? 0 : 1;
                return;
            }

            using var mutex = new Mutex(initiallyOwned: true, "Local\\QwenLocalChat-D-codex", out var createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Qwen 本地聊天窗口已经在运行。", "Qwen 本地聊天", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Application.Run(new MainForm(paths));
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Qwen 本地聊天启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }
}
