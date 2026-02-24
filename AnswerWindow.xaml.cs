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

    /// <summary>Shows the question header immediately; call AppendToLastAnswer to stream in the answer.</summary>
    public void StartNewAnswer(string question)
    {
        AnswerTextBlock.Text += $"[{DateTime.Now:HH:mm:ss}] Q: {question}{Environment.NewLine}A: ";
        ScrollToEnd();
    }

    /// <summary>Appends a streamed answer chunk to the current answer entry.</summary>
    public void AppendToLastAnswer(string chunk)
    {
        AnswerTextBlock.Text += chunk;
        ScrollToEnd();
    }

    /// <summary>Adds trailing newlines after the answer is fully streamed.</summary>
    public void FinalizeLastAnswer()
    {
        AnswerTextBlock.Text += $"{Environment.NewLine}{Environment.NewLine}";
        ScrollToEnd();
    }

    public void AppendAnswer(string question, string answer)
    {
        AnswerTextBlock.Text += $"[{DateTime.Now:HH:mm:ss}] Q: {question}{Environment.NewLine}A: {answer}{Environment.NewLine}{Environment.NewLine}";
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(() =>
        {
            AnswerTextBlock.UpdateLayout();
            AnswerScrollViewer.UpdateLayout();
            AnswerScrollViewer.ScrollToBottom();
        }, DispatcherPriority.Render);
    }
}
