using Avalonia.Controls;
using Avalonia.Input;
using Kiberone.Student.ViewModels;

namespace Kiberone.Student.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel && viewModel.IsScreenLocked)
            eventArgs.Cancel = true;
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel) return;

        // Typing shortcuts belong exclusively to the active trainer. Without this
        // guard, choosing a student or answering a quiz could type into the lesson.
        if (viewModel.IsLoginVisible ||
            viewModel.IsQuizVisible ||
            viewModel.IsNotificationVisible ||
            viewModel.IsScreenLocked)
            return;

        if (eventArgs.Key == Key.Escape)
        {
            if ((viewModel.SelectedSectionIndex == 3 || viewModel.SelectedSectionIndex == 4) &&
                viewModel.IsLessonStarted &&
                !viewModel.IsFinished)
            {
                viewModel.TogglePause();
                eventArgs.Handled = true;
            }
            return;
        }

        if (viewModel.SelectedSectionIndex != 3)
            return;

        if (eventArgs.Key == Key.Back)
        {
            if (viewModel.IsLessonStarted && !viewModel.IsPaused && !viewModel.IsFinished)
            {
                viewModel.RegisterBlockedBackspace();
                eventArgs.Handled = true;
            }
            return;
        }
        if (eventArgs.Key == Key.Space && !viewModel.IsLessonStarted)
        {
            viewModel.StartLesson();
            eventArgs.Handled = true;
            return;
        }
        if (!viewModel.IsLessonStarted || viewModel.IsPaused || viewModel.IsFinished)
            return;

        if (!string.IsNullOrEmpty(eventArgs.KeySymbol) && eventArgs.KeySymbol.Length == 1)
        {
            var character = eventArgs.KeySymbol[0];
            if (char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || char.IsPunctuation(character) || char.IsSymbol(character))
            {
                viewModel.HandleCharacter(character);
                eventArgs.Handled = true;
            }
        }
    }
}
