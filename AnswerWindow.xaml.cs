using System;
using System.Windows;
using System.Windows.Threading;

namespace TeamsAudioCapture;

public partial class AnswerWindow : Window
{
    public AnswerWindow()
    {
        InitializeComponent();
    }

    public void AppendAnswer(string question, string answer)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] Q: {question}{Environment.NewLine}A: {answer}{Environment.NewLine}{Environment.NewLine}";
        AnswerTextBlock.Text += entry;

        Dispatcher.BeginInvoke(() =>
        {
            AnswerTextBlock.UpdateLayout();
            AnswerScrollViewer.UpdateLayout();
            AnswerScrollViewer.ScrollToBottom();
        }, DispatcherPriority.Render);
    }
}
