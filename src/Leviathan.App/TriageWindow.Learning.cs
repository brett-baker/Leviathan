using Leviathan.ViewModels.Triage.Learning;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

/// <summary>
/// The "Learning" bar at the right of the chip bar (triage learning plan, slice 4): how far the examples are from being enough to
/// suggest Keep or Discard. It changes when the collector finishes a file, never per key, and the counting runs off the UI thread.
/// </summary>
internal sealed partial class TriageWindow
{
    private bool _learningHooked;
    private int _progressBusy;
    private volatile bool _progressAgain;

    private void StartLearningProgress()
    {
        if (!_learning || _learningHooked)
        {
            return;
        }

        _learningHooked = true;
        LearningBox.Visibility = Visibility.Visible;
        App.Host.FeatureCollector.Changed += OnCollectorChanged;
        RequestProgress();
    }

    private void UnhookLearning()
    {
        if (_learningHooked)
        {
            _learningHooked = false;
            App.Host.FeatureCollector.Changed -= OnCollectorChanged;
        }
    }

    // Raised on a pool thread each time a file's facts are done.
    private void OnCollectorChanged(object? sender, EventArgs e) => RequestProgress();

    // One count at a time; a request that arrives during a count asks for one more, so a burst of files is a few counts, not one each.
    private void RequestProgress()
    {
        if (Interlocked.Exchange(ref _progressBusy, 1) == 1)
        {
            _progressAgain = true;
            return;
        }

        _ = RefreshProgressAsync();
    }

    private async Task RefreshProgressAsync()
    {
        try
        {
            do
            {
                _progressAgain = false;
                try
                {
                    var progress = await App.Host.Learning.ProgressAsync(_closing.Token);
                    DispatcherQueue.TryEnqueue(() => ShowProgress(progress));
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    return;
                }

                await Task.Delay(500);
            }
            while (_progressAgain && !_closing.IsCancellationRequested);
        }
        finally
        {
            Interlocked.Exchange(ref _progressBusy, 0);
        }
    }

    private void ShowProgress(LearningProgress progress)
    {
        LearningBar.Value = progress.Fraction;
        LearningText.Text = progress.Label;
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(LearningBox, progress.Detail);
    }
}
