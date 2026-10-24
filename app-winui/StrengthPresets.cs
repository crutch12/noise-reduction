using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using static NoiseReduction.WinUI.MicrophoneText;

namespace NoiseReduction.WinUI
{
    // Три пресета силы, точное значение в дБ остаётся за ними
    internal sealed class StrengthPresets
    {
        internal static readonly int[] Values = { 12, 24, 40 };
        private static readonly string[] Names = { "Мягко", "Обычно", "Сильно" };
        private readonly ToggleButton[] buttons = new ToggleButton[3];
        private readonly ToggleButton custom;
        private bool updating;
        internal readonly Grid View = new Grid { ColumnSpacing = 4 };
        internal event Action<int> Selected;
        internal event Action CustomClicked;
        internal bool CustomOpen;

        // withCustom добавляет кнопку Своё для точной настройки, в панели трея её нет
        internal StrengthPresets(bool withCustom = false, bool compact = false)
        {
            for (int index = 0; index < buttons.Length; index++)
            {
                int value = Values[index];
                var button = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 5) };
                // в панели трея кнопки мельче, чтобы не перевешивали название микрофона
                if (compact) { button.Padding = new Thickness(6, 1, 6, 2); button.MinHeight = 0; button.Height = 24; button.FontSize = 12; }
                button.Click += (_, _) => { if (!updating) Selected?.Invoke(value); };
                View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(button, index); View.Children.Add(button);
                buttons[index] = button;
            }
            if (!withCustom) return;
            custom = new ToggleButton { HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4, 8, 5) };
            custom.Click += (_, _) => { if (!updating) CustomClicked?.Invoke(); };
            View.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(custom, buttons.Length); View.Children.Add(custom);
        }

        internal void Localize()
        {
            for (int index = 0; index < buttons.Length; index++) { buttons[index].Content = T(Names[index]); ToolTipService.SetToolTip(buttons[index], Values[index] + " " + T("дБ")); }
            if (custom != null) custom.Content = T("Своё");
        }

        internal void Update(int value, bool enabled)
        {
            updating = true;
            bool preset = Array.IndexOf(Values, value) >= 0;
            for (int index = 0; index < buttons.Length; index++) { buttons[index].IsChecked = Values[index] == value && !CustomOpen; buttons[index].IsEnabled = enabled; }
            if (custom != null) { custom.IsChecked = CustomOpen || !preset; custom.IsEnabled = enabled; }
            updating = false;
        }
    }
}
