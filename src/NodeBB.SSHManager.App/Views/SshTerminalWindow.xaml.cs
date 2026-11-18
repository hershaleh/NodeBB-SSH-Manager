using System.Text;
using System.Windows;
using System.Windows.Input;
using Renci.SshNet;
using NodeBB.SSHManager.Services;

namespace NodeBB.SSHManager.Views;

public partial class SshTerminalWindow : Window
{
    private readonly SshServerSession _session;
    private ShellStream? _shell;
    private CancellationTokenSource? _readCts;

    public SshTerminalWindow(SshServerSession session)
    {
        InitializeComponent();
        _session = session;
        ServerLabel.Text = $"SSH · {session.Profile.Name} ({session.Profile.Host})";
        Loaded += StartTerminal;
        Closed += (_, _) =>
        {
            DiagnosticLogService.Instance.Info("Terminal", "TerminalClosed", "SSH terminal window closed.", _session.Profile.Name);
            _readCts?.Cancel();
            _shell?.Dispose();
            _readCts?.Dispose();
        };
    }

    private async void StartTerminal(object sender, RoutedEventArgs e)
    {
        try
        {
            _shell = await Task.Run(() => _session.Client.CreateShellStream("xterm", 120, 40, 1000, 700, 4096));
            _readCts = new CancellationTokenSource();
            DiagnosticLogService.Instance.Info("Terminal", "TerminalOpened", "Interactive SSH terminal opened.", _session.Profile.Name);
            InputBox.Focus();
            _ = ReadOutputAsync(_readCts.Token);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Terminal", "TerminalOpenFailed", ex, _session.Profile.Name);
            Append($"\r\n[שגיאה בפתיחת מסוף] {ex.Message}\r\n");
        }
    }

    private async Task ReadOutputAsync(CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (!token.IsCancellationRequested && _shell is not null)
            {
                var count = await _shell.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (count <= 0) break;
                var output = Encoding.UTF8.GetString(buffer, 0, count);
                await Dispatcher.InvokeAsync(() => Append(output));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Terminal", "TerminalReadFailed", ex, _session.Profile.Name);
            await Dispatcher.InvokeAsync(() => Append($"\r\n[חיבור המסוף הסתיים] {ex.Message}\r\n"));
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var command = InputBox.Text;
        InputBox.Clear();
        if (_shell is null || !_shell.CanWrite) return;
        try { _shell.WriteLine(command); _shell.Flush(); }
        catch (Exception ex)
        {
            DiagnosticLogService.Instance.Error("Terminal", "TerminalWriteFailed", ex, _session.Profile.Name);
            Append($"\r\n[שגיאת שליחה] {ex.Message}\r\n");
        }
    }

    private void Append(string value)
    {
        OutputBox.AppendText(value);
        OutputBox.ScrollToEnd();
    }
}
