using System.Windows;
using System.Windows.Controls;
using WhisperBatch.App.Infrastructure;
using WhisperBatch.App.ViewModels;

namespace WhisperBatch.App.Views;

/// <summary>設定（Ctrl+,）: 表示言語。選ぶと画面の文字列がすぐに切り替わる。</summary>
public partial class SettingsDialog : Window
{
    private readonly AppModel _model;

    public SettingsDialog(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Render();
        LocSource.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => LocSource.Instance.PropertyChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        Choices.Children.Clear();
        foreach (var choice in _model.LanguagePreferenceChoices)
        {
            var radio = new RadioButton
            {
                Content = choice.Label,
                GroupName = "AppLanguage",
                IsChecked = _model.LanguagePreference == choice.Value,
                Margin = new Thickness(0, 2, 0, 2),
            };
            var value = choice.Value;
            radio.Checked += (_, _) =>
            {
                if (_model.LanguagePreference != value) Dispatcher.BeginInvoke(() => _model.LanguagePreference = value);
            };
            Choices.Children.Add(radio);
        }
    }
}
